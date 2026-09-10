using System.Collections.Concurrent;
using System.Diagnostics;
using WinMux.Core.Model;

namespace WinMux.Shell;

/// <summary>
/// Drives foreign application windows to follow their pane rectangles, on a thread of its own.
///
/// This is the direct consequence of ADR 0001, the most expensive finding of Phase 0: a
/// synchronous cross-process window call blocks the caller for as long as the target app is
/// wedged. `SetWindowPos` against a hung app froze the measured shell for a full six seconds, and
/// it hits attach mode exactly as hard as embed mode. So the UI thread never touches a foreign
/// window — it posts a desired rectangle here, and this thread applies it with SWP_ASYNCWINDOWPOS.
///
/// A wedged app can therefore stall only this thread, and the UI keeps painting.
/// </summary>
internal sealed class ForeignWindowTracker : IDisposable
{
    private readonly record struct Placement(IntPtr Hwnd, int X, int Y, int Width, int Height, bool Visible, bool Child);

    private readonly ConcurrentDictionary<PaneId, Placement> _wanted = new();
    private readonly ConcurrentDictionary<PaneId, Placement> _applied = new();
    private readonly HashSet<PaneId> _nudged = [];
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stopping;

    /// <summary>How long a single window call took, worst case, since the last reset.</summary>
    public double WorstCallMs { get; private set; }

    public ForeignWindowTracker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "winmux-foreign-layout" };
        _thread.Start();
    }

    /// <summary>Called from the UI thread. Never blocks on anything cross-process.</summary>
    public void Place(PaneId pane, IntPtr hwnd, int x, int y, int width, int height, bool visible, bool child)
    {
        _wanted[pane] = new Placement(hwnd, x, y, width, height, visible, child);
        _wake.Set();
    }

    public void Forget(PaneId pane)
    {
        _wanted.TryRemove(pane, out _);
        _applied.TryRemove(pane, out _);
        lock (_nudged) _nudged.Remove(pane);
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

            foreach (var (pane, want) in _wanted)
            {
                if (_stopping) return;
                if (_applied.TryGetValue(pane, out var already) && already == want) continue;
                if (!Win32Interop.IsWindow(want.Hwnd)) { _applied.TryRemove(pane, out _); continue; }

                var sw = Stopwatch.StartNew();
                try
                {
                    if (!want.Visible)
                    {
                        Win32Interop.ShowWindow(want.Hwnd, Win32Interop.SW_HIDE);
                    }
                    else
                    {
                        if (Win32Interop.IsIconic(want.Hwnd)) Win32Interop.ShowWindow(want.Hwnd, Win32Interop.SW_RESTORE);
                        // An embedded child is positioned in the parent's CLIENT coordinates and
                        // needs no z-order games; it is clipped by the parent like any other child.
                        // SWP_NOCOPYBITS stops Windows preserving the old client bits, which is
                        // what leaves a reparented window showing whatever was on screen before.
                        var flags = (want.Child ? Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOCOPYBITS : 0) |
                                    Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW;
                        var after = want.Child ? IntPtr.Zero : Win32Interop.HWND_TOP;

                        if (want.Child && _nudged.Add(pane) && want.Width > 2 && want.Height > 2)
                        {
                            // One deliberate off-by-one resize the first time. A modern app hosting
                            // XAML content re-lays-out on WM_SIZE and otherwise never repaints after
                            // being reparented; giving it a size it has not seen forces that.
                            Win32Interop.SetWindowPos(want.Hwnd, after, want.X, want.Y,
                                want.Width - 1, want.Height - 1, flags);

                            // Hide/show as well. Content drawn through DirectComposition (Win11
                            // Explorer, anything XAML) keeps a visual tree bound to its old
                            // composition target after a reparent, and a plain repaint request
                            // never reaches it; re-showing the window rebuilds that binding.
                            Win32Interop.ShowWindow(want.Hwnd, Win32Interop.SW_HIDE);
                            Win32Interop.ShowWindow(want.Hwnd, Win32Interop.SW_SHOWNA);
                        }

                        Win32Interop.SetWindowPos(want.Hwnd, after, want.X, want.Y, want.Width, want.Height, flags);

                        // Force the repaint. Without it a freshly reparented window shows whatever
                        // was on screen behind it until something else happens to invalidate it.
                        Win32Interop.RedrawWindow(want.Hwnd, IntPtr.Zero, IntPtr.Zero, Win32Interop.RDW_FULL);
                    }
                    _applied[pane] = want;
                }
                catch (Exception)
                {
                    // A window that vanished mid-call is normal; retry on the next tick.
                    _applied.TryRemove(pane, out _);
                }
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds > WorstCallMs) WorstCallMs = sw.Elapsed.TotalMilliseconds;
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(1000);
        _wake.Dispose();
    }
}
