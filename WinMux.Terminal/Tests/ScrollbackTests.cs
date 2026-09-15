using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// The scrollback the shell now lets people look at.
///
/// Worth asserting because the whole scrollback UI rests on it: if the engine did not retain
/// history, or did not address it the way the viewport assumes, the feature would be a scrollbar
/// over nothing.
/// </summary>
public class ScrollbackTests
{
    [Fact]
    public void Lines_that_scroll_off_the_screen_are_retained()
    {
        var engine = new TerminalEmulationEngine(columns: 40, rows: 10);

        for (var i = 0; i < 100; i++) engine.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));

        Assert.True(engine.ScrollbackCount > 0, "nothing was retained above the screen");
        Assert.Equal(engine.ScrollbackCount + engine.Rows, engine.TotalRows);
    }

    [Fact]
    public void A_retained_line_still_reads_back_correctly()
    {
        // The viewport addresses rows absolutely from 0, so row 0 must be the oldest line.
        var engine = new TerminalEmulationEngine(columns: 40, rows: 10);
        for (var i = 0; i < 100; i++) engine.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));

        var cells = new TerminalCell[engine.Columns];
        var info = engine.CopyRow(0, cells);
        var text = new string(cells.Take(info.Length).Select(c => c.IsBlank ? ' ' : c.Character).ToArray()).TrimEnd();

        Assert.StartsWith("line ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrollback_stops_growing_at_its_capacity()
    {
        var engine = new TerminalEmulationEngine(columns: 40, rows: 10, scrollbackCapacity: 50);

        for (var i = 0; i < 500; i++) engine.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));

        Assert.True(engine.ScrollbackCount <= 50, $"retained {engine.ScrollbackCount} rows for a 50-row limit");
    }

    [Fact]
    public void A_terminal_with_no_scrollback_keeps_only_its_screen()
    {
        var engine = new TerminalEmulationEngine(columns: 40, rows: 10, scrollbackCapacity: 0);

        for (var i = 0; i < 100; i++) engine.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));

        Assert.Equal(0, engine.ScrollbackCount);
        Assert.Equal(engine.Rows, engine.TotalRows);
    }
}
