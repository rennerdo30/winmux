using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// CSI sequences with a private-parameter prefix that happen to end in <c>m</c>.
///
/// <c>ESC[>4m</c> is xterm's "set modifyOtherKeys": the <c>&gt;</c> makes it a private sequence that
/// has nothing to do with SGR. A parser that drops the prefix and reads the rest sees <c>ESC[4m</c>
/// — underline on. Claude Code sends <c>ESC[&gt;4m</c> once while starting up and then styles almost
/// nothing, so there is never an SGR 24 or a reset to undo it: the whole pane, and everything
/// printed in it afterwards, comes out underlined.
///
/// That is the bug that was actually reported. It was found by capturing the bytes a real session
/// emits through a real ConPTY, after two plausible SGR theories had been chased instead.
/// </summary>
public sealed class PrivateModeSgrTests
{
    private static TerminalCell FirstCellAfter(string prefix)
    {
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(Encoding.UTF8.GetBytes(prefix + "x"));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);
        return row[0];
    }

    private static bool AnyUnderline(TerminalCell cell) =>
        (cell.Attributes & (TerminalCellAttributes.Underline |
                            TerminalCellAttributes.DoubleUnderline |
                            TerminalCellAttributes.CurlyUnderline)) != 0;

    [Theory]
    [InlineData("\e[>4m", "set modifyOtherKeys — what Claude Code sends")]
    [InlineData("\e[>4;2m", "modifyOtherKeys with a level")]
    [InlineData("\e[>0m", "reset modifyOtherKeys")]
    [InlineData("\e[?4m", "a question-mark private sequence")]
    [InlineData("\e[<4m", "a less-than private sequence")]
    public void A_private_sequence_ending_in_m_is_not_an_sgr(string sequence, string what)
    {
        Assert.False(
            AnyUnderline(FirstCellAfter(sequence)),
            $"{what}: the private prefix makes this not SGR, so it must style nothing");
    }

    [Fact]
    public void An_ordinary_sgr_still_works_after_a_private_one()
    {
        // The repair must not be a blanket "ignore anything ending in m".
        var engine = new TerminalEmulationEngine(20, 4, 10);
        engine.Write(Encoding.UTF8.GetBytes("\e[>4m\e[4munderlined"));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);

        Assert.True(AnyUnderline(row[0]), "a real SGR 4 after a private sequence must still underline");
    }

    [Fact]
    public void The_sequence_a_real_session_starts_with_leaves_the_screen_unstyled()
    {
        // Lifted from a capture of `claude --resume` running in a real ConPTY: the private-mode
        // handshake it sends before drawing anything.
        const string handshake =
            "\e[1t\e[?1004h\e[?9001h\e[?25l\e[?2004h\e[?2031h\e[>0q\e[?u\e[c\e[>4m\e[<u\e[?1004l";

        var engine = new TerminalEmulationEngine(40, 4, 10);
        engine.Write(Encoding.UTF8.GetBytes(handshake + "plain text"));

        var row = new TerminalCell[engine.Columns];
        engine.CopyRow(engine.ScrollbackCount, row);

        for (var column = 0; column < "plain text".Length; column++)
        {
            Assert.False(AnyUnderline(row[column]), $"column {column} of an unstyled line is underlined");
        }
    }
}
