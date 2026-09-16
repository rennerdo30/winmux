using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Core.Layout;
using WinMux.Core.Settings;
using WinMux.Core.Update;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// The settings dialog.
///
/// Every control here changes something visible, and nothing is shown that WinMux does not actually
/// honour: a settings page full of inert switches is a worse lie than having no settings page. What
/// cannot be changed yet — where the session lives, which quirks file is loaded, the version — is
/// shown as information, clearly not as a control.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _theme;
    private readonly ListBox _profileList;
    private readonly ComboBox _terminal;
    private readonly List<LaunchProfile> _profiles;
    private string _pendingDefaultTerminal = string.Empty;
    private readonly ComboBox _tabPlacement;
    private readonly CheckBox _confirmClosing;
    private readonly TextBox _fontFamily;
    private readonly NumericUpDown _fontSize;
    private readonly CheckBox _checkUpdates;
    private readonly ComboBox _updateChannel;

    /// <summary>Null unless the user saved; otherwise the settings they chose.</summary>
    public WinMuxSettings? Result { get; private set; }

    /// <summary>Set when the user asked for the cwd-reporting page on the way out.</summary>
    public bool OpenCwdReporting { get; private set; }

    /// <summary>The edited profile list, when the user saved. Null means they cancelled.</summary>
    public IReadOnlyList<LaunchProfile>? Profiles { get; private set; }

    public SettingsWindow(
        WinMuxSettings current,
        IReadOnlyList<LaunchProfile> profiles,
        string sessionPath,
        string settingsPath,
        string profilesPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = [.. profiles];

        Title = "WinMux settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;
        Chrome.AppIcon.Apply(this);

        _theme = Choice(
            [("Follow Windows", ThemePreference.System), ("Dark", ThemePreference.Dark), ("Light", ThemePreference.Light)],
            current.Theme);

        // The default-terminal list IS the profile list, so "which terminals do I have" and
        // "which one opens by default" stop being two unrelated ideas.
        _terminal = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = Palette.ControlRadius,
        };

        _profileList = new ListBox
        {
            Height = 210,
            CornerRadius = Palette.ControlRadius,
            Background = Palette.SurfaceBrush,
        };
        _profileList.DoubleTapped += (_, _) => _ = EditSelectedAsync();

        _tabPlacement = Choice(
            [
                ("Top", TabStripPlacement.Top),
                ("Bottom", TabStripPlacement.Bottom),
                ("Left", TabStripPlacement.Left),
                ("Right", TabStripPlacement.Right),
            ],
            current.DefaultTabPlacement);

        _fontFamily = new TextBox
        {
            Text = current.TerminalFontFamily,
            MinWidth = 260,
            PlaceholderText = TerminalFontMetrics.DefaultFontFamily,
        };

        _fontSize = new NumericUpDown
        {
            Value = (decimal)current.TerminalFontSize,
            Minimum = 6,
            Maximum = 72,
            Increment = 1,
            FormatString = "0.#",
            MinWidth = 110,
        };

        _checkUpdates = new CheckBox
        {
            IsChecked = current.CheckForUpdates,
            Foreground = Palette.TextBrush,
            MinWidth = 0,
        };

        _updateChannel = Choice(
            [("Stable only", UpdateChannel.Stable), ("Stable and prereleases", UpdateChannel.Prerelease)],
            current.UpdateChannel);

        // The card supplies the label, so the box itself is only the switch.
        _confirmClosing = new CheckBox
        {
            IsChecked = current.ConfirmBeforeClosingPanes,
            Foreground = Palette.TextBrush,
            MinWidth = 0,
        };

        var cwd = new Button { Content = "Set up…", Classes = { Chrome.Theme.DialogButton } };
        cwd.Click += (_, _) => { OpenCwdReporting = true; Close(); };

        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 24), Spacing = Palette.GapSmall };

        body.Children.Add(SettingsCard.Heading("Appearance"));
        body.Children.Add(SettingsCard.Row(
            "Theme",
            _theme,
            "Follow Windows to switch with the system light and dark setting."));

        body.Children.Add(SettingsCard.Heading("Profiles"));
        body.Children.Add(SettingsCard.Stacked(
            "Everything you can open in a pane",
            ProfileEditor(),
            "Shells and applications alike. These appear in the New menu, in an empty pane's " +
            "launcher and in the command palette."));

        body.Children.Add(SettingsCard.Heading("Terminal"));
        body.Children.Add(SettingsCard.Row(
            "Font",
            _fontFamily,
            "A fallback list. The first font present is used, and the cell size is measured from " +
            "whichever that turns out to be."));
        body.Children.Add(SettingsCard.Row(
            "Font size",
            _fontSize,
            "In pixels, between 6 and 72."));

        body.Children.Add(SettingsCard.Heading("New panes"));
        body.Children.Add(SettingsCard.Row(
            "Default terminal",
            _terminal,
            "What a new terminal pane opens, and what the prefix key's split commands use."));
        body.Children.Add(SettingsCard.Row(
            "Tabs in a new group",
            _tabPlacement,
            "Which edge a stack's tab strip takes when you first tab a pane."));

        body.Children.Add(SettingsCard.Heading("Sessions"));
        body.Children.Add(SettingsCard.Row(
            "Confirm before closing running panes",
            _confirmClosing,
            "Asks first when an action would close a pane with a program still in it."));
        body.Children.Add(SettingsCard.Info("Session file", sessionPath));
        body.Children.Add(SettingsCard.Info("Settings file", settingsPath));
        body.Children.Add(SettingsCard.Info("Profiles file", profilesPath));

        body.Children.Add(SettingsCard.Heading("Working directories"));
        body.Children.Add(SettingsCard.Row(
            "Shell directory reporting",
            cwd,
            "Restoring a pane's directory needs the shell to report it. Without the profile " +
            "snippets, a PowerShell pane restores to the wrong directory as soon as you cd."));

        body.Children.Add(SettingsCard.Heading("Updates"));
        body.Children.Add(SettingsCard.Row(
            "Check for updates on start",
            _checkUpdates,
            "Looks for a newer release on GitHub. Nothing is ever downloaded or installed without asking."));
        body.Children.Add(SettingsCard.Row(
            "Releases to offer",
            _updateChannel,
            "Prereleases are tagged builds that have not been declared stable."));

        body.Children.Add(SettingsCard.Heading("About"));
        body.Children.Add(SettingsCard.Info(
            "Version",
            typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown"));

        var save = new Button { Content = "Save", IsDefault = true, Classes = { Chrome.Theme.DialogButton } };
        save.Click += (_, _) =>
        {
            Profiles = _profiles;
            Result = current with
            {
                Theme = Selected<ThemePreference>(_theme),
                DefaultTerminal = SelectedTerminalId(),
                DefaultTabPlacement = Selected<TabStripPlacement>(_tabPlacement),
                ConfirmBeforeClosingPanes = _confirmClosing.IsChecked == true,
                TerminalFontFamily = string.IsNullOrWhiteSpace(_fontFamily.Text)
                    ? TerminalFontMetrics.DefaultFontFamily
                    : _fontFamily.Text.Trim(),
                TerminalFontSize = (double)(_fontSize.Value ?? (decimal)TerminalFontMetrics.DefaultFontSize),
                CheckForUpdates = _checkUpdates.IsChecked == true,
                UpdateChannel = Selected<UpdateChannel>(_updateChannel),
            };
            Close();
        };

        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { Chrome.Theme.DialogButton } };
        cancel.Click += (_, _) => Close();

        var footer = new Border
        {
            Background = Palette.DialogFooterBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Palette.GapLarge, Palette.GapMedium),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Palette.GapSmall,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, save },
            },
        };

        var root = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 640 });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };

        _pendingDefaultTerminal = current.DefaultTerminal;
        RenderProfiles();
    }

    /// <summary>The list, and beneath it: add from the catalogue, add by hand, edit, or remove.</summary>
    private Control ProfileEditor()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Palette.GapSmall,
            Margin = new Thickness(0, Palette.GapMedium, 0, 0),
        };

        row.Children.Add(Small("Add application…", () => _ = AddFromCatalogAsync()));

        // Connections had no button of their own. They were reachable — "Add manually…", then a kind
        // dropdown — but nothing on this page said the word, so the only way to discover that WinMux
        // does Remote Desktop at all was to open a dialog named after something else and read a list.
        // CLAUDE.md section 5a: a capability nobody can find is a note to the author.
        row.Children.Add(Small("Add connection…", () => _ = AddConnectionAsync()));

        row.Children.Add(Small("Add manually…", () => _ = AddManuallyAsync()));
        row.Children.Add(Small("Edit…", () => _ = EditSelectedAsync()));
        row.Children.Add(Small("Remove", RemoveSelected));

        return new StackPanel { Children = { _profileList, row } };
    }

    private Button Small(string label, Action invoke)
    {
        var button = new Button
        {
            Content = label,
            MinHeight = Palette.ControlHeight,
            Padding = Palette.ButtonPadding,
            CornerRadius = Palette.ControlRadius,
        };
        button.Click += (_, _) => invoke();
        return button;
    }

    private async Task AddFromCatalogAsync()
    {
        var picker = new AppPickerWindow(PlatformServices.Apps, PlatformServices.AppIcons);
        await picker.ShowDialog(this);
        if (picker.Result is not { } app) return;

        // Straight into the editor rather than straight into the list: the catalogue supplies a
        // name and a path, and the window rules are exactly what a user may still need to set.
        await EditAsync(new LaunchProfile
        {
            Id = LaunchProfile.MakeId(app.Name),
            Name = app.Name,
            Kind = ProfileKind.Application,
            Program = app.Program,
            Args = app.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            WorkingDirectory = app.WorkingDirectory,
            Source = app.Source,
        }, replacing: null);
    }

    private Task AddManuallyAsync() => EditAsync(
        new LaunchProfile { Id = string.Empty, Name = string.Empty, Program = string.Empty },
        replacing: null);

    /// <summary>
    /// The same editor, opened already on a connection so the fields that matter are the ones
    /// showing. SSH is the default because it is the one people reach for most; the kind dropdown
    /// switches to Remote Desktop, SFTP or FTP without leaving the dialog.
    /// </summary>
    private Task AddConnectionAsync() => EditAsync(
        new LaunchProfile
        {
            Id = string.Empty,
            Name = string.Empty,
            Program = string.Empty,
            Kind = ProfileKind.Ssh,
        },
        replacing: null);

    private Task EditSelectedAsync() =>
        Selected() is { } existing ? EditAsync(existing, existing) : Task.CompletedTask;

    private async Task EditAsync(LaunchProfile profile, LaunchProfile? replacing)
    {
        var editor = new ProfileEditorWindow(profile);
        await editor.ShowDialog(this);
        if (editor.Result is not { } edited) return;

        if (replacing is null)
        {
            var id = edited.Id;
            var suffix = 2;
            while (_profiles.Any(p => p.Id == id)) id = $"{edited.Id}-{suffix++}";
            _profiles.Add(edited with { Id = id });
        }
        else
        {
            _profiles[_profiles.IndexOf(replacing)] = edited;
        }

        RenderProfiles();
    }

    private void RemoveSelected()
    {
        if (Selected() is not { } profile) return;
        // Never leave the launcher with nothing to offer.
        if (_profiles.Count == 1) return;
        _profiles.Remove(profile);
        RenderProfiles();
    }

    private LaunchProfile? Selected() => (_profileList.SelectedItem as ListBoxItem)?.Tag as LaunchProfile;

    private void RenderProfiles()
    {
        var selectedId = Selected()?.Id;

        _profileList.ItemsSource = _profiles.Select(profile => new ListBoxItem
        {
            Tag = profile,
            Content = new StackPanel
            {
                Margin = new Thickness(2),
                Children =
                {
                    new TextBlock
                    {
                        Text = profile.Name + (profile.Kind == ProfileKind.Application ? "   (application)" : ""),
                        Foreground = Palette.TextBrush,
                    },
                    new TextBlock
                    {
                        Text = profile.Program + (profile.Args.Count > 0 ? " " + string.Join(" ", profile.Args) : ""),
                        FontSize = Palette.CaptionSize,
                        Foreground = Palette.FaintTextBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        }).ToArray();

        var index = _profiles.FindIndex(p => p.Id == selectedId);
        _profileList.SelectedIndex = index >= 0 ? index : (_profiles.Count > 0 ? 0 : -1);

        // The default-terminal choice can only offer terminals, and has to survive one being
        // renamed, added or deleted while this dialog is open.
        var currentTerminal = SelectedTerminalId();
        var terminals = _profiles.Where(p => p.Kind == ProfileKind.Terminal).ToArray();
        _terminal.ItemsSource = terminals
            .Select(p => new ComboBoxItem { Content = p.Name, Tag = p.Id })
            .ToArray();
        var terminalIndex = Array.FindIndex(terminals, p => p.Id == currentTerminal);
        _terminal.SelectedIndex = terminals.Length == 0 ? -1 : Math.Max(0, terminalIndex);
        _pendingDefaultTerminal = currentTerminal;
    }

    private string SelectedTerminalId() =>
        (_terminal.SelectedItem as ComboBoxItem)?.Tag as string ?? _pendingDefaultTerminal;

    private static ComboBox Choice<T>((string Label, T Value)[] options, T current)
    {
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = Palette.ControlRadius,
            ItemsSource = options.Select(option => new ComboBoxItem
            {
                Content = option.Label,
                Tag = option.Value,
            }).ToArray(),
        };

        // Select what is in force, rather than assuming the first entry is the default: a settings
        // dialog that opens showing the wrong value is how people change things by accident.
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => Equals(option.Value, current)));
        return box;
    }

    private static T Selected<T>(ComboBox box) => (T)((ComboBoxItem)box.SelectedItem!).Tag!;
}
