using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
internal sealed class WindowPickerWindow : Window
{
    private readonly IWindowCatalog _catalog;
    private readonly IReadOnlyCollection<WindowHandle> _exclude;
    private readonly ListBox _list;
    private readonly TextBlock _status;

    /// <summary>The chosen window, or null if the user backed out.</summary>
    public AdoptableWindow? Result { get; private set; }

    public WindowPickerWindow(IWindowCatalog catalog, IReadOnlyCollection<WindowHandle> exclude)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _exclude = exclude ?? throw new ArgumentNullException(nameof(exclude));

        Title = "Attach an open window";
        Width = 560;
        Height = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

        _list = new ListBox { CornerRadius = Palette.ControlRadius, Background = Palette.SurfaceBrush };
        _list.DoubleTapped += (_, _) => Accept();

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

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightButtons.Children.Add(cancel);
        rightButtons.Children.Add(attach);

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
            Text = "WinMux will take this window into the pane. Closing the pane detaches it and " +
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

    private void Accept()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is AdoptableWindow window)
        {
            Result = window;
            Close();
        }
    }
}
