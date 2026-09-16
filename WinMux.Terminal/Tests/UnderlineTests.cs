using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// Which cells come back underlined.
///
/// Written because a terminal pane showed *every* line underlined — ordinary prompt output that had
/// asked for no such thing. Underline is set by SGR 4 and cleared by SGR 24 or a full reset, and if
/// any of those is mishandled the attribute leaks across the whole screen, which is what it looked
/// like on screen.
/// </summary>
public sealed class UnderlineTests
{
    private static TerminalCell[] Write(string vt, int columns = 20)
    {
        var engine = new TerminalEmulationEngine(columns, 4, 10);
        engine.Write(Encoding.UTF8.GetBytes(vt));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);
        return row;
    }

    private static bool Underlined(TerminalCell cell) =>
        (cell.Attributes & TerminalCellAttributes.Underline) != 0;

    [Fact]
    public void Plain_text_is_not_underlined()
    {
        // The whole bug in one assertion: text that asked for nothing must carry nothing.
        var row = Write("hello");

        Assert.All("hello".Select((_, i) => row[i]), cell => Assert.False(Underlined(cell)));
    }

    [Fact]
    public void Sgr_4_underlines_and_sgr_24_stops()
    {
        var row = Write("\e[4mon\e[24moff");

        Assert.True(Underlined(row[0]), "SGR 4 should underline");
        Assert.True(Underlined(row[1]));
        Assert.False(Underlined(row[2]), "SGR 24 should stop underlining");
        Assert.False(Underlined(row[3]));
    }

    [Fact]
    public void A_reset_clears_underline()
    {
        var row = Write("\e[4mon\e[0moff");

        Assert.True(Underlined(row[0]));
        Assert.False(Underlined(row[2]), "SGR 0 should clear underline along with everything else");
    }

    [Theory]
    // The attributes an ordinary colourful prompt actually uses. None of them is underline, and
    // each is a plausible way for a mis-mapped flag to arrive as one.
    [InlineData("\e[1m", "bold")]
    [InlineData("\e[2m", "faint")]
    [InlineData("\e[3m", "italic")]
    [InlineData("\e[7m", "inverse")]
    [InlineData("\e[9m", "strikethrough")]
    [InlineData("\e[32m", "green")]
    [InlineData("\e[38;5;208m", "256-colour")]
    [InlineData("\e[38;2;10;20;30m", "true colour")]
    [InlineData("\e[90m", "bright black")]
    [InlineData("\e[100m", "bright background")]
    public void Other_attributes_do_not_bring_underline_with_them(string sgr, string what)
    {
        var row = Write(sgr + "x");

        Assert.False(Underlined(row[0]), $"{what} ({sgr.Replace("\e", "ESC")}) should not underline");
    }

    [Fact]
    public void Underline_does_not_leak_onto_the_next_line()
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(Encoding.UTF8.GetBytes("\e[4mfirst\e[0m\r\nsecond"));

        var second = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount + 1, second);

        Assert.False(Underlined(second[0]), "the second line asked for nothing");
    }

    [Fact]
    public void A_blank_cell_carries_no_underline()
    {
        // Every row is padded with blanks to its full width. If those came back underlined the
        // whole screen would show a rule under each line, whatever the text did.
        var row = Write("hi");

        Assert.False(Underlined(row[5]), "padding past the text must be clean");
        Assert.False(Underlined(row[19]));
    }
}

/// <summary>
/// SGR 4 with colon sub-parameters, which is how modern terminals spell underline styles.
///
/// `4:0` is underline *off*, `4:1` single, `4:3` curly. A parser that splits on ';' only and
/// ignores what follows the colon reads every one of them as a bare `4` — underline on, and
/// `4:0` in particular then turns underline on when it was asked to turn it off. That produces
/// exactly the symptom reported: a pane where everything is underlined.
/// </summary>
public sealed class UnderlineSubParameterTests
{
    private static TerminalCell First(string vt)
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes(vt));
        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);
        return row[0];
    }

    private static bool AnyUnderline(TerminalCell cell) =>
        (cell.Attributes & (TerminalCellAttributes.Underline |
                            TerminalCellAttributes.DoubleUnderline |
                            TerminalCellAttributes.CurlyUnderline)) != 0;

    [Fact]
    public void Colon_zero_means_no_underline()
    {
        Assert.False(AnyUnderline(First("\e[4:0mx")), "4:0 is underline off, not underline on");
    }

    [Fact]
    public void Colon_one_is_a_single_underline()
    {
        Assert.True(AnyUnderline(First("\e[4:1mx")));
    }

    [Fact]
    public void Colon_three_is_a_curly_underline()
    {
        Assert.True(AnyUnderline(First("\e[4:3mx")));
    }

    [Fact]
    public void Turning_a_colon_underline_off_again_works()
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\e[4:3mon\e[4:0moff"));
        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);

        Assert.True(AnyUnderline(row[0]));
        Assert.False(AnyUnderline(row[2]), "4:0 after a curly underline must clear it");
    }

    [Fact]
    public void An_underline_colour_does_not_itself_underline()
    {
        // SGR 58 sets the colour a future underline would use. On its own it underlines nothing.
        Assert.False(AnyUnderline(First("\e[58;5;204mx")));
        Assert.False(AnyUnderline(First("\e[58:2::255:0:0mx")));
    }

    [Fact]
    public void Sgr_59_resets_the_underline_colour_without_underlining()
    {
        Assert.False(AnyUnderline(First("\e[59mx")));
    }
}

/// <summary>
/// The normalizer's edge cases, driven through the engine's public Write because that is where it
/// actually sits.
///
/// The one that matters most is a sequence split across two writes. ConPTY hands over whatever
/// bytes have arrived, so `ESC[4` and `:0m` routinely land in different reads — and a rewriter that
/// assumed whole sequences would corrupt the stream rather than fix it.
/// </summary>
public sealed class SgrNormalizerTests
{
    private static TerminalCell[] WriteChunks(params string[] chunks)
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        foreach (var chunk in chunks)
            engine.Write(System.Text.Encoding.UTF8.GetBytes(chunk));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);
        return row;
    }

    private static bool AnyUnderline(TerminalCell cell) =>
        (cell.Attributes & (TerminalCellAttributes.Underline |
                            TerminalCellAttributes.DoubleUnderline |
                            TerminalCellAttributes.CurlyUnderline)) != 0;

    [Theory]
    [InlineData("\e", "[4:0mx")]
    [InlineData("\e[", "4:0mx")]
    [InlineData("\e[4", ":0mx")]
    [InlineData("\e[4:", "0mx")]
    [InlineData("\e[4:0", "mx")]
    [InlineData("\e[4:0m", "x")]
    public void A_sequence_split_across_two_writes_is_still_repaired(string first, string second)
    {
        var row = WriteChunks(first, second);

        Assert.Equal('x', row[0].Character);
        Assert.False(AnyUnderline(row[0]), $"split after {first.Length} byte(s) lost the repair");
    }

    [Fact]
    public void Text_around_a_rewritten_sequence_survives_intact()
    {
        var row = WriteChunks("a\e[4:0mb\e[4:3mc");

        Assert.Equal('a', row[0].Character);
        Assert.Equal('b', row[1].Character);
        Assert.Equal('c', row[2].Character);
        Assert.False(AnyUnderline(row[1]));
        Assert.True(AnyUnderline(row[2]));
    }

    [Fact]
    public void An_underline_colour_keeps_its_colons()
    {
        // 58:2::255:0:0 is an underline colour and is full of colons. Rewriting it would corrupt
        // the sequence; only parameters that begin "4:" are touched.
        var row = WriteChunks("\e[58:2::255:0:0m\e[4:1mx");

        Assert.Equal('x', row[0].Character);
        Assert.True(AnyUnderline(row[0]), "the 4:1 after an underline colour must still underline");
    }

    [Fact]
    public void A_parameter_merely_containing_four_is_left_alone()
    {
        // "24" and "14" start with digits that include 4 but are not "4:". A sloppier match would
        // rewrite them and change the meaning of ordinary sequences.
        var row = WriteChunks("\e[4mon\e[24moff");

        Assert.True(AnyUnderline(row[0]));
        Assert.False(AnyUnderline(row[2]));
    }

    [Fact]
    public void Colours_and_movement_pass_through_unchanged()
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\e[32mgreen\e[0m\e[2;3Hat"));

        var first = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, first);
        Assert.Equal('g', first[0].Character);

        // Cursor position addressing still works, so the rewrite has not disturbed other CSIs.
        var second = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount + 1, second);
        Assert.Equal('a', second[2].Character);
    }

    [Fact]
    public void An_osc_title_is_not_mistaken_for_a_csi()
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\e]0;a:4:0 title\a\e[4:0mx"));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);

        Assert.Equal("a:4:0 title", engine.Title);
        Assert.Equal('x', row[0].Character);
        Assert.False(AnyUnderline(row[0]));
    }

    [Fact]
    public void An_unterminated_sequence_does_not_swallow_everything_after_it()
    {
        // A stream that opens a CSI and never closes it must not make the pane go blank forever:
        // the normalizer caps how much it will hold, passes the bytes on, and leaves the engine to
        // recover exactly as it would without one.
        //
        // What recovery looks like was measured rather than assumed, and is a little surprising.
        // The following "v" is 0x76, a legal CSI final byte, so it terminates the runaway sequence
        // and is itself consumed — "isible" is what reaches the screen. That is correct handling of
        // malformed input, and it is identical with the normalizer and without it.
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\e[" + new string('9', 400)));
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\r\nvisible"));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount + 1, row);

        var text = new string(row.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd();
        Assert.Equal("isible", text);
    }
}

/// <summary>
/// The SGR codes that turn attributes *off*, each checked for not turning underline on.
///
/// The colon form was one cause and not the only one: the pane was still underlined afterwards.
/// SGR 21 is the prime suspect — ECMA-48 defines it as "doubly underlined", but a large amount of
/// software emits it to mean "bold off", which is what SGR 22 actually does. A terminal that takes
/// the standard literally will underline every run that ends a bold span that way.
/// </summary>
public sealed class SgrOffCodeTests
{
    private static TerminalCell After(string sgr)
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(System.Text.Encoding.UTF8.GetBytes("\e[1mbold" + sgr + "x"));
        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);
        return row[4];
    }

    private static bool AnyUnderline(TerminalCell cell) =>
        (cell.Attributes & (TerminalCellAttributes.Underline |
                            TerminalCellAttributes.DoubleUnderline |
                            TerminalCellAttributes.CurlyUnderline)) != 0;

    [Theory]
    [InlineData("\e[22m", "22 (normal intensity)")]
    [InlineData("\e[23m", "23 (italic off)")]
    [InlineData("\e[24m", "24 (underline off)")]
    [InlineData("\e[25m", "25 (blink off)")]
    [InlineData("\e[27m", "27 (inverse off)")]
    [InlineData("\e[29m", "29 (strikethrough off)")]
    [InlineData("\e[55m", "55 (overline off)")]
    [InlineData("\e[0m", "0 (reset)")]
    public void Turning_something_off_does_not_turn_underline_on(string sgr, string what)
    {
        Assert.False(AnyUnderline(After(sgr)), $"SGR {what} must not underline");
    }

    [Fact]
    public void Sgr_21_is_a_double_underline_here_and_that_is_left_alone()
    {
        // SGR 21 is genuinely ambiguous. ECMA-48 defines it as "doubly underlined"; a lot of
        // software emits it meaning "bold off", which is really SGR 22. The engine follows the
        // standard, as xterm and VTE do, so this records the behaviour rather than arguing with it.
        //
        // It is written down because it was briefly suspected of causing the all-underlined pane.
        // It was not: a capture of the real session showed no SGR 21 -- or any SGR -- at all. The
        // cause was ESC[>4m, a private sequence read as SGR 4. Changing 21 would have been a guess
        // dressed as a fix, and would have broken a correct reading for anyone who relies on it.
        Assert.True(AnyUnderline(After("\e[21m")));
    }
}
