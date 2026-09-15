using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// Create or edit one profile.
///
/// The window-matching fields are only shown for an application, because they mean nothing for a
/// terminal and a form that shows inapplicable controls teaches people to ignore it. They are also
/// optional: left empty, the shipped quirks database decides how to find and host the window
/// (ADR 0003), which is the right answer for anything it already knows about.
/// </summary>
internal sealed class ProfileEditorWindow : Window
{
    private readonly LaunchProfile _original;
    private readonly TextBox _name;
    private readonly ComboBox _kind;
    private readonly TextBox _program;
    private readonly TextBox _args;
    private readonly TextBox _cwd;
    private readonly ComboBox _strategy;
    private readonly TextBox _windowClass;
    private readonly TextBox _titleContains;
    private readonly StackPanel _appOnly;

    /// <summary>The edited profile, or null if the user backed out.</summary>
    public LaunchProfile? Result { get; private set; }

    public ProfileEditorWindow(LaunchProfile profile)
    {
        _original = profile ?? throw new ArgumentNullException(nameof(profile));

        Title = string.IsNullOrWhiteSpace(profile.Name) ? "New profile" : "Edit " + profile.Name;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

        _name = Field(profile.Name, "Shown in every menu that opens a pane");
        _program = Field(profile.Program, @"e.g. pwsh.exe or C:\Program Files\App\app.exe");
        _args = Field(string.Join(" ", profile.Args), "Separated by spaces");
        _cwd = Field(profile.WorkingDirectory, "Empty inherits from the focused pane");
        _windowClass = Field(profile.WindowClass, "Optional — only if the quirks database gets it wrong");
        _titleContains = Field(profile.TitleContains, "Optional — for an app with several windows");

        _kind = Choice([("Terminal", ProfileKind.Terminal), ("Application", ProfileKind.Application)], profile.Kind);
        _kind.SelectionChanged += (_, _) => UpdateKindVisibility();

        _strategy = Choice(
            [("Automatic", HostStrategy.Auto), ("Embed in the pane", HostStrategy.Embed), ("Follow the pane", HostStrategy.Attach)],
            profile.Strategy);

        var browse = new Button
        {
            Content = "Browse\u2026",
            Padding = new Thickness(12, 6),
            CornerRadius = Palette.ControlRadius,
            Margin = new Thickness(8, 0, 0, 0),
        };
        browse.Click += (_, _) => _ = BrowseAsync();

        var programRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Right);
        programRow.Children.Add(browse);
        programRow.Children.Add(_program);

        _appOnly = new StackPanel { Spacing = 6 };
        _appOnly.Children.Add(Section("Window"));
        _appOnly.Children.Add(Row("Hosting", _strategy));
        _appOnly.Children.Add(Row("Window class", _windowClass));
        _appOnly.Children.Add(Row("Title contains", _titleContains));

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Spacing = 6 };
        body.Children.Add(Row("Name", _name));
        body.Children.Add(Row("Opens", _kind));
        body.Children.Add(Row("Program", programRow));
        body.Children.Add(Row("Arguments", _args));
        body.Children.Add(Row("Start in", _cwd));
        body.Children.Add(_appOnly);

        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            Classes = { Chrome.Theme.DialogButton },
        };
        save.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Classes = { Chrome.Theme.DialogButton },
        };
        cancel.Click += (_, _) => Close();

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, save },
            },
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
        UpdateKindVisibility();
    }

    private void UpdateKindVisibility() =>
        _appOnly.IsVisible = Selected<ProfileKind>(_kind) == ProfileKind.Application;

    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program or shortcut",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Programs and shortcuts") { Patterns = ["*.exe", "*.lnk"] },
            ],
        });
        if (picked.Count == 0) return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        // A shortcut is resolved here rather than stored as-is: PaneHost launches with CreateProcess,
        // which cannot run a .lnk at all.
        if (PlatformServices.Apps.Resolve(path, out _) is { } resolved)
        {
            _program.Text = resolved.Program;
            if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = resolved.Name;
            if (string.IsNullOrWhiteSpace(_args.Text)) _args.Text = resolved.Arguments;
            if (string.IsNullOrWhiteSpace(_cwd.Text)) _cwd.Text = resolved.WorkingDirectory;
        }
        else
        {
            _program.Text = path;
        }
    }

    private void Accept()
    {
        var name = _name.Text?.Trim() ?? string.Empty;
        var program = _program.Text?.Trim() ?? string.Empty;
        if (name.Length == 0 || program.Length == 0)
        {
            // Refuse rather than save something that cannot open. The two required fields are the
            // two the list and the launcher both need.
            _name.BorderBrush = name.Length == 0 ? Palette.DangerBrush : Palette.EdgeBrush;
            _program.BorderBrush = program.Length == 0 ? Palette.DangerBrush : Palette.EdgeBrush;
            return;
        }

        Result = _original with
        {
            Id = string.IsNullOrWhiteSpace(_original.Id) ? LaunchProfile.MakeId(name) : _original.Id,
            Name = name,
            Kind = Selected<ProfileKind>(_kind),
            Program = program,
            Args = (_args.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
            WorkingDirectory = _cwd.Text?.Trim() ?? string.Empty,
            Strategy = Selected<HostStrategy>(_strategy),
            WindowClass = _windowClass.Text?.Trim() ?? string.Empty,
            TitleContains = _titleContains.Text?.Trim() ?? string.Empty,
        };
        Close();
    }

    private static TextBox Field(string value, string watermark) => new()
    {
        Text = value,
        PlaceholderText = watermark,
        CornerRadius = Palette.ControlRadius,
    };

    private static Control Section(string text) => SettingsCard.Heading(text);

    private static Control Row(string label, Control control)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 2),
            ColumnDefinitions = [new ColumnDefinition(130, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star)],
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.TextBrush,
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private static ComboBox Choice<T>((string Label, T Value)[] options, T current)
    {
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = Palette.ControlRadius,
            ItemsSource = options.Select(o => new ComboBoxItem { Content = o.Label, Tag = o.Value }).ToArray(),
        };
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => Equals(o.Value, current)));
        return box;
    }

    private static T Selected<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem!).Tag!;
}
