using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WinMux.Platform;
using Avalonia.VisualTree;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The window's own title bar, with the toolbar living inside it.
///
/// This is the largest single difference between an application that looks like Windows 11 and one
/// that does not. Explorer, Settings, Terminal and Edge all put their controls *in* the caption
/// row; an app with a separate system title bar above its own chrome reads as older than it is, no
/// matter how the rest of it is styled, because the doubled bar is visible before anything else.
///
/// Avalonia 12 draws its own decorations — border, shadow, resize grips and a title bar with
/// caption buttons — and <see cref="Window.WindowDecorations"/> chooses how much of that it does.
/// <c>BorderOnly</c> is the setting that matters here: Avalonia keeps the parts that are tedious
/// and easy to get wrong (the drop shadow, the eight resize grips, the frame at the right
/// thickness for the scaling) and stops drawing the caption, which is ours.
///
/// Getting that wrong is visible immediately and was: left at the default <c>Full</c>, Avalonia's
/// title bar and this one both drew, so the window title appeared twice, overlapping itself, and
/// its caption buttons sat underneath this row's background.
///
/// Two consequences of owning the caption, worth knowing before touching this:
///
/// * <b>Snap layouts are kept, but not for free.</b> Windows 11 offers its snap flyout when the
///   pointer rests on a window's maximise button, and it finds that button by asking the window —
///   which an app drawing its own caption never gets asked. <see cref="ISnapLayoutService"/> is the
///   platform capability that answers, and claiming the rectangle means the system, not Avalonia,
///   now drives that button: its click and its hover state arrive through the service.
/// * <b>A maximised extended window is larger than its monitor.</b> Windows oversizes it by the
///   resize border on every edge, which would push the caption buttons off-screen. Avalonia reports
///   the overhang as <see cref="Window.OffScreenMargin"/>, and the root takes it as padding.
/// </summary>
internal static class TitleBar
{
    /// <summary>
    /// Wrap <paramref name="content"/> in a caption row and take the window's decorations over.
    /// </summary>
    /// <param name="window">The window being decorated. Its hints are set here.</param>
    /// <param name="content">The toolbar, which becomes the left half of the caption.</param>
    public static Control Build(Window window, Control content)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);

        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = Palette.CaptionHeight;

        var icon = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Source = AppIcon.Bitmap,
        };

        var title = new TextBlock
        {
            FontSize = Palette.CaptionSize,
            Foreground = Palette.MutedTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Bind(TextBlock.TextProperty, window.GetObservable(Window.TitleProperty));

        var minimise = CaptionButton(
            Icons.CaptionMinimise(), "Minimize", () => window.WindowState = WindowState.Minimized);

        // One button, two glyphs: a maximised window offers "restore down", and showing the wrong
        // one is the kind of small wrongness that makes hand-drawn chrome obvious.
        var maximiseGlyph = new Panel { VerticalAlignment = VerticalAlignment.Center };
        var maximise = CaptionButton(maximiseGlyph, "Maximize", () => ToggleMaximised(window));

        // Once the rectangle is claimed, Windows stops delivering ordinary mouse input over it, so
        // the button's own Click never fires and its hover pseudo-class is never set. Both come
        // back through the service instead.
        IDisposable? snap = null;
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == nint.Zero) return;

            snap = PlatformServices.SnapLayouts.Track(
                WindowHandle.FromPlatformValue(handle),
                new MaximizeButton(
                    Bounds: () => ButtonBounds(window, maximise),
                    Invoke: () => Avalonia.Threading.Dispatcher.UIThread.Post(() => ToggleMaximised(window)),
                    HoverChanged: hovered => Avalonia.Threading.Dispatcher.UIThread.Post(
                        () => maximise.Classes.Set(Theme.CaptionHover, hovered))));
        };
        window.Closed += (_, _) => snap?.Dispose();

        var close = CaptionButton(Icons.CaptionClose(), "Close", window.Close);
        close.Classes.Add(Theme.CaptionClose);

        void SyncWindowState()
        {
            var maximised = window.WindowState == WindowState.Maximized;
            maximiseGlyph.Children.Clear();
            maximiseGlyph.Children.Add(maximised ? Icons.CaptionRestore() : Icons.CaptionMaximise());
            ToolTip.SetTip(maximise, maximised ? "Restore down" : "Maximize");
        }

        SyncWindowState();

        var captionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Children = { minimise, maximise, close },
        };

        // The identity on the left, the caption buttons hard against the right, and the toolbar in
        // the column that gives.
        //
        // The toolbar is the star column rather than an Auto one sized to its buttons: Auto asks
        // for the full width its buttons want, which on a narrow window is more than there is, and
        // the shortfall comes out of the last column. The toolbar already scrolls when it does not
        // fit — it is a ScrollViewer for exactly this — and a caption button that is not where the
        // system puts it is not a caption button.
        var grid = new Grid
        {
            Height = Palette.CaptionHeight,
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            ],
        };

        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, title },
        };

        // The toolbar fills its column rather than sitting left-aligned in it. Left alignment makes
        // a control take its *desired* width instead of the width it was given, and a Grid does not
        // clip its children, so a left-aligned toolbar would draw over the caption buttons on a
        // window too narrow for both. Filling the column hands the overflow to the ScrollViewer
        // inside it, which is what it is for. The slack to the toolbar's right is then empty bar,
        // and the row's own handler makes it the drag region.
        Grid.SetColumn(identity, 0);
        Grid.SetColumn(content, 1);
        Grid.SetColumn(captionButtons, 2);
        grid.Children.Add(identity);
        grid.Children.Add(content);
        grid.Children.Add(captionButtons);

        var bar = new Border
        {
            Background = Palette.RaisedBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };

        // Dragging and double-click-to-maximise are the system's title-bar behaviours, and they
        // stop being free the moment we take the caption over. Buttons mark their own presses
        // handled, so this never fires from one.
        bar.PointerPressed += (_, e) =>
        {
            if (e.Handled) return;
            if (e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e);
        };
        bar.DoubleTapped += (_, e) =>
        {
            if (e.Handled) return;
            ToggleMaximised(window);
        };

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) SyncWindowState();
        };

        return bar;
    }

    /// <summary>
    /// Where a caption button is, in the window's client area, in physical pixels.
    ///
    /// Physical because that is what the window system speaks; Avalonia's bounds are logical, so
    /// they are scaled here rather than at the boundary, which has no business knowing about
    /// Avalonia's coordinate system.
    /// </summary>
    private static ClientRect? ButtonBounds(Window window, Visual button)
    {
        if (!button.IsVisible || button.Bounds.Width <= 0) return null;

        var topLeft = button.TranslatePoint(new Point(0, 0), window);
        if (topLeft is not { } origin) return null;

        var scale = window.RenderScaling;
        return new ClientRect(
            (int)(origin.X * scale),
            (int)(origin.Y * scale),
            (int)(button.Bounds.Width * scale),
            (int)(button.Bounds.Height * scale));
    }

    private static void ToggleMaximised(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private static Button CaptionButton(Control glyph, string tip, Action invoke)
    {
        var button = new Button
        {
            Content = glyph,
            Classes = { Theme.CaptionButton },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => invoke();
        return button;
    }

}
