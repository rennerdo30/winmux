using Avalonia.Threading;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a pane that repaints without stopping does to work queued at a lower priority.
///
/// <para>
/// Reported as "for a tab change i often need to doubleclick", with the observation that running
/// Claude Code in the panes seemed to be involved. It is: a terminal pane posts a repaint at
/// <see cref="DispatcherPriority.Render"/> every time its engine updates, and a full-screen program
/// redrawing continuously posts them faster than the dispatcher drains them. Anything queued below
/// Render then waits for a gap that does not come.
/// </para>
///
/// <para>
/// These tests are about Avalonia's dispatcher rather than about WinMux, which is the point: the
/// behaviour they pin is the reason a piece of WinMux had to change, and if it ever stopped being
/// true the reason would go with it.
/// </para>
/// </summary>
public class DispatcherPriorityTests
{
    [Fact]
    public Task Render_work_posted_later_still_runs_before_input_work() => Headless.RunSync(() =>
    {
        var order = new List<string>();

        Dispatcher.UIThread.Post(() => order.Add("input"), DispatcherPriority.Input);
        for (var i = 0; i < 20; i++) Dispatcher.UIThread.Post(() => order.Add("render"), DispatcherPriority.Render);

        Dispatcher.UIThread.RunJobs();

        Assert.Equal("render", order[0]);
        Assert.Equal("input", order[^1]);
    });

    [Fact]
    public Task A_pane_that_never_stops_repainting_starves_input_work_completely() => Headless.RunSync(() =>
    {
        // The reported case. The repaint reposts itself, standing in for an engine raising Updated
        // on every frame, and the job below Render never gets a turn.
        var focused = false;
        var repaints = 0;

        void Repaint()
        {
            if (++repaints >= 200) return;
            Dispatcher.UIThread.Post(Repaint, DispatcherPriority.Render);
        }

        Dispatcher.UIThread.Post(() => focused = true, DispatcherPriority.Input);
        Repaint();

        // Drain only the priorities at or above Render, which is what a busy dispatcher is doing.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.Equal(200, repaints);
        Assert.False(focused, "the input-priority job ran, so this is no longer the mechanism");
    });

    [Fact]
    public Task Work_at_the_repaint_s_own_priority_cannot_be_starved_by_it() => Headless.RunSync(() =>
    {
        // The fix in principle: at the same priority the queue is first-in-first-out, so work that
        // is already queued runs before repaints posted after it, however many of those arrive.
        var focused = false;
        var repaints = 0;

        void Repaint()
        {
            if (++repaints >= 200) return;
            Dispatcher.UIThread.Post(Repaint, DispatcherPriority.Render);
        }

        Dispatcher.UIThread.Post(() => focused = true, DispatcherPriority.Render);
        Repaint();

        Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

        Assert.True(focused);
    });
}
