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
    private const int IconPixels = 32;

    private readonly IAppCatalog _catalog;
    private readonly IAppIconSource _icons;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private IReadOnlyList<InstalledApp> _apps = [];

    /// <summary>Cancels the icon fill for a render that has been superseded by the next keystroke.</summary>
    private CancellationTokenSource? _iconFill;

    /// <summary>The chosen application, or null if the user backed out.</summary>
    public InstalledApp? Result { get; private set; }

    public AppPickerWindow(IAppCatalog catalog, IAppIconSource icons)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _icons = icons ?? throw new ArgumentNullException(nameof(icons));

        Title = "Choose an application";
        Width = 560;
        Height = 560;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

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
            Classes = { Chrome.Theme.DialogButton },
        };
        browse.Click += (_, _) => _ = BrowseAsync();

        var choose = new Button
        {
            Content = "Choose",
            IsDefault = true,
            Classes = { Chrome.Theme.DialogButton },
        };
        choose.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Classes = { Chrome.Theme.DialogButton },
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
        _iconFill?.Cancel();
        _iconFill?.Dispose();
        _iconFill = new CancellationTokenSource();

        var query = _search.Text?.Trim() ?? string.Empty;
        var matches = _apps
            .Where(app => query.Length == 0 || Matches(app, query))
            .Take(400)
            .ToArray();

        var items = new List<Control>();
        var rows = new List<(InstalledApp App, Image Icon)>();

        if (query.Length == 0)
        {
            // Grouped by the folder the Start Menu files it under, which is the grouping the
            // machine's owner and its installers already chose. Uncategorised first: those are the
            // applications sitting directly under Programs, which is where the well-known ones are.
            foreach (var group in matches
                .GroupBy(app => app.Category)
                .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                if (group.Key.Length > 0) items.Add(Header(group.Key));
                foreach (var app in group) items.Add(Row(app, rows));
            }
        }
        else
        {
            // While filtering, headings get in the way: the answer is usually the first row, and
            // scattering it under a heading per application is noise rather than structure.
            foreach (var app in matches) items.Add(Row(app, rows));
        }

        _list.ItemsSource = items;
        SelectFirstApp();

        _status.Text = _apps.Count == 0
            ? "no applications found"
            : $"{matches.Length} of {_apps.Count} applications";

        _ = FillIconsAsync(rows, _iconFill.Token);
    }

    /// <summary>A category heading: visible, and not something the arrow keys can land on.</summary>
    private static Control Header(string text) => new ListBoxItem
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = Palette.CaptionSize,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.MutedTextBrush,
            Margin = new Thickness(2, 10, 2, 2),
        },
        IsEnabled = false,
        Focusable = false,
    };

    private static ListBoxItem Row(InstalledApp app, List<(InstalledApp App, Image Icon)> rows)
    {
        var icon = new Image
        {
            Width = IconPixels,
            Height = IconPixels,
            Margin = new Thickness(2, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        rows.Add((app, icon));

        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = app.Name, Foreground = Palette.TextBrush },
                new TextBlock
                {
                    Text = app.Program,
                    FontSize = Palette.CaptionSize,
                    Foreground = Palette.FaintTextBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        var row = new DockPanel { Margin = new Thickness(2) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(text);

        return new ListBoxItem { Tag = app, Content = row };
    }

    /// <summary>
    /// Fill the icons after the list is on screen.
    ///
    /// Extracting a few hundred icons out of a few hundred executables is far too slow to do before
    /// showing anything — the dialog would sit blank for a second every time a key is pressed. The
    /// rows appear immediately with the space reserved, and the pictures arrive into it. Cancelled
    /// on the next keystroke, because filling a list nobody is looking at any more is pure cost.
    /// </summary>
    private async Task FillIconsAsync(List<(InstalledApp App, Image Icon)> rows, CancellationToken token)
    {
        if (!_icons.IsAvailable || rows.Count == 0) return;

        try
        {
            foreach (var (app, image) in rows)
            {
                if (token.IsCancellationRequested) return;

                // 48, not 256. The shell's jumbo list pads an application that has no large
                // rendition into a 256-square, so asking for the biggest and drawing it at 32
                // makes those icons appear about eight pixels across, adrift in their box.
                var png = await Task.Run(() => _icons.GetIconPng(app.Program, 48), token);
                if (png is null || token.IsCancellationRequested) continue;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    using var stream = new MemoryStream(png);
                    try
                    {
                        image.Source = new Avalonia.Media.Imaging.Bitmap(stream);
                    }
                    catch (Exception)
                    {
                        // A picture that will not decode is not worth a message; the row keeps its
                        // name and its path, which is what the user is reading anyway.
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Select the first real application, stepping over any heading above it.</summary>
    private void SelectFirstApp()
    {
        for (var index = 0; index < _list.ItemCount; index++)
        {
            if (ItemAt(index)?.Tag is InstalledApp)
            {
                _list.SelectedIndex = index;
                return;
            }
        }

        _list.SelectedIndex = -1;
    }

    private ListBoxItem? ItemAt(int index) =>
        (_list.ItemsSource as IReadOnlyList<Control>)?.ElementAtOrDefault(index) as ListBoxItem;

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

        // Step over category headings rather than landing on one: they are not choices, and a
        // selection that stops on a heading makes Enter do nothing for no visible reason.
        var step = e.Key == Key.Down ? 1 : -1;
        for (var next = _list.SelectedIndex + step; next >= 0 && next < _list.ItemCount; next += step)
        {
            if (ItemAt(next)?.Tag is not InstalledApp) continue;
            _list.SelectedIndex = next;
            _list.ScrollIntoView(next);
            return;
        }
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
