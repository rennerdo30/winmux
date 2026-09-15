using System.Collections.Concurrent;
using System.Diagnostics;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Platform;

namespace WinMux.Shell;

/// <summary>
/// Drives foreign application windows to follow their pane rectangles, on a thread of its own.
///
/// This is the direct consequence of ADR 0001, the most expensive finding of Phase 0: a
/// synchronous cross-process window call blocks the caller for as long as the target app is
/// wedged. `SetWindowPos` against a hung app froze the measured shell for a full six seconds, and
/// it hits attach mode exactly as hard as embed mode. So the UI thread never touches a foreign
/// window — it posts a desired rectangle here, and this thread hands it to the platform.
///
/// A wedged app can therefore stall only this thread, and the UI keeps painting.
///
/// What is left here after the Phase 5 extraction is the *policy*: which window should be where,
/// what has already been applied, and which placement is a window's first. None of that is
/// Windows-specific, and it is now testable without a single real window
/// (see <c>ForeignWindowTrackerTests</c>). How to make a window actually obey lives behind
/// <see cref="IHostWindowService"/>.
/// </summary>
internal sealed class ForeignWindowTracker : IDisposable
{
    private readonly record struct Placement(WindowHandle Window, Rect Bounds, bool Visible, bool Child);

    private readonly IHostWindowService _windows;
    private readonly ConcurrentDictionary<PaneId, Placement> _wanted = new();
    private readonly ConcurrentDictionary<PaneId, Placement> _applied = new();
    private readonly HashSet<PaneId> _placedOnce = [];
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stopping;

    /// <summary>How long a single window call took, worst case, since the last reset.</summary>
    public double WorstCallMs { get; private set; }

    public ForeignWindowTracker(IHostWindowService windows) : this(windows, runThread: true) { }

    /// <summary>
    /// With <paramref name="runThread"/> false the tracker does nothing until <see cref="ApplyPending"/>
    /// is called. That is how the tests drive it: the placement policy is worth asserting on exactly,
    /// and a background thread would make every assertion a race.
    /// </summary>
    internal ForeignWindowTracker(IHostWindowService windows, bool runThread)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        if (!runThread) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "winmux-foreign-layout" };
        _thread.Start();
    }

    /// <summary>Called from the UI thread. Never blocks on anything cross-process.</summary>
    public void Place(PaneId pane, WindowHandle window, Rect bounds, bool visible, bool child)
    {
        _wanted[pane] = new Placement(window, bounds, visible, child);
        _wake.Set();
    }

    public void Forget(PaneId pane)
    {
        _wanted.TryRemove(pane, out _);
        _applied.TryRemove(pane, out _);
        lock (_placedOnce) _placedOnce.Remove(pane);
    }

    /// <summary>
    /// Re-assert every placement, including z-order. Attach-mode windows are separate top-level
    /// windows, so they do not ride the shell's z-order for free: activating the shell would
    /// otherwise bury them behind it. Called when the shell is activated.
    /// </summary>
    public void Refresh()
    {
        _applied.Clear();
        _wake.Set();
    }

    private void Loop()
    {
        while (!_stopping)
        {
            _wake.Wait(200);
            _wake.Reset();
            ApplyPending();
        }
    }

    /// <summary>One pass over every wanted placement. The whole of the tracker's work.</summary>
    internal void ApplyPending()
    {
        foreach (var (pane, want) in _wanted)
        {
            if (_stopping) return;
            Apply(pane, want);
        }
    }

    private void Apply(PaneId pane, in Placement want)
    {
        if (_applied.TryGetValue(pane, out var already) && already == want) return;
        if (!_windows.IsAlive(want.Window)) { _applied.TryRemove(pane, out _); return; }

        // Only an embedded window that is actually being shown can spend its one first-placement
        // licence; a hidden pane does no adoption work, so the flag must survive until it appears.
        var firstPlacement = want.Child && want.Visible && MarkPlaced(pane);
        var mode = want.Child ? WindowPlacementMode.EmbeddedChild : WindowPlacementMode.FloatingTopLevel;

        var sw = Stopwatch.StartNew();
        try
        {
            _windows.Apply(want.Window, new WindowPlacement(want.Bounds, mode, want.Visible, firstPlacement));
            _applied[pane] = want;
        }
        catch (Exception)
        {
            // A window that vanished mid-call is normal; retry on the next tick — and give the
            // first-placement licence back, because the work it pays for never happened.
            _applied.TryRemove(pane, out _);
            if (firstPlacement) lock (_placedOnce) _placedOnce.Remove(pane);
        }
        sw.Stop();
        if (sw.Elapsed.TotalMilliseconds > WorstCallMs) WorstCallMs = sw.Elapsed.TotalMilliseconds;
    }

    private bool MarkPlaced(PaneId pane)
    {
        lock (_placedOnce) return _placedOnce.Add(pane);
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(1000);
        _wake.Dispose();
    }
}
