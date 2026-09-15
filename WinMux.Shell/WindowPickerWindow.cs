using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Platform;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// Pick a window that is already open and take it into a pane.
///
/// Listed in z-order, so whatever the user was last looking at is at the top. Refreshing is
/// explicit and cheap (9 ms measured) because the desktop changes while this is open — and a list
/// that reshuffled itself under the pointer would be worse than a stale one.
/// </summary>
/// <summary>
/// A window being dragged towards a pane.
///
/// A reference type only because <c>DataFormat&lt;T&gt;</c> demands one, and
/// <see cref="AdoptableWindow"/> is a struct.
/// </summary>
internal sealed record DraggedWindow(AdoptableWindow Window);

internal sealed class WindowPickerWindow : Window
{
    /// <summary>
    /// The format a dragged window travels in.
    ///
    /// In-process and private, not text: the payload never leaves WinMux, dropping one of these
    /// anywhere but a pane does nothing, and a text payload would be pasted into the first editor
    /// it landed on.
    /// </summary>
    public static readonly DataFormat<DraggedWindow> WindowDragFormat =
        DataFormat.CreateInProcessFormat<DraggedWindow>("winmux-adoptable-window");

    private readonly IWindowCatalog _catalog;
    private readonly IReadOnlyCollection<WindowHandle> _exclude;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly bool _draggable;

    /// <summary>The chosen window, or null if the user backed out.</summary>
    public AdoptableWindow? Result { get; private set; }

    /// <param name="draggable">
    /// True for the modeless tray, whose rows can be dragged onto a pane. A modal dialog cannot
    /// offer this: it makes its owner uninteractive, so there is nothing to drag onto.
    /// </param>
    public WindowPickerWindow(
        IWindowCatalog catalog,
        IReadOnlyCollection<WindowHandle> exclude,
        bool draggable = false)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _exclude = exclude ?? throw new ArgumentNullException(nameof(exclude));
        _draggable = draggable;

        Title = draggable ? "Open windows" : "Attach an open window";
        Width = 560;
        Height = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

        _list = new ListBox { CornerRadius = Palette.ControlRadius, Background = Palette.SurfaceBrush };
        _list.DoubleTapped += (_, _) => Accept();
        if (draggable) EnableDragOut();

        _status = new TextBlock
        {
            Foreground = Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var refresh = new Button
        {
            Content = "Refresh",
            Classes = { Chrome.Theme.DialogButton },
        };
        refresh.Click += (_, _) => Render();

        // The tray has no "Attach": there is no pane it would attach *to*, because the point of it
        // is that you choose the pane by dropping on one. A button that did nothing would be worse
        // than no button.
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        if (draggable)
        {
            var close = new Button
            {
                Content = "Close",
                IsCancel = true,
                Classes = { Chrome.Theme.DialogButton },
            };
            close.Click += (_, _) => Close();
            rightButtons.Children.Add(close);
        }
        else
        {
            var attach = new Button
            {
                Content = "Attach",
                IsDefault = true,
                Classes = { Chrome.Theme.DialogButton },
            };
            attach.Click += (_, _) => Accept();

            var cancel = new Button
            {
                Content = "Cancel",
                IsCancel = true,
                Classes = { Chrome.Theme.DialogButton },
            };
            cancel.Click += (_, _) => Close();

            rightButtons.Children.Add(cancel);
            rightButtons.Children.Add(attach);
        }

        var actions = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(refresh, Dock.Left);
        DockPanel.SetDock(rightButtons, Dock.Right);
        actions.Children.Add(refresh);
        actions.Children.Add(rightButtons);

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = actions,
        };

        var caption = new TextBlock
        {
            Text = draggable
                ? "Drag a window onto a pane to put it there. Closing that pane detaches it and puts " +
                  "it back where it was — the application keeps running either way."
                : "WinMux will take this window into the pane. Closing the pane detaches it and " +
                  "puts it back where it was — the application keeps running either way.",
            Foreground = Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 14), LastChildFill = true };
        DockPanel.SetDock(caption, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(caption);
        body.Children.Add(_status);
        body.Children.Add(_list);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += (_, _) => Render();
    }

    private void Render()
    {
        var windows = _catalog.List(_exclude, out var error);

        _list.ItemsSource = windows.Select(window => new ListBoxItem
        {
            Tag = window,
            DataContext = window,
            Content = new StackPanel
            {
                Margin = new Thickness(2),
                Children =
                {
                    new TextBlock
                    {
                        Text = window.Title,
                        Foreground = Palette.TextBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = window.ProcessName.Length > 0
                            ? $"{window.ProcessName}  ({window.ProcessId})"
                            : $"process {window.ProcessId}",
                        FontSize = Palette.CaptionSize,
                        Foreground = Palette.FaintTextBrush,
                    },
                },
            },
        }).ToArray();

        if (windows.Count > 0) _list.SelectedIndex = 0;
        _status.Text = windows.Count == 0
            ? "no windows available to attach"
            : $"{windows.Count} window(s), most recently used first" + (error is null ? "" : "; " + error);
    }

    /// <summary>
    /// Let a row be dragged out of the list and dropped on a pane.
    ///
    /// This is as close to "drag a running window into a pane" as Windows allows. Dragging the
    /// application's own window off the desktop and onto WinMux is not available to us: Windows
    /// delivers a window-move to the window being moved, not to whatever it passes over, so there
    /// is no drop for us to receive. Dragging its entry out of this list is the same gesture over
    /// something we do own.
    ///
    /// Started from the press rather than from a movement threshold because that is what
    /// <see cref="DragDrop.DoDragDropAsync"/> takes. Pressing and releasing without moving ends the
    /// drag with no effect, so a plain click still selects the row.
    /// </summary>
    private void EnableDragOut()
    {
        _list.AddHandler(PointerPressedEvent, async (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
            if (Row(e.Source) is not { } window) return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(WindowDragFormat, new DraggedWindow(window)));
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The window a pointer event landed on, by walking up to the row that carries it.</summary>
    private static AdoptableWindow? Row(object? source)
    {
        for (var visual = source as Control; visual is not null; visual = visual.Parent as Control)
        {
            if (visual.DataContext is AdoptableWindow window) return window;
            if (visual is ListBoxItem { Tag: AdoptableWindow tagged }) return tagged;
        }

        return null;
    }

    private void Accept()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is AdoptableWindow window)
        {
            Result = window;
            if (!_draggable) Close();
        }
    }
}
