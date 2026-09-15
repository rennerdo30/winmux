using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Platform;

namespace WinMux.Shell.Tests;

/// <summary>
/// The placement policy, with no windows involved at all.
///
/// This is what the Phase 5 extraction bought. Before it, every one of these rules — deduplicate,
/// re-assert on activation, never touch a dead window, spend the first-placement licence exactly
/// once and only when the window is actually shown — could only be checked by launching a real
/// application and looking at the screen. They are all decisions, and decisions deserve tests.
/// </summary>
public sealed class ForeignWindowTrackerTests
{
    private static readonly WindowHandle Window = WindowHandle.FromPlatformValue(0x1234);
    private static readonly PaneId Pane = PaneId.New();
    private static readonly Rect Bounds = new(10, 20, 300, 200);

    [Fact]
    public void A_wanted_placement_reaches_the_platform_once()
    {
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: false);
        tracker.ApplyPending();
        tracker.ApplyPending();

        var placement = Assert.Single(windows.Applied).Placement;
        Assert.Equal(Bounds, placement.Bounds);
        Assert.True(placement.Visible);
    }

    [Theory]
    [InlineData(true, WindowPlacementMode.EmbeddedChild)]
    [InlineData(false, WindowPlacementMode.FloatingTopLevel)]
    public void Child_selects_embedded_and_attached_selects_floating(bool child, WindowPlacementMode expected)
    {
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: child);
        tracker.ApplyPending();

        Assert.Equal(expected, Assert.Single(windows.Applied).Placement.Mode);
    }

    [Fact]
    public void A_changed_rectangle_is_applied_again()
    {
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: false);
        tracker.ApplyPending();
        tracker.Place(Pane, Window, Bounds with { Width = 640 }, visible: true, child: false);
        tracker.ApplyPending();

        Assert.Equal(2, windows.Applied.Count);
        Assert.Equal(640, windows.Applied[^1].Placement.Bounds.Width);
    }

    [Fact]
    public void Refresh_reasserts_an_unchanged_placement()
    {
        // ADR 0011: a floating host window is not owned by the shell's z-order, so activating the
        // shell buries it. Re-asserting an identical rectangle is the entire fix, and the dedup
        // above would otherwise swallow it.
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: false);
        tracker.ApplyPending();
        tracker.Refresh();
        tracker.ApplyPending();

        Assert.Equal(2, windows.Applied.Count);
    }

    [Fact]
    public void A_dead_window_is_never_touched()
    {
        var windows = new FakeHostWindows();
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: false);
        tracker.ApplyPending();

        Assert.Empty(windows.Applied);
    }

    [Fact]
    public void An_embedded_window_is_told_about_its_first_placement_exactly_once()
    {
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: true);
        tracker.ApplyPending();
        tracker.Place(Pane, Window, Bounds with { Width = 640 }, visible: true, child: true);
        tracker.ApplyPending();

        Assert.True(windows.Applied[0].Placement.IsFirstPlacement);
        Assert.False(windows.Applied[1].Placement.IsFirstPlacement);
    }

    [Fact]
    public void A_hidden_first_placement_does_not_spend_the_first_placement_licence()
    {
        // An inactive tab is placed while hidden. Windows only finishes adopting a reparented
        // window when it is shown, so the licence has to survive until the pane appears.
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: false, child: true);
        tracker.ApplyPending();
        tracker.Place(Pane, Window, Bounds, visible: true, child: true);
        tracker.ApplyPending();

        Assert.False(windows.Applied[0].Placement.IsFirstPlacement);
        Assert.True(windows.Applied[1].Placement.IsFirstPlacement);
    }

    [Fact]
    public void A_failed_placement_is_retried_and_keeps_its_first_placement_licence()
    {
        var windows = new FakeHostWindows(Window) { FailNextApply = true };
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: true);
        tracker.ApplyPending();
        tracker.ApplyPending();

        Assert.Single(windows.Applied);
        Assert.True(windows.Applied[0].Placement.IsFirstPlacement);
    }

    [Fact]
    public void Forget_stops_the_placement_and_lets_a_re_adopted_pane_start_over()
    {
        var windows = new FakeHostWindows(Window);
        using var tracker = new ForeignWindowTracker(windows, runThread: false);

        tracker.Place(Pane, Window, Bounds, visible: true, child: true);
        tracker.ApplyPending();
        tracker.Forget(Pane);
        tracker.ApplyPending();

        Assert.Single(windows.Applied);

        tracker.Place(Pane, Window, Bounds, visible: true, child: true);
        tracker.ApplyPending();

        Assert.True(windows.Applied[^1].Placement.IsFirstPlacement);
    }

    private sealed class FakeHostWindows(params WindowHandle[] alive) : IHostWindowService
    {
        private readonly HashSet<WindowHandle> _alive = [.. alive];

        public List<(WindowHandle Window, WindowPlacement Placement)> Applied { get; } = [];
        public bool FailNextApply { get; set; }

        public bool IsAlive(WindowHandle window) => _alive.Contains(window);

        public void Apply(WindowHandle window, WindowPlacement placement)
        {
            if (FailNextApply)
            {
                FailNextApply = false;
                throw new InvalidOperationException("the window went away mid-call");
            }

            Applied.Add((window, placement));
        }
    }
}
