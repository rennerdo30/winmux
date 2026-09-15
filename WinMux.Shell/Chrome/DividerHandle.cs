using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using WinMux.Core.Layout;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The grab handle in a split's gutter.
///
/// Resizing a pane by dragging the gutter has worked since ADR 0005 — and nobody could tell,
/// because the gutter was six pixels of window background with no handle, no hover state and no
/// cursor change. A feature with no affordance is a feature only its author knows about.
///
/// The handle does not capture the pointer or handle any event: <c>MainWindow</c> already owns the
/// drag, on the canvas, hit-testing against the arrangement's divider rectangles. This is purely
/// what the drag looks like, so the two cannot disagree about where a divider is.
/// </summary>
internal static class DividerHandle
{
    /// <summary>Length of the grip mark along the divider, in pixels.</summary>
    private const double GripExtent = 26;

    /// <summary>Thickness of the grip mark across the divider.</summary>
    private const double GripWeight = 3;

    public static Control Build(Divider divider)
    {
        var columns = divider.Direction == SplitDirection.Columns;

        // A short mark at the middle rather than a full-length rule: Windows 11 splitters are
        // almost invisible until you approach them, and a hard line through every gutter would
        // read as a border between unrelated things.
        var grip = new Border
        {
            Background = Palette.EdgeBrush,
            CornerRadius = new CornerRadius(GripWeight / 2),
            Width = columns ? GripWeight : GripExtent,
            Height = columns ? GripExtent : GripWeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var handle = new Panel
        {
            Background = Avalonia.Media.Brushes.Transparent,
            Cursor = new Cursor(columns ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth),
            Children = { grip },
            [ToolTip.TipProperty] = columns ? "Drag to resize the columns" : "Drag to resize the rows",
        };

        // Brighten on approach. The pointer events are observed, never handled, so the drag that
        // MainWindow runs on the canvas underneath is unaffected.
        handle.PointerEntered += (_, _) =>
        {
            grip.Background = Palette.AccentBrush;
            if (columns) grip.Height = GripExtent * 1.6; else grip.Width = GripExtent * 1.6;
        };
        handle.PointerExited += (_, _) =>
        {
            grip.Background = Palette.EdgeBrush;
            if (columns) grip.Height = GripExtent; else grip.Width = GripExtent;
        };

        return handle;
    }
}
