using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WinMux.Core.Settings;
using WinMux.Platform;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// Pick an application to put in a pane.
///
/// The Start Menu is the list, because a power user should not have to know where an executable
/// lives to run it here (CLAUDE.md section 5a). Typing filters; Enter takes the top match; browsing
/// is there for anything the Start Menu does not know about, including a shortcut, which is
/// resolved rather than handed to a process that cannot launch it.
/// </summary>
internal sealed class AppPickerWindow : Window
{
    private readonly IAppCatalog _catalog;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private IReadOnlyList<InstalledApp> _apps = [];

    /// <summary>The chosen application, or null if the user backed out.</summary>
    public InstalledApp? Result { get; private set; }

    public AppPickerWindow(IAppCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        Title = "Choose an application";
        Width = 560;
        Height = 560;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;

        _search = new TextBox
        {
            PlaceholderText = "Search installed applications",
            CornerRadius = Palette.ControlRadius,
            Margin = new Thickness(0, 0, 0, 10),
        };
        _search.TextChanged += (_, _) => Render();
        _search.KeyDown += OnSearchKey;

        _list = new ListBox { CornerRadius = Palette.ControlRadius, Background = Palette.SurfaceBrush };
        _list.DoubleTapped += (_, _) => Accept();

        _status = new TextBlock { Foreground = Palette.MutedTextBrush, Margin = new Thickness(0, 8, 0, 0) };

        var browse = new Button
        {
            Content = "Browse…",
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
        };
        browse.Click += (_, _) => _ = BrowseAsync();

        var choose = new Button
        {
            Content = "Choose",
            IsDefault = true,
            MinWidth = 96,
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
        };
        choose.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 96,
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
        };
        cancel.Click += (_, _) => Close();

        var actions = new DockPanel { LastChildFill = false };
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightButtons.Children.Add(cancel);
        rightButtons.Children.Add(choose);
        DockPanel.SetDock(browse, Dock.Left);
        DockPanel.SetDock(rightButtons, Dock.Right);
        actions.Children.Add(browse);
        actions.Children.Add(rightButtons);

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = actions,
        };

        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 14), LastChildFill = true };
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        body.Children.Add(_search);
        body.Children.Add(_status);
        body.Children.Add(_list);

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opened += (_, _) => { _search.Focus(); _ = LoadAsync(); };
    }

    /// <summary>
    /// Reading a few hundred shortcuts takes a couple of hundred milliseconds, which is a visible
    /// stutter if it happens on the UI thread while the dialog is opening.
    /// </summary>
    private async Task LoadAsync()
    {
        _status.Text = "reading the Start Menu…";
        string? error = null;
        var apps = await Task.Run(() => _catalog.List(out error));

        Dispatcher.UIThread.Post(() =>
        {
            _apps = apps;
            Render();
            if (error is not null) _status.Text = "some entries could not be read: " + error;
        });
    }

    private void Render()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        var matches = _apps
            .Where(app => query.Length == 0 || Matches(app, query))
            .Take(400)
            .ToArray();

        _list.ItemsSource = matches.Select(app => new ListBoxItem
        {
            Tag = app,
            Content = new StackPanel
            {
                Margin = new Thickness(2),
                Children =
                {
                    new TextBlock { Text = app.Name, Foreground = Palette.TextBrush },
                    new TextBlock
                    {
                        Text = app.Program,
                        FontSize = 11,
                        Foreground = Palette.FaintTextBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        }).ToArray();

        if (matches.Length > 0) _list.SelectedIndex = 0;
        _status.Text = _apps.Count == 0
            ? "no applications found"
            : $"{matches.Length} of {_apps.Count} applications";
    }

    /// <summary>
    /// Every word must appear somewhere, so "code ins" finds "Visual Studio Code (Insiders)" without
    /// requiring the user to remember the order.
    /// </summary>
    private static bool Matches(InstalledApp app, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word =>
                app.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                app.Program.Contains(word, StringComparison.OrdinalIgnoreCase));

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        // Arrows move through the list while the caret stays in the box: typing then picking should
        // never need the mouse or a Tab.
        if (e.Key is not (Key.Down or Key.Up)) return;
        e.Handled = true;
        if (_list.ItemCount == 0) return;
        var next = _list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
        _list.SelectedIndex = Math.Clamp(next, 0, _list.ItemCount - 1);
        _list.ScrollIntoView(_list.SelectedIndex);
    }

    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program or shortcut",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Programs and shortcuts")
                {
                    Patterns = ["*.exe", "*.lnk"],
                },
            ],
        });

        if (picked.Count == 0) return;
        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) { _status.Text = "that is not a local file"; return; }

        var resolved = _catalog.Resolve(path, out var error);
        if (resolved is null) { _status.Text = error ?? "that file cannot be launched"; return; }

        Result = resolved;
        Close();
    }

    private void Accept()
    {
        if ((_list.SelectedItem as ListBoxItem)?.Tag is InstalledApp app)
        {
            Result = app;
            Close();
        }
    }
}
