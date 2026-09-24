using Avalonia;

namespace WinMux.Shell.Chrome;

/// <summary>
/// Where a tab dropped at a point belongs in a strip.
///
/// <para>
/// Worked out from the tab rectangles rather than from how far the pointer travelled, because tabs
/// are not all the same width: a title of "build" and one of "npm run watch" differ by a factor of
/// three, and a distance-based guess lands on the wrong one constantly.
/// </para>
///
/// <para>
/// Its own type because it is the only part of dropping a tab that is arithmetic, and therefore the
/// only part that can be checked without a pointer, a window and a drag session.
/// </para>
/// </summary>
internal static class TabDropIndex
{
    /// <summary>
    /// The insertion index for <paramref name="point"/>, between 0 and the number of tabs.
    /// </summary>
    /// <param name="tabs">Each tab's rectangle, in the strip's own coordinates, in order.</param>
    /// <param name="point">Where the pointer let go, in the same coordinates.</param>
    /// <param name="vertical">Whether the strip runs down the side rather than across the top.</param>
    public static int For(IReadOnlyList<Rect> tabs, Point point, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        for (var i = 0; i < tabs.Count; i++)
        {
            var bounds = tabs[i];
            var before = vertical ? point.Y < bounds.Bottom : point.X < bounds.Right;
            if (!before) continue;

            // The near half of a tab means "before this one", the far half "after it", so a drop
            // lands in the gap it was aimed at rather than always on the same side.
            var past = vertical
                ? point.Y > bounds.Y + bounds.Height / 2
                : point.X > bounds.X + bounds.Width / 2;
            return past ? i + 1 : i;
        }

        // Past the last tab, which is also what the empty part of a strip means.
        return tabs.Count;
    }

    /// <summary>
    /// The line to draw for an insertion at <paramref name="index"/>: where the tab will go.
    ///
    /// <para>
    /// A caret in the gap rather than a highlight on a tab, because the question a drag asks is
    /// "between which two", and highlighting one tab answers a different question — the user cannot
    /// tell whether it means before or after.
    /// </para>
    /// </summary>
    /// <returns>Null when the strip has no tabs to sit between.</returns>
    public static Rect? CaretFor(IReadOnlyList<Rect> tabs, int index, bool vertical, double thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0) return null;

        var at = Math.Clamp(index, 0, tabs.Count);
        var edge = at < tabs.Count
            ? (vertical ? tabs[at].Y : tabs[at].X)
            : (vertical ? tabs[^1].Bottom : tabs[^1].Right);

        // Centred on the gap, then nudged inside the strip so a caret at either end is not half
        // drawn outside it.
        var start = edge - thickness / 2;
        var first = tabs[0];
        var last = tabs[^1];

        if (vertical)
        {
            start = Math.Clamp(start, first.Y, Math.Max(first.Y, last.Bottom - thickness));
            return new Rect(first.X, start, Math.Max(first.Width, last.Width), thickness);
        }

        start = Math.Clamp(start, first.X, Math.Max(first.X, last.Right - thickness));
        return new Rect(start, first.Y, thickness, Math.Max(first.Height, last.Height));
    }
}
