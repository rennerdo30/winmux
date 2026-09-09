using System.Diagnostics;

namespace HangSpike;

internal sealed class PaneHandle
{
    public int AppPid;
    public int HostPid;
    public IntPtr AppHwnd;
    public IntPtr HostHwnd;
    /// <summary>The window the shell drives to follow the pane rect.</summary>
    public IntPtr TrackHwnd;
    /// <summary>True when the shell itself called SetParent to make the pane a child of its own window.</summary>
    public bool IsChildOfShell;
    public EmbedRecord? ShellEmbed;

    public string Describe() =>
        "app pid=" + AppPid + " hwnd=0x" + AppHwnd.ToString("X") +
        (HostPid > 0 ? "  host pid=" + HostPid + " hwnd=0x" + HostHwnd.ToString("X") : "  host=none") +
        "  shellReparented=" + IsChildOfShell;

    public override string ToString() => Describe();
}

internal static class Scenarios
{
    public static string Describe(string s) => s switch
    {
        "T1" => "T1 attach-mode baseline: app stays top-level, shell only repositions it. No SetParent anywhere.",
        "T2" => "T2 in-process embed: the SHELL calls SetParent(app, shellHwnd). One process, direct attachment.",
        "T3" => "T3 OOP host, reparented: host SetParent(app, host); shell SetParent(host, shell). Tests TRANSITIVITY.",
        "T4" => "T4 OOP host, positioned: host SetParent(app, host); shell only repositions the host's top-level window.",
        "T5" => "T5 lifecycle: host process killed hard. Does the embedded app survive its host dying?",
        "T6" => "T6 lifecycle: host told to detach and quit cleanly. Is the app restored exactly?",
        _ => "unknown scenario " + s,
    };
}

internal static class PaneLauncher
{
    private static Process StartSelf(string logDir, params string[] extra)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var e in extra) psi.ArgumentList.Add(e);
        psi.ArgumentList.Add("--logdir"); psi.ArgumentList.Add(logDir);
        return Process.Start(psi)!;
    }

    private static Dictionary<string, string>? WaitReady(string logDir, int pid, int timeoutMs)
    {
        var path = Ipc.ReadyPath(logDir, pid);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var s = Ipc.TryRead(path);
            if (s != null && s.Contains("hwnd=", StringComparison.Ordinal)) return Ipc.Parse(s);
            Thread.Sleep(50);
        }
        return null;
    }

    public static PaneHandle? Launch(string scenario, string logDir, IntPtr shellHwnd)
    {
        const int paneW = 520, paneH = 340;

        if (scenario is "T1" or "T2")
        {
            var app = StartSelf(logDir, "--role", "hangapp", "--x", "700", "--y", "260", "--w", paneW.ToString(), "--h", paneH.ToString());
            var d = WaitReady(logDir, app.Id, 10000);
            if (d == null) { Log.Line("hangapp never reported ready"); return null; }
            var appHwnd = new IntPtr(long.Parse(d["hwnd"]));

            var h = new PaneHandle { AppPid = app.Id, AppHwnd = appHwnd, TrackHwnd = appHwnd };
            if (scenario == "T2")
            {
                h.ShellEmbed = Embedding.Embed(appHwnd, shellHwnd, paneW, paneH);
                h.IsChildOfShell = true;
            }
            return h;
        }

        if (scenario is "T3" or "T4" or "T5" or "T6")
        {
            var host = StartSelf(logDir, "--role", "panehost",
                "--x", "700", "--y", "260", "--w", paneW.ToString(), "--h", paneH.ToString());
            var d = WaitReady(logDir, host.Id, 20000);
            if (d == null) { Log.Line("panehost never reported ready"); return null; }

            var h = new PaneHandle
            {
                HostPid = host.Id,
                HostHwnd = new IntPtr(long.Parse(d["hwnd"])),
                AppPid = int.Parse(d["apppid"]),
                AppHwnd = new IntPtr(long.Parse(d["apphwnd"])),
            };
            h.TrackHwnd = h.HostHwnd;

            if (scenario == "T3")
            {
                // The trap: chaining SetParent shell -> host -> app. Attachment is transitive,
                // so this is expected to re-attach the shell to the hung app.
                h.ShellEmbed = Embedding.Embed(h.HostHwnd, shellHwnd, paneW, paneH);
                h.IsChildOfShell = true;
            }
            return h;
        }

        Log.Line("unknown scenario " + scenario);
        return null;
    }
}

internal static class LifecycleTest
{
    public static int Run(string scenario, string logDir, PaneHandle pane, NativeWindow shellWin)
    {
        Log.Line(Scenarios.Describe(scenario));
        var before = Snapshot(pane, "before");

        if (scenario == "T5")
        {
            Log.Line("killing host pid " + pane.HostPid + " hard (no tree kill, so the app itself is untouched)");
            try
            {
                var p = Process.GetProcessById(pane.HostPid);
                p.Kill(entireProcessTree: false);
                p.WaitForExit(5000);
            }
            catch (Exception e) { Log.Line("kill failed: " + e.Message); }
        }
        else // T6
        {
            Log.Line("asking host pid " + pane.HostPid + " to detach then quit");
            Ipc.Write(Ipc.CmdPath(logDir, pane.HostPid), "detach=1\n");
            Thread.Sleep(1200);
            Ipc.Write(Ipc.CmdPath(logDir, pane.HostPid), "quit=1\n");
        }

        // Pump for a bounded window. Must be PeekMessage, not GetMessage: this code path
        // sets no timer, so GetMessage blocks indefinitely and the observation window
        // silently stretches to minutes, which invalidates the whole measurement.
        PumpFor(2500);

        var after = Snapshot(pane, "after");

        var appAlive = IsAlive(pane.AppPid);
        var windowAlive = Win32.IsWindow(pane.AppHwnd);
        var parent = windowAlive ? Win32.GetParent(pane.AppHwnd) : IntPtr.Zero;
        var topLevel = windowAlive && parent == IntPtr.Zero;

        Log.Line("");
        Log.Line("---------------- RESULT " + scenario + " ----------------");
        Log.Line(before);
        Log.Line(after);
        Log.Line("app process alive : " + appAlive);
        Log.Line("app window alive  : " + windowAlive);
        Log.Line("app window parent : 0x" + parent.ToString("X") + (topLevel ? " (top-level, restored)" : ""));
        var pass = appAlive && windowAlive && topLevel;
        Log.Line("VERDICT " + scenario + ": " + (pass ? "APP SURVIVED" : "APP LOST") +
                 " (alive=" + appAlive + " window=" + windowAlive + " topLevel=" + topLevel + ")");
        Log.Line("-------------------------------------------");
        return pass ? 0 : 1;
    }

    private static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            while (Win32.PeekMessageW(out var m, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
            {
                Win32.TranslateMessage(ref m);
                Win32.DispatchMessageW(ref m);
            }
            Thread.Sleep(15);
        }
        Log.Line("observation window: pumped for " + sw.ElapsedMilliseconds + "ms");
    }

    private static string Snapshot(PaneHandle pane, string label)
    {
        var w = Win32.IsWindow(pane.AppHwnd);
        return label.PadRight(7) + " appWindow=" + w +
               " parent=0x" + (w ? Win32.GetParent(pane.AppHwnd).ToString("X") : "-") +
               " visible=" + (w && Win32.IsWindowVisible(pane.AppHwnd)) +
               " style=0x" + (w ? Win32.GetWindowLongPtrW(pane.AppHwnd, Win32.GWL_STYLE).ToInt64().ToString("X") : "-") +
               " hostAlive=" + IsAlive(pane.HostPid);
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
