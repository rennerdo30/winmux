using WinMux.Core.Layout;

namespace WinMux.Shell.Tests;

public sealed class WindowGeometryTests
{
    [Fact]
    public void Visible_geometry_is_preserved()
    {
        var saved = new Rect(100, 80, 900, 600);

        Assert.Equal(saved, WindowGeometry.ClampToVisibleArea(saved, [new Rect(0, 0, 1920, 1080)]));
    }

    [Fact]
    public void Offscreen_geometry_is_moved_onto_the_nearest_available_screen()
    {
        var restored = WindowGeometry.ClampToVisibleArea(
            new Rect(6000, 4000, 900, 600),
            [new Rect(0, 0, 1920, 1040)]);

        Assert.Equal(new Rect(1020, 440, 900, 600), restored);
    }

    [Fact]
    public void Oversized_geometry_is_reduced_to_the_working_area()
    {
        var restored = WindowGeometry.ClampToVisibleArea(
            new Rect(-200, -100, 3000, 2000),
            [new Rect(0, 0, 1280, 720)]);

        Assert.Equal(new Rect(0, 0, 1280, 720), restored);
    }
}
