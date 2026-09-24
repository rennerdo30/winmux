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
}
