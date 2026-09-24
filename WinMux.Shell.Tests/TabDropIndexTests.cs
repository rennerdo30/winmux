using Avalonia;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

/// <summary>Where a dropped tab lands in a strip. Rectangles and a point, so no window is needed.</summary>
public class TabDropIndexTests
{
    /// <summary>Three tabs of deliberately different widths, which is the normal case.</summary>
    private static readonly Rect[] Across =
    [
        new(0, 0, 60, 28),     // 0..60
        new(60, 0, 180, 28),   // 60..240
        new(240, 0, 80, 28),   // 240..320
    ];

    [Theory]
    [InlineData(5, 0)]      // near half of the first tab
    [InlineData(45, 1)]     // far half of the first tab
    [InlineData(100, 1)]    // near half of the wide one
    [InlineData(200, 2)]    // far half of the wide one
    [InlineData(250, 2)]    // near half of the last
    [InlineData(310, 3)]    // far half of the last
    [InlineData(600, 3)]    // the empty part of the strip, past every tab
    public void A_drop_lands_in_the_gap_it_was_aimed_at(double x, int expected) =>
        Assert.Equal(expected, TabDropIndex.For(Across, new Point(x, 14), vertical: false));

    [Fact]
    public void A_vertical_strip_measures_down_instead_of_across()
    {
        Rect[] down = [new(0, 0, 120, 28), new(0, 28, 120, 28)];

        Assert.Equal(0, TabDropIndex.For(down, new Point(60, 5), vertical: true));
        Assert.Equal(1, TabDropIndex.For(down, new Point(60, 20), vertical: true));
        Assert.Equal(2, TabDropIndex.For(down, new Point(60, 50), vertical: true));
        Assert.Equal(2, TabDropIndex.For(down, new Point(60, 900), vertical: true));
    }

    [Fact]
    public void An_empty_strip_takes_the_drop_at_the_front()
    {
        // A group always has tabs, but the strip is rebuilt constantly and a drop can arrive
        // against a snapshot that has none. Answering 0 beats throwing at the user.
        Assert.Equal(0, TabDropIndex.For([], new Point(40, 10), vertical: false));
    }

    [Fact]
    public void A_point_before_every_tab_lands_at_the_front()
    {
        Assert.Equal(0, TabDropIndex.For(Across, new Point(-40, 14), vertical: false));
    }
}

/// <summary>Where the insertion caret is drawn, which is the whole of the drag's feedback.</summary>
public class TabDropCaretTests
{
    private static readonly Rect[] Across =
    [
        new(0, 0, 60, 28),
        new(60, 0, 180, 28),
        new(240, 0, 80, 28),
    ];

    [Fact]
    public void The_caret_sits_on_the_gap_between_two_tabs()
    {
        var caret = TabDropIndex.CaretFor(Across, index: 1, vertical: false);

        Assert.NotNull(caret);
        Assert.Equal(59, caret!.Value.X);          // centred on x = 60
        Assert.Equal(2, caret.Value.Width);
        Assert.Equal(28, caret.Value.Height);      // full height of the strip
    }

    [Fact]
    public void A_caret_at_either_end_stays_inside_the_strip()
    {
        // Centred on the edge, half of it would be drawn outside the strip and clipped away, so a
        // drop at the very front or the very back would show no caret at all.
        var front = TabDropIndex.CaretFor(Across, index: 0, vertical: false)!.Value;
        var back = TabDropIndex.CaretFor(Across, index: 3, vertical: false)!.Value;

        Assert.Equal(0, front.X);
        Assert.Equal(318, back.X);
        Assert.True(back.Right <= 320);
    }

    [Fact]
    public void A_vertical_strip_gets_a_horizontal_caret()
    {
        Rect[] down = [new(0, 0, 120, 28), new(0, 28, 120, 28)];

        var caret = TabDropIndex.CaretFor(down, index: 1, vertical: true)!.Value;

        Assert.Equal(27, caret.Y);
        Assert.Equal(2, caret.Height);
        Assert.Equal(120, caret.Width);
    }

    [Fact]
    public void An_index_past_the_ends_is_clamped_rather_than_thrown()
    {
        Assert.NotNull(TabDropIndex.CaretFor(Across, index: 99, vertical: false));
        Assert.NotNull(TabDropIndex.CaretFor(Across, index: -5, vertical: false));
    }

    [Fact]
    public void A_strip_with_no_tabs_has_no_caret() =>
        Assert.Null(TabDropIndex.CaretFor([], index: 0, vertical: false));
}
