using System.Diagnostics;

namespace HangSpike;

/// <summary>
/// Stand-in for the WinMux shell. Runs one scenario end to end, samples its own
/// responsiveness from two vantage points, and prints a machine-readable verdict.
/// </summary>
internal static class Shell
{
    // Shell window geometry, and the pane rectangle inside it.
    private const int SX = 60, SY = 60, SW = 900, SH = 620;
    private const int PANE_CX = 20, PANE_CY = 140;          // client coords, for child panes
    private const int PANE_W = 520, PANE_H = 340;
    private static int PaneScreenX => SX + PANE_CX + 8;      // screen coords, for top-level panes
    private static int PaneScreenY => SY + PANE_CY + 30;

    private sealed class Samples
    {
        public readonly List<double> UiGapMs = new();
        public readonly List<double> LayoutCallMs = new();
        public int WatchdogSamples, WatchdogHung, WatchdogSendFailed;
        public double WatchdogMaxSendMs;

        public double MaxGap => UiGapMs.Count == 0 ? 0 : UiGapMs.Max();
        public double MaxLayout => LayoutCallMs.Count == 0 ? 0 : LayoutCallMs.Max();

        public string Describe(string label) =>
            label.PadRight(9) +
            " uiTicks=" + UiGapMs.Count.ToString().PadLeft(4) +
            " maxUiGap=" + MaxGap.ToString("F0").PadLeft(6) + "ms" +
            " maxLayoutCall=" + MaxLayout.ToString("F0").PadLeft(6) + "ms" +
            " watchdog=" + WatchdogSamples.ToString().PadLeft(3) +
            " hung=" + WatchdogHung.ToString().PadLeft(3) +
            " sendFail=" + WatchdogSendFailed.ToString().PadLeft(3) +
            " maxSend=" + WatchdogMaxSendMs.ToString("F0").PadLeft(5) + "ms";
    }

    private enum Phase { Warmup, Baseline, Hang, Done }

    public static int Run(Args a)
    {
        var scenario = a.Str("scenario", "T4").ToUpperInvariant();
        var logDir = a.Str("logdir", ".");
        var hangMs = a.Int("hangms", 6000);
        var asyncPos = a.Flag("asyncpos");
        var noLayout = a.Flag("nolayout");
        Log.Init("shell", logDir);
        var mode = noLayout ? "nolayout" : asyncPos ? "async" : "sync";
        Log.Line("scenario=" + scenario + " hangMs=" + hangMs + " layoutMode=" + mode);

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (scenario is "T2" or "T3")
        {
            var prev = Win32.SetThreadDpiHostingBehavior(Win32.DPI_HOSTING_BEHAVIOR_MIXED);
            Log.Line("shell SetThreadDpiHostingBehavior(MIXED) previous=" + prev);
        }

        var shellWin = new NativeWindow(
            className: "WinMuxSpikeShell",
            title: "WinMux Spike Shell " + scenario,
            style: Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN,
            exStyle: 0,
            x: SX, y: SY, w: SW, h: SH,
            parent: IntPtr.Zero,
            bg: Win32.Rgb(0x18, 0x18, 0x25),
            fg: Win32.Rgb(0xf9, 0xe2, 0xaf));
        shellWin.Show();

        var baseline = new Samples();
        var hang = new Samples();
        var phase = Phase.Warmup;
        var phaseLock = new object();
        Samples Current() { lock (phaseLock) { return phase == Phase.Hang ? hang : baseline; } }

        // ---- watchdog: a background thread, which owns no windows and is therefore
        // ---- never part of any attached input queue. This is the honest observer.
        var stop = false;
        var watchdog = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                lock (phaseLock) { if (phase is Phase.Warmup or Phase.Done) { Monitor.Wait(phaseLock, 50); continue; } }
                var s = Current();
                var sw = Stopwatch.StartNew();
                var ok = Win32.SendMessageTimeoutW(shellWin.Handle, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero,
                             Win32.SMTO_ABORTIFHUNG | Win32.SMTO_BLOCK, 250, out _) != IntPtr.Zero;
                sw.Stop();
                var hungFlag = Win32.IsHungAppWindow(shellWin.Handle);
                lock (s)
                {
                    s.WatchdogSamples++;
                    if (!ok) s.WatchdogSendFailed++;
                    if (hungFlag) s.WatchdogHung++;
                    s.WatchdogMaxSendMs = Math.Max(s.WatchdogMaxSendMs, sw.Elapsed.TotalMilliseconds);
                }
                Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "watchdog" };
        watchdog.Start();

        // ---- launch the pane per scenario ----
        var pane = PaneLauncher.Launch(scenario, logDir, shellWin.Handle);
        if (pane == null) { Log.Line("FATAL: pane launch failed"); return 2; }
        Log.Line("pane ready: " + pane);

        // T5/T6 are lifecycle tests, not responsiveness tests.
        if (scenario is "T5" or "T6")
        {
            var rc = LifecycleTest.Run(scenario, logDir, pane, shellWin);
            Volatile.Write(ref stop, true);
            Cleanup(pane);
            return rc;
        }

        var trackHwnd = pane.TrackHwnd;
        var isChild = pane.IsChildOfShell;
        int px = isChild ? PANE_CX : PaneScreenX;
        int py = isChild ? PANE_CY : PaneScreenY;

        var ticks = 0;
        double lastTickMs = Log.Clock.Elapsed.TotalMilliseconds;
        var runSw = Stopwatch.StartNew();
        double hangRequestedAt = -1;

        shellWin.OnMessage = (msg, _, _) =>
        {
            if (msg != Win32.WM_TIMER) return null;
            ticks++;

            var nowMs = Log.Clock.Elapsed.TotalMilliseconds;
            var gap = nowMs - lastTickMs;
            lastTickMs = nowMs;

            // The layout operation a real shell performs constantly: move/resize the pane.
            // Skipping it (--nolayout) isolates input-queue attachment from the separate
            // failure mode of blocking inside a synchronous cross-process window call.
            var lsw = Stopwatch.StartNew();
            if (!noLayout)
            {
                uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | (asyncPos ? Win32.SWP_ASYNCWINDOWPOS : 0);
                Win32.SetWindowPos(trackHwnd, IntPtr.Zero, px, py, PANE_W + (ticks % 2), PANE_H, flags);
            }
            lsw.Stop();

            var s = Current();
            lock (s) { s.UiGapMs.Add(gap); s.LayoutCallMs.Add(lsw.Elapsed.TotalMilliseconds); }

            shellWin.Text =
                "WinMux spike shell — scenario " + scenario + "\n" +
                "phase " + phase + "   ticks " + ticks + "   t+" + runSw.Elapsed.TotalSeconds.ToString("F1") + "s\n" +
                "pane " + pane.Describe() + "\n" +
                "last ui gap " + gap.ToString("F0") + "ms   last layout call " +
                lsw.Elapsed.TotalMilliseconds.ToString("F0") + "ms";
            shellWin.Repaint();

            var t = runSw.Elapsed.TotalMilliseconds;
            lock (phaseLock)
            {
                if (phase == Phase.Warmup && t > 700) { phase = Phase.Baseline; Log.Line("PHASE baseline"); }
                else if (phase == Phase.Baseline && t > 2400)
                {
                    phase = Phase.Hang;
                    hangRequestedAt = t;
                    Ipc.Write(Ipc.CmdPath(logDir, pane.AppPid), "hang=" + hangMs + "\n");
                    Log.Line("PHASE hang — requested " + hangMs + "ms hang from app pid " + pane.AppPid);
                }
                else if (phase == Phase.Hang && t > hangRequestedAt + hangMs + 2500)
                {
                    phase = Phase.Done;
                    Log.Line("PHASE done");
                    Win32.PostMessageW(shellWin.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            return IntPtr.Zero;
        };

        Win32.SetTimer(shellWin.Handle, new IntPtr(1), 50, IntPtr.Zero);
        NativeWindow.Pump();
        Volatile.Write(ref stop, true);

        // ---- verdict ----
        var frozen = hang.MaxGap > 1000 || hang.WatchdogSendFailed > 0 || hang.WatchdogHung > 0 || hang.MaxLayout > 1000;
        Log.Line("");
        Log.Line("---------------- RESULT " + scenario + " [" + mode + "] ----------------");
        Log.Line(Scenarios.Describe(scenario));
        Log.Line(baseline.Describe("BASELINE"));
        Log.Line(hang.Describe("HANG"));
        Log.Line("VERDICT " + scenario + " [" + mode + "]: SHELL " + (frozen ? "FROZEN" : "RESPONSIVE") +
                 "  (maxUiGap=" + hang.MaxGap.ToString("F0") + "ms" +
                 " maxLayoutCall=" + hang.MaxLayout.ToString("F0") + "ms" +
                 " hungSamples=" + hang.WatchdogHung + "/" + hang.WatchdogSamples + ")");
        Log.Line("-------------------------------------------");

        Cleanup(pane);
        return frozen ? 1 : 0;
    }

    private static void Cleanup(PaneHandle pane)
    {
        foreach (var pid in new[] { pane.AppPid, pane.HostPid })
        {
            if (pid <= 0) continue;
            try { var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception e) { Log.Line("cleanup pid " + pid + ": " + e.Message); }
        }
    }
}
