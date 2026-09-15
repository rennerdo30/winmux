using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Core.Layout;
using WinMux.Core.Settings;
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
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.DialogBrush;

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
            Height = 190,
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

        _confirmClosing = new CheckBox
        {
            Content = "Ask before an action closes panes that are still running",
            IsChecked = current.ConfirmBeforeClosingPanes,
            Foreground = Palette.TextBrush,
        };

        var cwd = new Button
        {
            Content = "Set up shell directory reporting…",
            Padding = new Thickness(12, 6),
            CornerRadius = Palette.ControlRadius,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        cwd.Click += (_, _) => { OpenCwdReporting = true; Close(); };

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 20), Spacing = 6 };
        body.Children.Add(Section("Appearance"));
        body.Children.Add(Row("Theme", _theme));

        body.Children.Add(Section("Profiles"));
        body.Children.Add(new TextBlock
        {
            Text = "Everything you can open in a pane: shells and applications alike. These appear " +
                   "in the New menu, in an empty pane's launcher and in the command palette.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.MutedTextBrush,
            Margin = new Thickness(0, 0, 0, 6),
        });
        body.Children.Add(_profileList);
        body.Children.Add(ProfileButtons());

        body.Children.Add(Section("New panes"));
        body.Children.Add(Row("Default terminal", _terminal));
        body.Children.Add(Row("Tabs in a new group", _tabPlacement));
        body.Children.Add(Section("Sessions"));
        body.Children.Add(_confirmClosing);
        body.Children.Add(Info("Session file", sessionPath));
        body.Children.Add(Info("Settings file", settingsPath));
        body.Children.Add(Info("Profiles file", profilesPath));
        body.Children.Add(Section("Working directories"));
        body.Children.Add(new TextBlock
        {
            Text = "Restoring a pane's directory needs the shell to report it. Without the profile " +
                   "snippets, a PowerShell pane restores to the wrong directory as soon as you cd.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.MutedTextBrush,
            Margin = new Thickness(0, 0, 0, 6),
        });
        body.Children.Add(cwd);
        body.Children.Add(Section("About"));
        body.Children.Add(Info("Version", typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown"));

        var save = new Button
        {
            Content = "Save",
            IsDefault = true,
            MinWidth = 96,
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
        };
        save.Click += (_, _) =>
        {
            Profiles = _profiles;
            Result = current with
            {
                Theme = Selected<ThemePreference>(_theme),
                DefaultTerminal = SelectedTerminalId(),
                DefaultTabPlacement = Selected<TabStripPlacement>(_tabPlacement),
                ConfirmBeforeClosingPanes = _confirmClosing.IsChecked == true,
            };
            Close();
        };

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 96,
            Padding = new Thickness(14, 7),
            CornerRadius = Palette.ControlRadius,
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
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 620 });
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };

        _pendingDefaultTerminal = current.DefaultTerminal;
        RenderProfiles();
    }

    /// <summary>Add from the catalogue, add by hand, edit, or remove.</summary>
    private Control ProfileButtons()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
        };

        row.Children.Add(Small("Add application…", () => _ = AddFromCatalogAsync()));
        row.Children.Add(Small("Add manually…", () => _ = AddManuallyAsync()));
        row.Children.Add(Small("Edit…", () => _ = EditSelectedAsync()));
        row.Children.Add(Small("Remove", RemoveSelected));
        return row;
    }

    private Button Small(string label, Action invoke)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(11, 6),
            CornerRadius = Palette.ControlRadius,
        };
        button.Click += (_, _) => invoke();
        return button;
    }

    private async Task AddFromCatalogAsync()
    {
        var picker = new AppPickerWindow(PlatformServices.Apps);
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
                        FontSize = 11,
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

    private static Control Section(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.FaintTextBrush,
        Margin = new Thickness(0, 14, 0, 2),
    };

    private static Control Row(string label, Control control)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 2),
            ColumnDefinitions = [new ColumnDefinition(190, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star)],
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

    private static Control Info(string label, string value)
    {
        var grid = new Grid
        {
            Margin = new Thickness(0, 2),
            ColumnDefinitions = [new ColumnDefinition(190, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star)],
        };
        var name = new TextBlock { Text = label, Foreground = Palette.MutedTextBrush };
        var text = new SelectableTextBlock
        {
            Text = value,
            Foreground = Palette.MutedTextBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(name);
        grid.Children.Add(text);
        return grid;
    }

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
