using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Shell.Chrome;

namespace WinMux.Shell;

/// <summary>
/// The find bar for a terminal pane.
///
/// A top-level window rather than something drawn inside the pane, for the reason in CLAUDE.md
/// section 6: transient surfaces are separate layered windows, because a pane hosting a native
/// application paints over anything the shell draws. A terminal pane is Avalonia's own and would
/// have been safe, but having one find bar that works the same over every kind of pane is worth
/// more than saving a window.
///
/// It is deliberately not modal and does not steal the pane's scrollback: typing filters as you go,
/// Enter and Shift+Enter walk the matches, and Escape puts the view back on the live screen.
/// </summary>
internal sealed class TerminalSearchWindow : Window
{
    private const int BarWidth = 380;

    private readonly TextBox _query;
    private readonly TextBlock _count;
    private readonly TerminalPaneControl _pane;
    private bool _hasBeenActivated;

    public TerminalSearchWindow(TerminalPaneControl pane)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));

        Title = "Find in pane";
        Width = BarWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.BorderOnly;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Palette.DialogBrush;

        _query = new TextBox
        {
            PlaceholderText = "Find…",
            FontSize = Palette.BodySize,
            MinHeight = Palette.ControlHeight,
            CornerRadius = Palette.ControlRadius,
        };

        _count = new TextBlock
        {
            FontSize = Palette.CaptionSize,
            Foreground = Palette.FaintTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 64,
            TextAlignment = TextAlignment.Right,
        };

        var previous = Step("Previous match (Shift+Enter)", forward: false);
        var next = Step("Next match (Enter)", forward: true);

        var row = new Grid
        {
            Margin = new Thickness(Palette.GapMedium, Palette.GapSmall),
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            ],
        };

        _count.Margin = new Thickness(Palette.GapMedium, 0, Palette.GapSmall, 0);
        Grid.SetColumn(_query, 0);
        Grid.SetColumn(_count, 1);
        Grid.SetColumn(previous, 2);
        Grid.SetColumn(next, 3);
        row.Children.Add(_query);
        row.Children.Add(_count);
        row.Children.Add(previous);
        row.Children.Add(next);

        Content = new Border
        {
            Background = Palette.DialogBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Palette.SurfaceRadius,
            Child = row,
        };

        _query.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Run();
        };
        _query.KeyDown += OnKeyDown;

        // Closing puts the pane back where it was; leaving highlights behind after the bar is gone
        // would be a scrollback that looks permanently marked up.
        Closed += (_, _) => _pane.ClearSearch();

        Activated += (_, _) => _hasBeenActivated = true;
        Deactivated += (_, _) =>
        {
            if (_hasBeenActivated) Close();
        };

        Opened += (_, _) => _query.Focus();
    }

    /// <summary>Put the bar at the top-right of the pane it searches, the way every editor does.</summary>
    public void ShowOver(Window owner, Rect paneBounds)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var scale = owner.RenderScaling;
        var left = owner.Position.X + (int)((paneBounds.Right - BarWidth - 16) * scale);
        var top = owner.Position.Y + (int)((paneBounds.Y + 16) * scale);
        Position = new PixelPoint(Math.Max(0, left), Math.Max(0, top));

        Show(owner);
    }

    private Button Step(string tip, bool forward)
    {
        var button = new Button
        {
            Content = forward ? Icons.Chevron() : Icons.ChevronUp(),
            Classes = { Chrome.Theme.IconButton },
            [ToolTip.TipProperty] = tip,
        };
        button.Click += (_, _) =>
        {
            _pane.StepMatch(forward);
            UpdateCount();
            _query.Focus();
        };
        return button;
    }

    private void Run()
    {
        _pane.Search(_query.Text);
        UpdateCount();
    }

    private void UpdateCount()
    {
        var hasQuery = !string.IsNullOrEmpty(_query.Text);
        _count.Text = TerminalSearchModel.Describe(_pane.CurrentMatch, _pane.MatchCount, hasQuery);
        _count.Foreground = hasQuery && _pane.MatchCount == 0 ? Palette.DangerBrush : Palette.FaintTextBrush;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                _pane.StepMatch(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                UpdateCount();
                e.Handled = true;
                break;
        }
    }
}
