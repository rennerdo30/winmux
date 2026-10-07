using Avalonia.Threading;

namespace WinMux.Shell;

/// <summary>
/// One pending repaint at a time, however often one is asked for.
///
/// <para>
/// A terminal engine raises its update event on the pty thread every time it takes a write, and a
/// program drawing a full screen takes many writes per frame. Posting a job for each one puts work
/// on the dispatcher faster than the dispatcher drains it, and the queue never empties.
/// </para>
///
/// <para>
/// That is not merely wasteful, and this is the part that took two attempts to find: <b>Avalonia
/// delivers input as dispatcher jobs too, below <c>Render</c></b>. A flood of repaints therefore
/// starves the click itself, not just whatever the click was going to do — which is why a tab took
/// two clicks to select while Claude Code was running, and why fixing the focus job alone changed
/// nothing.
/// </para>
///
/// <para>
/// Coalescing bounds the queue, but the priority must also yield to input: one Render job that
/// continually replaces itself can still starve Input forever. Nothing is lost by dropping
/// duplicate requests: the job uses the latest state when it runs.
/// </para>
/// </summary>
internal sealed class CoalescedRepaint
{
    private readonly Action _paint;
    private readonly DispatcherPriority _priority;
    private int _pending;

    public CoalescedRepaint(Action paint, DispatcherPriority? priority = null)
    {
        _paint = paint ?? throw new ArgumentNullException(nameof(paint));
        _priority = priority ?? DispatcherPriority.Background;
    }

    /// <summary>How many repaints have actually been drawn, for tests to check the coalescing.</summary>
    public int Painted { get; private set; }

    /// <summary>
    /// Ask for a repaint. Safe from any thread, which matters because the engine raises its update
    /// on whichever thread is reading the pty.
    /// </summary>
    public void Request()
    {
        // Already queued: that job has not run yet, so it will draw this update too.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;

        Dispatcher.UIThread.Post(
            () =>
            {
                // Cleared before painting, not after. A write that lands while this is drawing is a
                // change this frame will not show, and must be able to queue the next one.
                Volatile.Write(ref _pending, 0);
                Painted++;
                _paint();
            },
            _priority);
    }
}
