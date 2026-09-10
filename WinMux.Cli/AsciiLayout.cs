using System.Text;
using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Cli;

/// <summary>
/// Draws a layout into a character grid.
///
/// The same <see cref="Layouter"/> the shell will use, arranged into a terminal-sized rect with a
/// one-column gutter. That is the point: what you see here is what the real layout engine computed,
/// not a separate drawing of what it was supposed to compute.
/// </summary>
internal static class AsciiLayout
{
    public static string Render(LayoutTree tree, int columns, int rows)
    {
        var arrangement = Layouter.Arrange(tree.Root, new Rect(0, 0, columns, rows), dividerThickness: 1);

        var grid = new char[rows, columns];
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
                grid[y, x] = ' ';

        foreach (var (id, rect) in arrangement.PaneRects.OrderBy(p => p.Value.Y).ThenBy(p => p.Value.X))
        {
            var pane = tree.GetPane(id)!;
            DrawPane(grid, rect, pane, focused: id == tree.Focused, columns, rows);
        }

        var sb = new StringBuilder();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++) sb.Append(grid[y, x]);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void DrawPane(char[,] grid, Rect rect, Pane pane, bool focused, int cols, int rowCount)
    {
        if (rect.Width < 2 || rect.Height < 2) return;

        // Double border marks the focused pane, so focus is visible without a legend.
        var (h, v, tl, tr, bl, br) = focused
            ? ('═', '║', '╔', '╗', '╚', '╝')
            : ('─', '│', '┌', '┐', '└', '┘');

        int x0 = rect.Left, x1 = rect.Right - 1, y0 = rect.Top, y1 = rect.Bottom - 1;

        for (int x = x0; x <= x1; x++) { Put(grid, x, y0, h, cols, rowCount); Put(grid, x, y1, h, cols, rowCount); }
        for (int y = y0; y <= y1; y++) { Put(grid, x0, y, v, cols, rowCount); Put(grid, x1, y, v, cols, rowCount); }
        Put(grid, x0, y0, tl, cols, rowCount);
        Put(grid, x1, y0, tr, cols, rowCount);
        Put(grid, x0, y1, bl, cols, rowCount);
        Put(grid, x1, y1, br, cols, rowCount);

        int inner = rect.Width - 2;
        if (inner < 3) return;

        var lines = new List<string> { (focused ? "* " : "") + pane.Title };
        if (rect.Height >= 5)
        {
            lines.Add(Kind(pane));
            if (pane.Restore.Cwd.IsKnown) lines.Add(Shorten(pane.Restore.Cwd.Path, inner));
        }

        for (int i = 0; i < lines.Count && y0 + 1 + i < y1; i++)
            Write(grid, x0 + 1, y0 + 1 + i, Centre(Shorten(lines[i], inner), inner), cols, rowCount);
    }

    private static string Kind(Pane pane) => pane.Kind switch
    {
        PaneKind.Terminal => "[terminal]",
        PaneKind.FileBrowser => "[files]",
        PaneKind.ForeignApp => "[app: " + pane.Restore.Strategy.ToString().ToLowerInvariant() + "]",
        _ => "[?]",
    };

    private static string Centre(string s, int width) =>
        s.Length >= width ? s : new string(' ', (width - s.Length) / 2) + s;

    private static string Shorten(string s, int width)
    {
        if (s.Length <= width) return s;
        if (width <= 1) return s[..Math.Max(0, width)];
        // Keep the tail of a path: the last segments identify it, the drive letter rarely does.
        return width <= 4 ? s[^width..] : "…" + s[^(width - 1)..];
    }

    private static void Put(char[,] grid, int x, int y, char c, int cols, int rows)
    {
        if (x >= 0 && x < cols && y >= 0 && y < rows) grid[y, x] = c;
    }

    private static void Write(char[,] grid, int x, int y, string s, int cols, int rows)
    {
        for (int i = 0; i < s.Length; i++) Put(grid, x + i, y, s[i], cols, rows);
    }
}
