using Avalonia.Media;
using WinMux.Terminal;

namespace WinMux.Shell.Tests;

/// <summary>
/// Cutting a row into runs that can each be drawn with one brush.
///
/// The renderer drew every row as a single <c>FormattedText</c> with one hardcoded foreground, so
/// every colour and attribute the engine had already parsed was discarded on the way to the screen.
/// These assert the arithmetic that fixes it — that runs break where style breaks and nowhere else,
/// because a run per cell would be correct and far too slow.
/// </summary>
public sealed class TerminalRunSplitterTests
{
    private static TerminalCell Cell(
        char character,
        TerminalColor? foreground = null,
        TerminalCellAttributes attributes = TerminalCellAttributes.None) =>
        new(character, null, attributes, foreground ?? TerminalColor.Default, TerminalColor.Default, 0, false);

    private static TerminalCell[] Row(string text) => text.Select(c => Cell(c)).ToArray();

    [Fact]
    public void An_unstyled_row_is_a_single_run()
    {
        var cells = Row("hello world");

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Text);
        Assert.Equal(0, runs[0].Column);
        Assert.Equal(11, runs[0].Columns);
    }

    [Fact]
    public void A_colour_change_starts_a_new_run()
    {
        var red = TerminalColor.FromPalette(1);
        TerminalCell[] cells = [Cell('a'), Cell('b'), Cell('c', red), Cell('d', red)];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Equal(2, runs.Count);
        Assert.Equal("ab", runs[0].Text);
        Assert.Equal("cd", runs[1].Text);
        Assert.Equal(2, runs[1].Column);
        Assert.Equal(red, runs[1].Foreground);
    }

    [Fact]
    public void An_attribute_change_starts_a_new_run()
    {
        TerminalCell[] cells = [Cell('a'), Cell('b', attributes: TerminalCellAttributes.Bold)];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Equal(2, runs.Count);
        Assert.Equal(TerminalCellAttributes.Bold, runs[1].Attributes);
    }

    [Fact]
    public void Runs_return_to_one_when_the_style_returns()
    {
        // Three runs, not two: the middle one is styled and the outer two are separate stretches.
        var red = TerminalColor.FromPalette(1);
        TerminalCell[] cells = [Cell('a'), Cell('b', red), Cell('c')];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Equal(3, runs.Count);
        Assert.Equal(["a", "b", "c"], runs.Select(r => r.Text));
    }

    [Fact]
    public void A_wide_glyphs_trailing_half_does_not_split_a_run()
    {
        // Otherwise every CJK character would start a new run, and a line of them would be one
        // text layout per character.
        TerminalCell[] cells =
        [
            new('あ', null, TerminalCellAttributes.WideLeading, TerminalColor.Default, TerminalColor.Default, 0, false),
            new('\0', null, TerminalCellAttributes.WideTrailing, TerminalColor.Default, TerminalColor.Default, 0, false),
            Cell('x'),
        ];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Single(runs);
        Assert.Equal("あx", runs[0].Text);

        // Three columns of screen for two characters of text: the caller positions by Columns.
        Assert.Equal(3, runs[0].Columns);
    }

    [Fact]
    public void A_blank_cell_becomes_a_space_rather_than_disappearing()
    {
        // Dropping blanks would shift everything after them left.
        TerminalCell[] cells =
        [
            Cell('a'),
            new('\0', null, TerminalCellAttributes.None, TerminalColor.Default, TerminalColor.Default, 0, true),
            Cell('b'),
        ];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        Assert.Equal("a b", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void An_empty_row_produces_no_runs()
    {
        Assert.Empty(TerminalRunSplitter.Split(Row("abc"), 0));
    }

    [Fact]
    public void A_length_beyond_the_buffer_is_clamped()
    {
        var cells = Row("ab");

        var runs = TerminalRunSplitter.Split(cells, 99);

        Assert.Equal("ab", runs.Single().Text);
    }

    [Fact]
    public void Columns_always_account_for_every_cell_in_the_row()
    {
        // The invariant the renderer positions by: runs tile the row exactly, with no gap and no
        // overlap, or coloured backgrounds would leave seams.
        var red = TerminalColor.FromPalette(1);
        TerminalCell[] cells = [Cell('a'), Cell('b', red), Cell('c', red), Cell('d')];

        var runs = TerminalRunSplitter.Split(cells, cells.Length);

        var column = 0;
        foreach (var run in runs)
        {
            Assert.Equal(column, run.Column);
            column += run.Columns;
        }

        Assert.Equal(cells.Length, column);
    }
}

/// <summary>
/// Turning a <see cref="TerminalColor"/> into something drawable.
///
/// The scheme is Campbell, Windows Terminal's default, replacing a hardcoded Nord palette that
/// ignored the system entirely.
/// </summary>
public sealed class TerminalPaletteTests
{
    [Fact]
    public void A_default_colour_resolves_differently_for_text_and_background()
    {
        Assert.Equal(TerminalPalette.DefaultForeground, TerminalPalette.Resolve(TerminalColor.Default, false));
        Assert.Equal(TerminalPalette.DefaultBackground, TerminalPalette.Resolve(TerminalColor.Default, true));
    }

    [Fact]
    public void A_24_bit_colour_is_used_exactly_as_asked()
    {
        var color = TerminalPalette.Resolve(TerminalColor.FromRgb(0x12, 0x34, 0x56), false);

        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), color);
    }

    [Fact]
    public void The_sixteen_ansi_colours_are_campbell()
    {
        Assert.Equal(Color.FromRgb(0xC5, 0x0F, 0x1F), TerminalPalette.Resolve(TerminalColor.FromPalette(1), false));
        Assert.Equal(Color.FromRgb(0x16, 0xC6, 0x0C), TerminalPalette.Resolve(TerminalColor.FromPalette(10), false));
    }

    [Theory]
    // The cube's corners: index 16 is black, 231 is white, and the levels are not evenly spaced.
    [InlineData(16, 0x00, 0x00, 0x00)]
    [InlineData(231, 0xFF, 0xFF, 0xFF)]
    [InlineData(21, 0x00, 0x00, 0xFF)]
    [InlineData(196, 0xFF, 0x00, 0x00)]
    public void The_colour_cube_is_indexed_the_way_xterm_indexes_it(int index, byte r, byte g, byte b)
    {
        Assert.Equal(Color.FromRgb(r, g, b), TerminalPalette.Resolve(TerminalColor.FromPalette((byte)index), false));
    }

    [Theory]
    [InlineData(232, 8)]
    [InlineData(255, 238)]
    public void The_greyscale_ramp_runs_from_8_to_238(int index, byte value)
    {
        Assert.Equal(Color.FromRgb(value, value, value), TerminalPalette.Resolve(TerminalColor.FromPalette((byte)index), false));
    }

    [Fact]
    public void Faint_text_moves_toward_the_background_rather_than_toward_transparency()
    {
        // Alpha would composite against the wallpaper through Mica, so faint text on a dark
        // background would come out lighter than normal text.
        var faint = TerminalPalette.Faint(Colors.White, Colors.Black);

        Assert.Equal(255, faint.A);
        Assert.True(faint.R < 255 && faint.R > 0, $"expected a blend, got {faint}");
    }
}
