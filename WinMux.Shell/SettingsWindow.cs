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
    private readonly ComboBox _terminal;
    private readonly ComboBox _tabPlacement;
    private readonly CheckBox _confirmClosing;

    /// <summary>Null unless the user saved; otherwise the settings they chose.</summary>
    public WinMuxSettings? Result { get; private set; }

    /// <summary>Set when the user asked for the cwd-reporting page on the way out.</summary>
    public bool OpenCwdReporting { get; private set; }

    public SettingsWindow(WinMuxSettings current, string sessionPath, string settingsPath)
    {
        ArgumentNullException.ThrowIfNull(current);

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

        _terminal = Choice(
            [
                ("Command Prompt", "cmd"),
                ("Windows PowerShell", "windows-powershell"),
                ("PowerShell 7", "powershell"),
                ("WSL", "wsl"),
            ],
            current.DefaultTerminal);

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
        body.Children.Add(Section("New panes"));
        body.Children.Add(Row("Default terminal", _terminal));
        body.Children.Add(Row("Tabs in a new group", _tabPlacement));
        body.Children.Add(Section("Sessions"));
        body.Children.Add(_confirmClosing);
        body.Children.Add(Info("Session file", sessionPath));
        body.Children.Add(Info("Settings file", settingsPath));
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
            Result = current with
            {
                Theme = Selected<ThemePreference>(_theme),
                DefaultTerminal = Selected<string>(_terminal),
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
    }

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
