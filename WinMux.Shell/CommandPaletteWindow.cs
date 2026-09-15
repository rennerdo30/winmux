using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;
using WinMux.Shell.Keymap;

namespace WinMux.Shell;

/// <summary>
/// The command palette: everything WinMux can do, by name, with the key that also does it.
///
/// A top-level window rather than an overlay drawn on the canvas, because a pane hosting a native
/// window paints above anything Avalonia draws and the palette would vanish behind Explorer
/// (CLAUDE.md section 6). That much was always true. What was not:
///
/// * It had a **system title bar**, so the thing every application shows as a floating surface
///   showed as an ordinary window with "WinMux command palette" written across the top.
/// * It opened **wherever Windows felt like**, instead of over the window it belongs to.
/// * It **stayed open when it lost focus**. Every palette in every application — VS Code, Terminal,
///   Visual Studio, the Start menu's search — closes the moment you click away, because it is a
///   transient surface and not a document.
/// * Its background was a **hardcoded `#181825`**, so it ignored the theme, the accent and Mica.
///
/// The count is <see cref="MainWindow"/>'s to keep: this window closes itself on deactivation, and
/// the shell holds the single instance so asking twice raises the one that is open.
/// </summary>
internal sealed class CommandPaletteWindow : Window
{
    /// <summary>How far below the owner's top edge the palette sits, like every palette does.</summary>
    private const int TopOffset = 96;

    private const int PaletteWidth = 560;

    private readonly TextBox _query;
    private readonly ListBox _results;
    private readonly IReadOnlyList<PaletteCommand> _commands;
    private readonly Action<string> _dispatch;
    private bool _hasBeenActivated;

    public CommandPaletteWindow(
        IEnumerable<string> actions,
        Action<string> dispatch,
        KeyBindingTable? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(dispatch);

        _commands = CommandPaletteModel.Build(actions, bindings);
        _dispatch = dispatch;

        Title = "WinMux command palette";
        Width = PaletteWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;

        // BorderOnly leaves Avalonia drawing the frame and the drop shadow — which is most of what
        // makes a floating surface read as floating — and stops it drawing a title bar.
        WindowDecorations = WindowDecorations.BorderOnly;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Palette.DialogBrush;

        _query = new TextBox
        {
            PlaceholderText = "Type a command…",
            FontSize = Palette.BodySize,
            MinHeight = 36,
            CornerRadius = Palette.ControlRadius,
        };

        _results = new ListBox
        {
            MaxHeight = 380,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, Palette.GapSmall, 0, 0),
            ItemTemplate = new FuncDataTemplate<PaletteCommand>((_, _) => Row(), supportsRecycling: true),
        };

        var body = new StackPanel
        {
            Margin = new Thickness(Palette.GapMedium),
            Children = { _query, _results },
        };

        Content = new Border
        {
            Background = Palette.DialogBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.SurfaceRadius,
            Child = body,
        };

        _query.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Refresh();
        };
        _query.KeyDown += OnKeyDown;
        _results.KeyDown += OnKeyDown;
        _results.DoubleTapped += (_, _) => InvokeSelected();

        // A transient surface goes away when you look elsewhere — but only once it has actually
        // had focus. A window can be told it is deactivated while it is still opening, and closing
        // on that would make the palette flash and vanish rather than appear.
        Activated += (_, _) => _hasBeenActivated = true;
        Deactivated += (_, _) =>
        {
            if (_hasBeenActivated) Close();
        };

        Opened += (_, _) =>
        {
            Refresh();
            _query.Focus();
        };
    }

    /// <summary>Put it over the window it acts on, near the top, and show it.</summary>
    public void ShowOver(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Position is in physical pixels and the sizes here are logical, so the scaling has to be
        // applied by hand. Centred horizontally, high rather than middle: the list grows downward
        // and a centred palette jumps as you type.
        var scale = owner.RenderScaling;
        var left = owner.Position.X + (int)((owner.Bounds.Width - PaletteWidth) / 2 * scale);
        var top = owner.Position.Y + (int)(TopOffset * scale);
        Position = new PixelPoint(Math.Max(0, left), Math.Max(0, top));

        Show(owner);
    }

    private static Control Row()
    {
        var label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.TextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(PaletteCommand.Label)));

        // The shortcut is the reason to keep reading the palette after the first week: it is how
        // someone stops needing it.
        var shortcut = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = Palette.CaptionSize,
            Foreground = Palette.FaintTextBrush,
        };
        shortcut.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(PaletteCommand.Shortcut)));

        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
        };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(shortcut, 1);
        grid.Children.Add(label);
        grid.Children.Add(shortcut);
        return grid;
    }

    private void Refresh()
    {
        var matches = CommandPaletteModel.Filter(_commands, _query.Text);
        _results.ItemsSource = matches;
        _results.SelectedIndex = matches.Count == 0 ? -1 : 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            InvokeSelected();
            e.Handled = true;
        }
        else if (sender == _query && e.Key is Key.Down or Key.Up)
        {
            var count = (_results.ItemsSource as IReadOnlyList<PaletteCommand>)?.Count ?? 0;
            if (count > 0)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + delta, 0, count - 1);
            }
            e.Handled = true;
        }
    }

    private void InvokeSelected()
    {
        if (_results.SelectedItem is not PaletteCommand command) return;
        Close();
        _dispatch(command.Action);
    }
}
