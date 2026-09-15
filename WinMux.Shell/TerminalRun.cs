using System.Text;
using WinMux.Terminal;

namespace WinMux.Shell;

/// <summary>A stretch of one row that shares a colour and a set of attributes.</summary>
/// <param name="Column">First column, so the caller knows where to draw it.</param>
/// <param name="Columns">Width in cells — not <c>Text.Length</c>, which differs for wide glyphs.</param>
public readonly record struct TerminalRun(
    int Column,
    int Columns,
    string Text,
    TerminalColor Foreground,
    TerminalColor Background,
    TerminalCellAttributes Attributes);

/// <summary>
/// Cuts a row of cells into the fewest runs that can be drawn with one brush each.
///
/// The renderer drew a whole row as a single <c>FormattedText</c>, which is why colour never
/// reached the screen. Drawing one <c>FormattedText</c> per *cell* would fix that and be far too
/// slow — a 200-column row at 60 Hz is 12,000 text layouts a second. Runs are the middle: ordinary
/// output is one or two runs per row, and a rainbow prompt is a dozen.
/// </summary>
internal static class TerminalRunSplitter
{
    /// <summary>
    /// Attributes that change how a run is drawn. The wide-glyph flags are per-cell bookkeeping,
    /// not style, so they must not split a run — otherwise every CJK character starts a new one.
    /// </summary>
    private const TerminalCellAttributes StyleMask =
        TerminalCellAttributes.Bold |
        TerminalCellAttributes.Faint |
        TerminalCellAttributes.Italic |
        TerminalCellAttributes.Underline |
        TerminalCellAttributes.DoubleUnderline |
        TerminalCellAttributes.CurlyUnderline |
        TerminalCellAttributes.Strikethrough |
        TerminalCellAttributes.Overline |
        TerminalCellAttributes.Inverse |
        TerminalCellAttributes.Invisible;

    public static IReadOnlyList<TerminalRun> Split(ReadOnlySpan<TerminalCell> cells, int length)
    {
        var runs = new List<TerminalRun>();
        if (length <= 0) return runs;
        length = Math.Min(length, cells.Length);

        var text = new StringBuilder();
        var start = 0;
        var style = Style(cells[0]);

        for (var column = 0; column < length; column++)
        {
            var cell = cells[column];

            // A wide glyph's trailing half carries no character of its own; it only occupies a
            // column. Appending nothing keeps the text right while the column count advances.
            if (cell.IsWideTrailing) continue;

            var here = Style(cell);
            if (column > start && here != style)
            {
                runs.Add(new TerminalRun(start, column - start, text.ToString(), style.Fg, style.Bg, style.Attributes));
                text.Clear();
                start = column;
                style = here;
            }

            if (cell.IsBlank || (cell.Character == '\0' && cell.ExtendedGlyph is null)) text.Append(' ');
            else cell.AppendGlyph(text);
        }

        if (length > start)
            runs.Add(new TerminalRun(start, length - start, text.ToString(), style.Fg, style.Bg, style.Attributes));

        return runs;
    }

    private static (TerminalColor Fg, TerminalColor Bg, TerminalCellAttributes Attributes) Style(TerminalCell cell) =>
        (cell.Foreground, cell.Background, cell.Attributes & StyleMask);
}
