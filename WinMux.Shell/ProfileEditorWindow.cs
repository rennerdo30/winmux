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
    private readonly StackPanel _connectionOnly;
    private readonly StackPanel _programOnly;
    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly TextBox _user;
    private readonly TextBox _identity;

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

        _kind = Choice(
            [
                ("Terminal", ProfileKind.Terminal),
                ("Application", ProfileKind.Application),
                ("SSH connection", ProfileKind.Ssh),
                ("Remote Desktop connection", ProfileKind.Rdp),
                ("SFTP connection (file browser)", ProfileKind.Sftp),
                ("FTP connection (file browser)", ProfileKind.Ftp),
            ],
            profile.Kind);
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

        _host = Field(profile.Host, "server.example.com");
        _port = Field(profile.Port > 0 ? profile.Port.ToString() : string.Empty, "default for the protocol");
        _user = Field(profile.User, "user name");
        _identity = Field(profile.Identity, "private key file (optional)");

        _connectionOnly = new StackPanel { Spacing = 6 };
        _connectionOnly.Children.Add(Section("Connection"));
        _connectionOnly.Children.Add(Row("Host", _host));
        _connectionOnly.Children.Add(Row("Port", _port));
        _connectionOnly.Children.Add(Row("User", _user));
        _connectionOnly.Children.Add(Row("Identity file", _identity));
        _connectionOnly.Children.Add(new TextBlock
        {
            // "Both clients" was written when there were two kinds. There are four, and two of them
            // are WinMux's own code rather than a Windows client, so the sentence has to say what
            // is actually true of all of them.
            Text = "No password is kept in any file WinMux owns. SSH uses your agent and keys, "
                 + "Remote Desktop uses its own client, and SFTP and FTP ask once and offer to save "
                 + "the password in Windows Credential Manager. 'Start in' is the folder a "
                 + "connection opens in — a remote path such as /srv/www for SFTP and FTP.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = Palette.CaptionSize,
            Foreground = Palette.MutedTextBrush,
            Margin = new Thickness(0, 4, 0, 0),
        });

        _appOnly = new StackPanel { Spacing = 6 };
        _appOnly.Children.Add(Section("Window"));
        _appOnly.Children.Add(Row("Hosting", _strategy));
        _appOnly.Children.Add(Row("Window class", _windowClass));
        _appOnly.Children.Add(Row("Title contains", _titleContains));

        // A connection derives its program from the host, so the program field would be a box
        // whose contents are ignored — and a control that does nothing is worse than no control.
        _programOnly = new StackPanel { Spacing = 6 };
        _programOnly.Children.Add(Row("Program", programRow));

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Spacing = 6 };
        body.Children.Add(Row("Name", _name));
        body.Children.Add(Row("Opens", _kind));
        body.Children.Add(_programOnly);
        body.Children.Add(_connectionOnly);
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

    private void UpdateKindVisibility()
    {
        var kind = Selected<ProfileKind>(_kind);
        var connection = RemoteConnection.IsConnection(kind);

        _connectionOnly.IsVisible = connection;
        _programOnly.IsVisible = !connection;

        // RDP is hosted like any other window, so its hosting options apply; SSH is a terminal.
        _appOnly.IsVisible = kind is ProfileKind.Application or ProfileKind.Rdp;
    }

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
        var host = _host.Text?.Trim() ?? string.Empty;
        var connection = RemoteConnection.IsConnection(Selected<ProfileKind>(_kind));

        // A connection needs a host where everything else needs a program; both need a name.
        // Refuse rather than save something that cannot open, and mark the field that is missing
        // rather than reporting a generic failure.
        var missingTarget = connection ? host.Length == 0 : program.Length == 0;
        if (name.Length == 0 || missingTarget)
        {
            _name.BorderBrush = name.Length == 0 ? Palette.DangerBrush : Palette.EdgeBrush;
            _program.BorderBrush = !connection && program.Length == 0 ? Palette.DangerBrush : Palette.EdgeBrush;
            _host.BorderBrush = connection && host.Length == 0 ? Palette.DangerBrush : Palette.EdgeBrush;
            return;
        }

        Result = _original with
        {
            Id = string.IsNullOrWhiteSpace(_original.Id) ? LaunchProfile.MakeId(name) : _original.Id,
            Name = name,
            Kind = Selected<ProfileKind>(_kind),
            Program = program,
            Host = host,
            Port = int.TryParse(_port.Text?.Trim(), out var port) && port is > 0 and <= 65535 ? port : 0,
            User = _user.Text?.Trim() ?? string.Empty,
            Identity = _identity.Text?.Trim() ?? string.Empty,
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
