namespace WinMux.Shell.Tests;

/// <summary>
/// Finding text in the scrollback.
///
/// The engine has kept five thousand rows since Phase 1 and the viewport made them reachable, and
/// there was still no way to find anything in them — which is most of what scrollback is for.
/// </summary>
public sealed class TerminalSearchModelTests
{
    private static readonly string[] Buffer =
    [
        "error: build failed",
        "warning: unused variable",
        "ERROR: link failed",
        "",
        "all done",
    ];

    [Fact]
    public void Matching_ignores_case_by_default()
    {
        // A terminal search that is case-sensitive by default finds nothing and looks broken.
        var matches = TerminalSearchModel.Find(Buffer, "error");

        Assert.Equal(2, matches.Count);
        Assert.Equal(0, matches[0].Row);
        Assert.Equal(2, matches[1].Row);
    }

    [Fact]
    public void Case_sensitivity_can_be_asked_for()
    {
        Assert.Single(TerminalSearchModel.Find(Buffer, "error", caseSensitive: true));
    }

    [Fact]
    public void A_match_carries_where_it_starts_and_how_long_it_is()
    {
        var match = TerminalSearchModel.Find(Buffer, "unused").Single();

        Assert.Equal(1, match.Row);
        Assert.Equal(9, match.Column);
        Assert.Equal(6, match.Length);
    }

    [Fact]
    public void Several_matches_on_one_row_are_all_found()
    {
        var matches = TerminalSearchModel.Find(["ab ab ab"], "ab");

        Assert.Equal(3, matches.Count);
        Assert.Equal([0, 3, 6], matches.Select(m => m.Column));
    }

    [Fact]
    public void Matches_do_not_overlap()
    {
        // "aa" in "aaaa" is two matches. Overlapping ones make "next" ambiguous.
        Assert.Equal(2, TerminalSearchModel.Find(["aaaa"], "aa").Count);
    }

    [Fact]
    public void An_empty_query_matches_nothing_rather_than_everything()
    {
        Assert.Empty(TerminalSearchModel.Find(Buffer, ""));
        Assert.Empty(TerminalSearchModel.Find(Buffer, null));
    }

    [Fact]
    public void Stepping_wraps_at_both_ends()
    {
        // Refusing to continue past the last match makes people retype the query to start again.
        Assert.Equal(0, TerminalSearchModel.Step(2, 3, forward: true));
        Assert.Equal(2, TerminalSearchModel.Step(0, 3, forward: false));
        Assert.Equal(1, TerminalSearchModel.Step(0, 3, forward: true));
    }

    [Fact]
    public void Stepping_from_nowhere_lands_at_an_end()
    {
        Assert.Equal(0, TerminalSearchModel.Step(-1, 3, forward: true));
        Assert.Equal(2, TerminalSearchModel.Step(-1, 3, forward: false));
    }

    [Fact]
    public void Stepping_with_no_matches_stays_nowhere()
    {
        Assert.Equal(-1, TerminalSearchModel.Step(0, 0, forward: true));
    }

    [Fact]
    public void The_first_match_is_the_one_nearest_the_view()
    {
        // Otherwise the first Enter jumps to the top of a five-thousand-row history.
        // Matches land on rows 0, 2 and 5, so the answers below are match indices, not row numbers.
        var matches = TerminalSearchModel.Find(["x", "", "x", "", "", "x"], "x");

        Assert.Equal(1, TerminalSearchModel.NearestTo(matches, row: 3));
        Assert.Equal(0, TerminalSearchModel.NearestTo(matches, row: 0));
        Assert.Equal(2, TerminalSearchModel.NearestTo(matches, row: 99));
    }

    [Fact]
    public void With_no_matches_there_is_no_nearest()
    {
        Assert.Equal(-1, TerminalSearchModel.NearestTo([], row: 0));
    }

    [Theory]
    [InlineData(0, 17, true, "1 of 17")]
    [InlineData(2, 17, true, "3 of 17")]
    [InlineData(-1, 0, true, "no matches")]
    [InlineData(-1, 0, false, "")]
    public void The_count_reads_the_way_a_find_bar_reads(int current, int count, bool hasQuery, string expected)
    {
        Assert.Equal(expected, TerminalSearchModel.Describe(current, count, hasQuery));
    }
}

/// <summary>Scrolling to a row, which is what a search result needs from the viewport.</summary>
public sealed class TerminalViewportScrollToRowTests
{
    [Fact]
    public void A_match_is_placed_with_context_above_it()
    {
        // A third down, not at the very top: a match with no lines above it gives no context, and
        // context is most of why you searched.
        var viewport = new TerminalViewport();

        Assert.True(viewport.ScrollToRow(row: 100, totalRows: 500, screenRows: 30, scrollbackRows: 470));

        Assert.Equal(90, viewport.TopRow(500, 30));
    }

    [Fact]
    public void A_row_already_positioned_does_not_move_the_view()
    {
        var viewport = new TerminalViewport();
        viewport.ScrollToRow(100, 500, 30, 470);

        Assert.False(viewport.ScrollToRow(100, 500, 30, 470));
    }

    [Fact]
    public void A_row_near_the_top_of_the_buffer_does_not_scroll_past_it()
    {
        var viewport = new TerminalViewport();

        viewport.ScrollToRow(row: 2, totalRows: 500, screenRows: 30, scrollbackRows: 470);

        Assert.Equal(0, viewport.TopRow(500, 30));
    }

    [Fact]
    public void A_row_on_the_live_screen_leaves_the_view_following()
    {
        var viewport = new TerminalViewport();

        viewport.ScrollToRow(row: 495, totalRows: 500, screenRows: 30, scrollbackRows: 470);

        Assert.True(viewport.IsFollowing);
    }
}
