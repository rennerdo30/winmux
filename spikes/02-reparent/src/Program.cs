using System.Diagnostics;
using System.Text;

namespace ReparentSpike;

internal sealed class TargetResult
{
    public required string Name;
    public string WindowClass = "";
    public string Exe = "";
    public string Awareness = "";
    public double FindMs;
    public int Candidates;
    public bool Embedded;
    public bool ResizeFollows;
    public bool MoveFollows;
    public bool Detached;
    public bool RestoredExactly;
    public bool StillAlive;
    public string MinSize = "";
    public string Notes = "";
    public TargetSpec? Spec;

    public string Verdict =>
        !Embedded ? "EMBED FAILED" :
        !Detached ? "DETACH FAILED" :
        !RestoredExactly || !StillAlive ? "EMBEDS, UNCLEAN DETACH" :
        !ResizeFollows || !MoveFollows ? "EMBEDS, GEOMETRY QUIRKS" :
        "OK";
}

internal static class Program
{
    private const int HostX = 200, HostY = 150, HostW = 900, HostH = 620;
    private static NativeWindow _host = null!;
    private static readonly List<TargetResult> Results = [];

    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var a = new Args(argv);
        var only = a.Str("only", "");
        Log.Init("reparent", a.Str("logdir", Path.Combine(Path.GetTempPath(), "winmux-spike2")));

        if (a.Str("probe", "").Length > 0)
            return Probe.Run(a.Str("probe", ""), a.Str("probeargs", ""), a.Int("watchms", 12000));
        if (a.Str("role", "") == "guineapig") return GuineaPig.Run(a);

        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var mixed = !a.Flag("nomixed");
        var prev = mixed ? Win32.SetThreadDpiHostingBehavior(Win32.DPI_HOSTING_BEHAVIOR_MIXED) : -1;
        Log.Line("host: PerMonitorV2, DpiHostingBehavior=" + (mixed ? "MIXED (previous=" + prev + ")" : "DEFAULT (--nomixed)"));

        _host = new NativeWindow(
            className: "WinMuxSpike2Host",
            title: "WinMux reparent spike host",
            style: Win32.WS_POPUP | Win32.WS_CLIPCHILDREN | Win32.WS_VISIBLE,
            exStyle: Win32.WS_EX_TOOLWINDOW,
            x: HostX, y: HostY, w: HostW, h: HostH,
            parent: IntPtr.Zero,
            bg: Win32.Rgb(0x1e, 0x1e, 0x2e),
            fg: Win32.Rgb(0xcd, 0xd6, 0xf4));
        _host.Text = "WinMux spike 2 — reparent host";
        _host.Show();
        Log.Line("host window " + Win32Extra.Describe(_host.Handle));

        var targets = Targets.Build();
        if (only.Length > 0)
            targets = targets.Where(t => t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        var worker = new Thread(() =>
        {
            if (mixed) Win32.SetThreadDpiHostingBehavior(Win32.DPI_HOSTING_BEHAVIOR_MIXED);
            foreach (var t in targets)
            {
                try { Results.Add(RunTarget(t)); }
                catch (Exception e)
                {
                    Log.Line("!! " + t.Name + " threw: " + e.Message);
                    Results.Add(new TargetResult { Name = t.Name, Notes = "threw: " + e.Message });
                }
            }
            Win32.PostMessageW(_host.Handle, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "spike2-driver" };
        worker.Start();

        NativeWindow.Pump();
        Report();
        return Results.Count(r => r.Verdict is "EMBED FAILED" or "DETACH FAILED");
    }

    private static TargetResult RunTarget(TargetSpec t)
    {
        Log.Line("");
        Log.Line("======== " + t.Name + " ========");
        Log.Line("  why: " + t.Why);
        var res = new TargetResult { Name = t.Name, Spec = t };

        var before = SnapshotAdoptable(t);
        var proc = Targets.Launch(t);
        Log.Line("  launched " + Path.GetFileName(t.Exe) + (proc != null ? " pid " + proc.Id : "") +
                 (t.ForceDpiUnaware ? " (__COMPAT_LAYER=DPIUNAWARE)" : ""));

        var sw = Stopwatch.StartNew();
        var hwnd = WaitForNewWindow(t, proc, before, out var candidates, out var candidateList);
        res.FindMs = sw.Elapsed.TotalMilliseconds;
        res.Candidates = candidates;

        if (hwnd == IntPtr.Zero)
        {
            res.Notes = "no adoptable window within " + t.TimeoutMs + "ms";
            Log.Line("  FAILED: " + res.Notes);
            return res;
        }

        Thread.Sleep(t.SettleMs); // let splash screens resolve into the real window
        res.WindowClass = Win32Extra.GetClassName(hwnd);
        res.Exe = Path.GetFileName(Win32Extra.GetWindowExe(hwnd));
        res.Awareness = Win32Extra.GetWindowDpiAwareness(hwnd);
        Log.Line("  window found in " + res.FindMs.ToString("F0") + "ms after " + candidates + " candidate(s)");
        if (candidateList.Count > 1)
            foreach (var c in candidateList) Log.Line("    candidate: " + Win32Extra.Describe(c));
        Log.Line("  adopting " + Win32Extra.Describe(hwnd));

        if (Win32Extra.LooksElevated(hwnd))
        {
            res.Notes = "process not openable — elevated; UIPI forbids reparenting from a non-elevated host";
            Log.Line("  SKIPPED: " + res.Notes);
            return res;
        }

        // ---- embed ----
        Win32.GetClientRect(_host.Handle, out var hostClient);
        var rec = Embedding.Embed(hwnd, _host.Handle, hostClient.Width, hostClient.Height);
        Thread.Sleep(500);

        var parent = Win32Extra.TrueParent(hwnd);
        res.Embedded = parent == _host.Handle;
        Log.Line("  embedded: " + res.Embedded + " (parent=0x" + parent.ToString("X") + ")");
        if (!res.Embedded)
        {
            var err = Embedding.LastSetParentError;
            res.Notes = err switch
            {
                5 => "UIPI: SetParent refused with ERROR_ACCESS_DENIED (5)",
                87 => "SetParent refused with ERROR_INVALID_PARAMETER (87) — the window rejects reparenting",
                0 => "SetParent silently refused: no error set, window stayed top-level",
                _ => "SetParent refused with error " + err,
            };
            Embedding.Detach(rec);
            CloseTarget(hwnd, proc);
            return res;
        }

        // ---- resize ----
        (int w, int h)[] sizes = [(700, 480), (420, 300), (880, 600)];
        var resizeOk = true;
        var minNote = "";
        foreach (var (w, h) in sizes)
        {
            Win32.SetWindowPos(_host.Handle, IntPtr.Zero, HostX, HostY, w, h,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            Win32.GetClientRect(_host.Handle, out var hc);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, hc.Width, hc.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            Thread.Sleep(350);
            Win32.GetWindowRect(hwnd, out var cr);
            var follows = Math.Abs(cr.Width - hc.Width) <= 4 && Math.Abs(cr.Height - hc.Height) <= 4;
            if (!follows)
            {
                resizeOk = false;
                minNote = "asked " + hc.Width + "x" + hc.Height + ", got " + cr.Width + "x" + cr.Height;
            }
            Log.Line("  resize to " + hc.Width + "x" + hc.Height + " -> child " + cr.Width + "x" + cr.Height +
                     (follows ? " ok" : "  <-- does not follow (minimum size?)"));
        }
        res.ResizeFollows = resizeOk;
        res.MinSize = minNote;

        // ---- move ----
        Win32.SetWindowPos(_host.Handle, IntPtr.Zero, HostX + 260, HostY + 120, 880, 600,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Thread.Sleep(350);
        Win32.GetWindowRect(_host.Handle, out var hostRect);
        Win32.GetWindowRect(hwnd, out var childRect);
        res.MoveFollows = Math.Abs(childRect.left - hostRect.left) <= 6 && Math.Abs(childRect.top - hostRect.top) <= 6;
        Log.Line("  move: host at " + hostRect + ", child at " + childRect + " -> follows: " + res.MoveFollows);
        Win32.SetWindowPos(_host.Handle, IntPtr.Zero, HostX, HostY, HostW, HostH,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        // ---- detach, and verify the restore is exact ----
        Embedding.Detach(rec);
        Thread.Sleep(600);

        res.Detached = Win32.IsWindow(hwnd) && Win32Extra.IsTopLevel(hwnd);
        var style = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_STYLE);
        var exStyle = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        Win32.GetWindowRect(hwnd, out var after);
        var o = rec.OriginalRect;
        var rectOk = Math.Abs(after.left - o.left) <= 8 && Math.Abs(after.top - o.top) <= 8 &&
                     Math.Abs(after.Width - o.Width) <= 8 && Math.Abs(after.Height - o.Height) <= 8;
        var styleOk = style == rec.OriginalStyle && exStyle == rec.OriginalExStyle;
        res.RestoredExactly = res.Detached && styleOk && rectOk && Win32.IsWindowVisible(hwnd);
        res.StillAlive = Win32.IsWindow(hwnd);

        Log.Line("  detached: " + res.Detached +
                 "  style " + (styleOk ? "exact" : "0x" + style.ToInt64().ToString("X") + " vs 0x" + rec.OriginalStyle.ToInt64().ToString("X")) +
                 "  rect " + (rectOk ? "exact" : after + " vs " + o) +
                 "  visible " + Win32.IsWindowVisible(hwnd));

        CloseTarget(hwnd, proc);
        return res;
    }

    /// <summary>Windows that already satisfy the adoption rule, so a newly created one can be told apart.</summary>
    private static HashSet<IntPtr> SnapshotAdoptable(TargetSpec t)
    {
        var set = new HashSet<IntPtr>();
        Win32.EnumWindows((h, _) => { if (IsAdoptable(h, t)) set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    /// <summary>CLAUDE.md section 5: visible, top-level, non-owned, with a real title.</summary>
    private static bool IsAdoptable(IntPtr h, TargetSpec t)
    {
        if (!Win32.IsWindowVisible(h)) return false;
        if (!Win32Extra.IsTopLevel(h)) return false;
        if (Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero) return false;
        if (Win32.GetWindowText(h).Trim().Length == 0) return false;
        if (t.Match == MatchMode.ClassName &&
            !Win32Extra.GetClassName(h).Equals(t.ClassName, StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Match == MatchMode.ProcessName &&
            !Path.GetFileName(Win32Extra.GetWindowExe(h)).Equals(t.ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
        Win32.GetWindowRect(h, out var r);
        return r.Width > 120 && r.Height > 80;   // skip tool/helper slivers
    }

    private static IntPtr WaitForNewWindow(TargetSpec t, Process? proc, HashSet<IntPtr> before,
                                           out int candidates, out List<IntPtr> seen)
    {
        var sw = Stopwatch.StartNew();
        var found = new List<IntPtr>();
        while (sw.ElapsedMilliseconds < t.TimeoutMs)
        {
            var now = new List<IntPtr>();
            Win32.EnumWindows((h, _) =>
            {
                if (before.Contains(h) || !IsAdoptable(h, t)) return true;
                if (t.Match == MatchMode.Pid && proc != null)
                {
                    Win32.GetWindowThreadProcessId(h, out var pid);
                    if (pid != (uint)proc.Id) return true;
                }
                now.Add(h);
                return true;
            }, IntPtr.Zero);

            foreach (var h in now) if (!found.Contains(h)) found.Add(h);
            if (found.Count > 0 && sw.ElapsedMilliseconds > 400)
            {
                candidates = found.Count;
                seen = found;
                return found[^1];   // last to appear is the real window, not the splash
            }
            Thread.Sleep(80);
        }
        candidates = found.Count;
        seen = found;
        return found.Count > 0 ? found[^1] : IntPtr.Zero;
    }

    private static void CloseTarget(IntPtr hwnd, Process? proc)
    {
        if (Win32.IsWindow(hwnd)) Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(900);
        try { if (proc is { HasExited: false }) { proc.Kill(entireProcessTree: true); proc.WaitForExit(2000); } }
        catch { }
    }

    /// <summary>
    /// Emit the quirks database seed (CLAUDE.md section 5). Verified-app coverage is a documented
    /// feature, so the evidence has to leave the spike in machine-readable form, not just a log.
    /// </summary>
    private static void WriteQuirksSeed()
    {
        var entries = Results.Select(r => new
        {
            match = new { exe = r.Exe, windowClass = r.WindowClass },
            strategy = r.Embedded ? "embed" : "attach",
            windowSelection = new
            {
                mode = r.Spec?.Match.ToString() ?? "Pid",
                processName = r.Spec?.ProcessName ?? "",
                className = r.Spec?.ClassName ?? "",
            },
            launchDelayMs = r.Spec?.SettleMs ?? 0,
            observedFindMs = Math.Round(r.FindMs),
            dpiAwareness = r.Awareness,
            limitations = r.Notes.Length > 0 ? r.Notes : null,
            verified = new
            {
                date = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                os = Environment.OSVersion.Version.ToString(),
                result = r.Verdict,
                resizeFollows = r.ResizeFollows,
                moveFollows = r.MoveFollows,
                detachExact = r.RestoredExactly,
            },
        }).ToList();

        var doc = new { version = 1, generatedBy = "spikes/02-reparent", entries };
        var json = System.Text.Json.JsonSerializer.Serialize(doc,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(AppContext.BaseDirectory, "quirks-seed.json");
        File.WriteAllText(path, json);
        Log.Line("quirks seed written to " + path);
    }

    private static void Report()
    {
        Log.Line("");
        Log.Line("================== SPIKE 2 SUMMARY ==================");
        Log.Line(string.Format("{0,-30} {1,-24} {2,-12} {3,-8} {4}", "target", "window class", "awareness", "find ms", "verdict"));
        foreach (var r in Results)
            Log.Line(string.Format("{0,-30} {1,-24} {2,-12} {3,-8} {4}",
                Win32Extra.Truncate(r.Name, 29), Win32Extra.Truncate(r.WindowClass, 23),
                r.Awareness, r.FindMs.ToString("F0"), r.Verdict));

        Log.Line("");
        foreach (var r in Results)
        {
            var bits = new List<string>();
            if (r.Candidates > 1) bits.Add(r.Candidates + " candidate windows");
            if (r.MinSize.Length > 0) bits.Add("min size: " + r.MinSize);
            if (r.Notes.Length > 0) bits.Add(r.Notes);
            if (!r.RestoredExactly && r.Embedded) bits.Add("restore not byte-exact");
            if (bits.Count > 0) Log.Line("  " + r.Name + ": " + string.Join("; ", bits));
        }
        Log.Line("=====================================================");
        WriteQuirksSeed();
    }
}
