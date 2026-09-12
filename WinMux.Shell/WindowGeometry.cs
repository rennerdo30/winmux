using CoreRect = WinMux.Core.Layout.Rect;

namespace WinMux.Shell;

/// <summary>Keeps restored top-level windows reachable when monitor topology has changed.</summary>
public static class WindowGeometry
{
    public static CoreRect ClampToVisibleArea(CoreRect saved, IReadOnlyList<CoreRect> workingAreas)
    {
        if (workingAreas.Count == 0) return saved;

        var target = workingAreas
            .OrderByDescending(area => IntersectionArea(saved, area))
            .First();
        var width = Math.Min(Math.Max(saved.Width, 320), target.Width);
        var height = Math.Min(Math.Max(saved.Height, 240), target.Height);
        var x = Math.Clamp(saved.X, target.Left, Math.Max(target.Left, target.Right - width));
        var y = Math.Clamp(saved.Y, target.Top, Math.Max(target.Top, target.Bottom - height));
        return new CoreRect(x, y, width, height);
    }

    private static long IntersectionArea(CoreRect a, CoreRect b)
    {
        var width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return (long)width * height;
    }
}
