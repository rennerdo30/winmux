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
