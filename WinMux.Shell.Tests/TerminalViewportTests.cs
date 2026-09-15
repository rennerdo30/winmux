namespace WinMux.Shell.Tests;

/// <summary>
/// Scrollback and selection, as arithmetic.
///
/// None of this could be tested before, because none of it existed: the control drew the bottom of
/// the buffer and had no wheel handler at all. The engine has kept scrollback since Phase 1 and
/// there was no way to look at it.
///
/// Absolute rows are the whole design. A selection made at row 400 must still cover the same text
/// after fifty more rows arrive, and a view parked in history must not slide.
/// </summary>
public sealed class TerminalViewportTests
{
    private const int ScreenRows = 25;

    [Fact]
    public void A_new_viewport_follows_the_live_screen()
    {
        var viewport = new TerminalViewport();

        Assert.True(viewport.IsFollowing);
        Assert.Equal(0, viewport.ScrollOffset);
        Assert.Equal(75, viewport.TopRow(totalRows: 100, ScreenRows));
    }

    [Fact]
    public void Scrolling_back_moves_the_top_row_up()
    {
        var viewport = new TerminalViewport();

        Assert.True(viewport.Scroll(10, scrollbackRows: 75));

        Assert.Equal(65, viewport.TopRow(100, ScreenRows));
        Assert.False(viewport.IsFollowing);
    }

    [Fact]
    public void Scrolling_stops_at_the_oldest_row_and_at_the_live_screen()
    {
        var viewport = new TerminalViewport();

        viewport.Scroll(10_000, scrollbackRows: 75);
        Assert.Equal(75, viewport.ScrollOffset);
        Assert.Equal(0, viewport.TopRow(100, ScreenRows));
        Assert.False(viewport.Scroll(5, 75));

        viewport.Scroll(-10_000, 75);
        Assert.Equal(0, viewport.ScrollOffset);
        Assert.False(viewport.Scroll(-1, 75));
    }

    [Fact]
    public void A_buffer_with_no_history_cannot_be_scrolled()
    {
        var viewport = new TerminalViewport();

        Assert.False(viewport.Scroll(3, scrollbackRows: 0));
        Assert.True(viewport.IsFollowing);
    }

    [Fact]
    public void Output_arriving_while_reading_history_does_not_move_the_text()
    {
        // The property that makes scrollback usable on a busy terminal: the rows on screen stay
        // the same rows, and the view drifts further from the bottom instead.
        var viewport = new TerminalViewport();
        viewport.OnBufferGrew(totalRows: 100, scrollbackRows: 75);
        viewport.Scroll(20, 75);
        var before = viewport.TopRow(100, ScreenRows);

        viewport.OnBufferGrew(totalRows: 130, scrollbackRows: 105);

        Assert.Equal(before, viewport.TopRow(130, ScreenRows));
        Assert.Equal(50, viewport.ScrollOffset);
    }

    [Fact]
    public void Output_arriving_while_following_keeps_following()
    {
        var viewport = new TerminalViewport();
        viewport.OnBufferGrew(100, 75);

        viewport.OnBufferGrew(130, 105);

        Assert.True(viewport.IsFollowing);
        Assert.Equal(105, viewport.TopRow(130, ScreenRows));
    }

    [Fact]
    public void Scrolling_to_the_bottom_reports_whether_it_had_to_move()
    {
        var viewport = new TerminalViewport();

        Assert.False(viewport.ScrollToBottom());
        viewport.Scroll(5, 75);
        Assert.True(viewport.ScrollToBottom());
    }

    [Fact]
    public void A_click_with_no_drag_selects_nothing()
    {
        var viewport = new TerminalViewport();

        viewport.BeginSelection(new TerminalPosition(10, 4));

        Assert.False(viewport.HasSelection);
        Assert.Null(viewport.Selection);
    }

    [Fact]
    public void A_selection_is_ordered_however_it_was_dragged()
    {
        var upward = new TerminalViewport();
        upward.BeginSelection(new TerminalPosition(20, 5));
        upward.ExtendSelection(new TerminalPosition(10, 2));

        var downward = new TerminalViewport();
        downward.BeginSelection(new TerminalPosition(10, 2));
        downward.ExtendSelection(new TerminalPosition(20, 5));

        Assert.Equal(downward.Selection, upward.Selection);
        Assert.Equal(new TerminalPosition(10, 2), upward.Selection!.Value.Start);
    }

    [Fact]
    public void A_single_row_selection_covers_only_the_dragged_columns()
    {
        var viewport = new TerminalViewport();
        viewport.BeginSelection(new TerminalPosition(7, 3));
        viewport.ExtendSelection(new TerminalPosition(7, 11));

        Assert.Equal((3, 11), viewport.RowSpan(7, columns: 80));
        Assert.Null(viewport.RowSpan(6, 80));
        Assert.Null(viewport.RowSpan(8, 80));
    }

    [Fact]
    public void A_multi_row_selection_fills_the_rows_between()
    {
        // Otherwise a dragged selection renders as a diagonal ribbon instead of selected text.
        var viewport = new TerminalViewport();
        viewport.BeginSelection(new TerminalPosition(5, 60));
        viewport.ExtendSelection(new TerminalPosition(8, 4));

        Assert.Equal((60, 80), viewport.RowSpan(5, columns: 80));
        Assert.Equal((0, 80), viewport.RowSpan(6, 80));
        Assert.Equal((0, 80), viewport.RowSpan(7, 80));
        Assert.Equal((0, 4), viewport.RowSpan(8, 80));
    }

    [Fact]
    public void A_span_is_clamped_to_the_row_width()
    {
        // A drag can end past the last column, and a narrowed terminal can leave an old selection
        // wider than the row it covers.
        var viewport = new TerminalViewport();
        viewport.BeginSelection(new TerminalPosition(3, 0));
        viewport.ExtendSelection(new TerminalPosition(3, 500));

        Assert.Equal((0, 80), viewport.RowSpan(3, columns: 80));
    }

    [Fact]
    public void An_empty_span_is_reported_as_no_span()
    {
        var viewport = new TerminalViewport();
        viewport.SetSelection(new TerminalPosition(2, 5), new TerminalPosition(4, 0));

        // Row 4's span is columns 0..0, which is nothing to highlight.
        Assert.Null(viewport.RowSpan(4, columns: 80));
    }

    [Fact]
    public void Clearing_a_selection_reports_whether_there_was_one()
    {
        var viewport = new TerminalViewport();

        Assert.False(viewport.ClearSelection());

        viewport.BeginSelection(new TerminalPosition(1, 1));
        viewport.ExtendSelection(new TerminalPosition(1, 9));
        Assert.True(viewport.ClearSelection());
        Assert.Null(viewport.Selection);
    }

    [Fact]
    public void A_selection_stays_on_the_same_text_when_the_buffer_grows()
    {
        // The reason rows are absolute rather than relative to the screen.
        var viewport = new TerminalViewport();
        viewport.OnBufferGrew(100, 75);
        viewport.Scroll(30, 75);
        viewport.BeginSelection(new TerminalPosition(50, 0));
        viewport.ExtendSelection(new TerminalPosition(52, 10));

        viewport.OnBufferGrew(200, 175);

        Assert.Equal((new TerminalPosition(50, 0), new TerminalPosition(52, 10)), viewport.Selection);
    }

    [Fact]
    public void Positions_order_by_row_then_column()
    {
        Assert.True(new TerminalPosition(1, 90) < new TerminalPosition(2, 0));
        Assert.True(new TerminalPosition(2, 1) > new TerminalPosition(2, 0));
        Assert.True(new TerminalPosition(3, 3) >= new TerminalPosition(3, 3));
    }
}
