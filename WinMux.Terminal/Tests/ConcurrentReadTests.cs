using System.Text;
using Xunit;

namespace WinMux.Terminal.Tests;

/// <summary>
/// Reading the engine while the pty thread writes to it.
///
/// <para>
/// WinMux 0.7.4 crashed with <c>IndexOutOfRangeException</c> out of <c>CopyRow</c>, from inside an
/// Avalonia repaint. The renderer reads the dimensions, works out which rows are visible, and then
/// copies them — each step taking the engine's lock separately, so the lock made every step atomic
/// and the frame as a whole atomic in no way at all. A program entering the alternate screen
/// discards the whole scrollback in one write, and any row index computed against the old buffer is
/// then past the end of the new one.
/// </para>
///
/// <para>
/// These tests are the reason <c>CopyRow</c> is total. The first two are deterministic and describe
/// the exact crash; the third is the racing version, which is the shape the user actually met.
/// </para>
/// </summary>
public class ConcurrentReadTests
{
    private static readonly byte[] EnterAlternateScreen = Encoding.UTF8.GetBytes("\u001b[?1049h");
    private static readonly byte[] LeaveAlternateScreen = Encoding.UTF8.GetBytes("\u001b[?1049l");

    [Fact]
    public void Entering_the_alternate_screen_discards_the_scrollback()
    {
        // The premise the crash rests on, measured rather than assumed: this is what makes a row
        // index that was valid microseconds ago point past the end of the buffer.
        var engine = Filled(rows: 10, lines: 100);
        var before = engine.TotalRows;

        engine.Write(EnterAlternateScreen);

        Assert.Equal(101, before);
        Assert.Equal(engine.Rows, engine.TotalRows);
        Assert.True(engine.TotalRows < before, "the alternate screen kept the scrollback");
    }

    [Fact]
    public void A_row_that_the_alternate_screen_took_away_reads_as_empty()
    {
        // Precisely the crashing call: the renderer's firstRow, used after the switch.
        var engine = Filled(rows: 10, lines: 100);
        var staleRow = engine.TotalRows - 1;
        engine.Write(EnterAlternateScreen);

        var info = engine.CopyRow(staleRow, new TerminalCell[engine.Columns]);

        Assert.Equal(0, info.Length);
    }

    [Fact]
    public void A_negative_row_reads_as_empty_too()
    {
        var engine = Filled(rows: 10, lines: 10);

        Assert.Equal(0, engine.CopyRow(-1, new TerminalCell[engine.Columns]).Length);
    }

    [Fact]
    public async Task Rendering_a_frame_while_a_program_writes_never_throws()
    {
        // The race itself. Without a total CopyRow this fails in well under a second; it is kept
        // because the deterministic tests above pin one mechanism, and a reader that composes a
        // frame out of several separate reads has more than one.
        var engine = Filled(rows: 24, lines: 200);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            var line = Encoding.UTF8.GetBytes("a line of output\r\n");
            while (!cancellation.IsCancellationRequested)
            {
                for (var i = 0; i < 50; i++) engine.Write(line);
                engine.Write(EnterAlternateScreen);
                for (var i = 0; i < 10; i++) engine.Write(line);
                engine.Write(LeaveAlternateScreen);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Read the geometry, then the rows, exactly as TerminalPaneControl.Render does.
                var columns = engine.Columns;
                var rows = engine.Rows;
                var firstRow = Math.Max(0, engine.TotalRows - rows);
                var cells = new TerminalCell[columns];

                for (var row = 0; row < rows; row++)
                {
                    var info = engine.CopyRow(firstRow + row, cells);
                    Assert.True(info.Length <= cells.Length, "a row was reported longer than the buffer it was copied into");
                }
            }
        });

        await Task.WhenAll(writer, reader);
    }

    private static TerminalEmulationEngine Filled(int rows, int lines)
    {
        var engine = new TerminalEmulationEngine(columns: 40, rows: rows);
        for (var i = 0; i < lines; i++) engine.Write(Encoding.UTF8.GetBytes($"line {i}\r\n"));
        return engine;
    }
}
