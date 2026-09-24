using Avalonia.Threading;

namespace WinMux.Shell.Tests;

/// <summary>
/// One queued repaint at a time.
///
/// <para>
/// Reported twice: a tab took two clicks to select while Claude Code was running. The first attempt
/// fixed the job that <em>moved the keyboard</em> after a tab was selected, and it changed nothing —
/// because Avalonia delivers input as dispatcher jobs below <c>Render</c>, so a pane posting a
/// repaint per engine update was starving the click itself.
/// </para>
///
/// <para>
/// So this is the fix at the source, and these tests are about the queue rather than about drawing:
/// however many updates arrive, at most one job is waiting.
/// </para>
/// </summary>
public class CoalescedRepaintTests
{
    [Fact]
    public Task One_request_paints_once() => Headless.RunSync(() =>
    {
        var painted = 0;
        var repaint = new CoalescedRepaint(() => painted++);

        repaint.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, painted);
    });

    [Fact]
    public Task A_thousand_requests_before_it_runs_paint_once() => Headless.RunSync(() =>
    {
        // The reported case: a full-screen program takes many writes per frame, and the old code
        // queued a job for each of them.
        var painted = 0;
        var repaint = new CoalescedRepaint(() => painted++);

        for (var i = 0; i < 1000; i++) repaint.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, painted);
    });

    [Fact]
    public Task A_request_after_a_paint_paints_again() => Headless.RunSync(() =>
    {
        // Coalescing must not swallow the next frame: output that arrives after a repaint has run
        // is output nobody has drawn yet.
        var painted = 0;
        var repaint = new CoalescedRepaint(() => painted++);

        repaint.Request();
        Dispatcher.UIThread.RunJobs();
        repaint.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, painted);
    });

    [Fact]
    public Task A_request_made_while_painting_is_not_lost() => Headless.RunSync(() =>
    {
        // A write landing while the frame is being drawn is a change that frame does not show, so
        // it has to be able to queue the next one. That is why the flag is cleared before painting
        // rather than after.
        var painted = 0;
        CoalescedRepaint? repaint = null;
        repaint = new CoalescedRepaint(() =>
        {
            painted++;
            if (painted == 1) repaint!.Request();
        });

        repaint.Request();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, painted);
    });

    [Fact]
    public Task Input_gets_a_turn_while_repaints_keep_arriving() => Headless.RunSync(() =>
    {
        // The property that actually matters. A repaint that reposts itself is a program drawing
        // without pause; with one job queued at a time the input below it still runs.
        var clicked = false;
        var painted = 0;
        CoalescedRepaint? repaint = null;
        repaint = new CoalescedRepaint(() =>
        {
            if (++painted < 200) repaint!.Request();
        });

        Dispatcher.UIThread.Post(() => clicked = true, DispatcherPriority.Input);
        repaint.Request();

        Dispatcher.UIThread.RunJobs();

        Assert.True(clicked, "the input-priority job never ran, so repaints are still starving input");
        Assert.Equal(200, painted);
    });
}
