using System.Globalization;
using Avalonia.Media;

namespace WinMux.Shell;

/// <summary>
/// The size of one terminal cell, measured from the font rather than assumed.
///
/// The renderer used three constants — <c>CellWidth = 8.45</c>, <c>CellHeight = 18</c>, and a
/// 14px font — that nothing ever checked against the typeface actually in use. Measured on the
/// machine this was found on, at 14px:
///
/// | font | advance |
/// |---|---|
/// | Cascadia Mono, the intended one | 8.203 |
/// | Consolas, the fallback | 7.697 |
///
/// So the assumed width was 3% wrong with the right font and 10% wrong without it. Glyphs inside a
/// run lay out at the font's real advance while every *position* — the run's origin, the cursor,
/// the selection, a search highlight, a coloured cell background — was computed at 8.45, so they
/// drifted apart across a row: a quarter of a character by column ten, three characters by column
/// a hundred, and a coloured prompt drawn as overlapping runs.
///
/// Cascadia Mono is not on a stock Windows install — it arrives with Windows Terminal and Visual
/// Studio — so the fallback is the common case, and the common case was the worse one.
/// </summary>
/// <param name="Typeface">The face the terminal draws with, after fallback has been resolved.</param>
/// <param name="FontSize">In device-independent pixels.</param>
/// <param name="CellWidth">One character's advance.</param>
/// <param name="CellHeight">One row's height.</param>
internal readonly record struct TerminalFontMetrics(
    Typeface Typeface,
    double FontSize,
    double CellWidth,
    double CellHeight)
{
    /// <summary>
    /// Enough characters that the per-character rounding in a text layout averages out.
    ///
    /// Measuring a single glyph rounds its advance to a whole pixel on some backends, which is
    /// exactly the kind of small error this type exists to remove.
    /// </summary>
    private const int SampleLength = 64;

    /// <summary>What a terminal is drawn with when the settings say nothing.</summary>
    public const string DefaultFontFamily = "Cascadia Mono, Consolas, monospace";

    public const double DefaultFontSize = 14;

    /// <summary>Measure a font by laying text out in it.</summary>
    public static TerminalFontMetrics Measure(string? family, double fontSize)
    {
        var size = fontSize is >= 6 and <= 72 ? fontSize : DefaultFontSize;
        var typeface = new Typeface(new FontFamily(
            string.IsNullOrWhiteSpace(family) ? DefaultFontFamily : family));

        var sample = new string('M', SampleLength);
        var text = new FormattedText(
            sample,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brushes.White);

        // Width over the sample, not Width of one glyph: see SampleLength.
        var width = text.Width / SampleLength;

        // The line box, not the glyph box. Height from a single line of text is what the font says
        // a row occupies, which is what a terminal grid is measured in.
        var height = text.Height;

        return Sane(typeface, size, width, height);
    }

    /// <summary>
    /// Refuse to return something that would make the grid unusable.
    ///
    /// A missing font, a zero-size layout or a backend that has not finished initialising can all
    /// produce a zero or absurd measurement, and a zero cell width turns every column calculation
    /// into a division by zero or an infinite loop. Falling back to the old constants is wrong by a
    /// few percent; falling back to zero is wrong by everything.
    /// </summary>
    private static TerminalFontMetrics Sane(Typeface typeface, double size, double width, double height)
    {
        if (!double.IsFinite(width) || width < 1) width = size * 0.6;
        if (!double.IsFinite(height) || height < 1) height = size * 1.3;
        return new TerminalFontMetrics(typeface, size, width, height);
    }

    /// <summary>How many whole columns fit in a width, once the padding is taken out.</summary>
    public int ColumnsIn(double width, double inset) =>
        Math.Max(1, (int)((width - 2 * inset) / CellWidth));

    /// <summary>How many whole rows fit in a height, once the padding is taken out.</summary>
    public int RowsIn(double height, double inset) =>
        Math.Max(1, (int)((height - 2 * inset) / CellHeight));

    /// <summary>The column a point falls in, clamped to the grid.</summary>
    public int ColumnAt(double x, double inset, int columns) =>
        Math.Clamp((int)Math.Round((x - inset) / CellWidth), 0, Math.Max(0, columns));

    /// <summary>The row a point falls in, relative to the top of the grid.</summary>
    public int RowAt(double y, double inset, int rows) =>
        Math.Clamp((int)((y - inset) / CellHeight), 0, Math.Max(0, rows - 1));
}
