using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// Private CSI sequences ending in <c>u</c> or <c>&gt;q</c> must not move the cursor.
///
/// Claude Code asks <c>ESC[?u</c> (kitty keyboard protocol: supported?) and <c>ESC[&gt;0q</c>
/// (XTVERSION) at startup. The engine read <c>ESC[?u</c> as <c>ESC[u</c>, restore cursor, and every
/// relative move afterwards landed rows from its target: the trust dialog's marker was drawn into
/// its border and could not be moved, and later screens were painted over earlier ones. Found by
/// replaying a capture of the real byte stream.
/// </summary>
public sealed class KeyboardProtocolQueryTests
{
    [Fact]
    public void The_public_restore_cursor_still_restores_it()
    {
        // The control case: without it, "the cursor did not move" could mean the test never
        // exercised a restore at all.
        var engine = new TerminalEmulationEngine(80, 24);

        Write(engine, "\e[6;6H\e7\e[20;30H\e[u");

        Assert.Equal((5, 5), (engine.Cursor.Row, engine.Cursor.Column));
    }

    [Theory]
    [InlineData("\e[?u")]
    [InlineData("\e[>1u")]
    [InlineData("\e[<u")]
    [InlineData("\e[=1;1u")]
    [InlineData("\e[>0q")]
    public void A_keyboard_protocol_or_version_query_leaves_the_cursor_where_it_is(string query)
    {
        var engine = new TerminalEmulationEngine(80, 24);

        Write(engine, "\e[6;6H\e7\e[20;30H" + query);

        Assert.Equal((19, 29), (engine.Cursor.Row, engine.Cursor.Column));
    }

    [Fact]
    public void Claude_Codes_trust_dialog_marker_moves_between_its_two_choices()
    {
        // The shape of the captured stream: two choices drawn, a blank line and a hint below, then
        // the startup queries, then one press of Down redrawing the marker with relative moves.
        var engine = new TerminalEmulationEngine(80, 24);
        var sequence =
            "\e7\e[r\e8" +
            "\r\n title\r\n\r\n" +
            " > No, exit\r\n" +
            "   Yes, I trust this folder\r\n\r\n" +
            " Enter to confirm\r\n" +
            "\e[1C\e[4A\e[>0q\e[?u\e[c\e(B\u000f\e[1D\e[4B\r" +
            "\e[1C\e[4A \r\e[1C\e[1B>\r\n\n\n";

        foreach (var value in Encoding.UTF8.GetBytes(sequence))
        {
            engine.Write([value]);
        }

        Assert.Equal("   No, exit", Row(engine, 3));
        Assert.Equal(" > Yes, I trust this folder", Row(engine, 4));
        Assert.Equal(" title", Row(engine, 1));
    }

    private static void Write(TerminalEmulationEngine engine, string text) => engine.Write(Encoding.UTF8.GetBytes(text));

    private static string Row(TerminalEmulationEngine engine, int row)
    {
        var cells = new TerminalCell[engine.Columns];
        var info = engine.CopyRow(engine.ScrollbackCount + row, cells);
        var text = new StringBuilder();
        for (var column = 0; column < info.Length; column++)
        {
            if (cells[column].IsBlank || cells[column].Character == '\0') text.Append(' ');
            else cells[column].AppendGlyph(text);
        }
        return text.ToString().TrimEnd();
    }
}
