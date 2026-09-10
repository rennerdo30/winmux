using System.Diagnostics;

namespace ReparentSpike;

/// <summary>
/// Diagnostic mode. Launches a target and dumps EVERY new top-level window that appears —
/// visible or not, owned or not, whatever process it belongs to. The adoption rule in
/// CLAUDE.md section 5 is a guess until you have actually looked at what an app creates.
/// </summary>
internal static class Probe
{
    public static int Run(string exe, string args, int watchMs)
    {
        Log.Line("probe: " + exe + " " + args + "   watching " + watchMs + "ms");

        var before = new HashSet<IntPtr>();
        Win32.EnumWindows((h, _) => { before.Add(h); return true; }, IntPtr.Zero);
        Log.Line("  " + before.Count + " top-level windows before launch");

        var spec = new TargetSpec { Name = "probe", Exe = exe, Args = args };
        var proc = Targets.Launch(spec);
        Log.Line("  launched pid " + (proc?.Id.ToString() ?? "?"));

        var reported = new HashSet<IntPtr>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < watchMs)
        {
            var fresh = new List<IntPtr>();
            Win32.EnumWindows((h, _) => { if (!before.Contains(h) && !reported.Contains(h)) fresh.Add(h); return true; }, IntPtr.Zero);
            foreach (var h in fresh)
            {
                reported.Add(h);
                Dump(h, sw.Elapsed.TotalMilliseconds);
            }
            Thread.Sleep(120);
        }

        Log.Line("  " + reported.Count + " new top-level window(s) in " + watchMs + "ms");
        try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
        return 0;
    }

    private static void Dump(IntPtr h, double atMs)
    {
        Win32.GetWindowThreadProcessId(h, out var pid);
        Win32.GetWindowRect(h, out var r);
        var style = Win32.GetWindowLongPtrW(h, Win32.GWL_STYLE).ToInt64();
        var ex = Win32.GetWindowLongPtrW(h, Win32.GWL_EXSTYLE).ToInt64();
        var owner = Win32.GetWindow(h, Win32.GW_OWNER);
        var parent = Win32.GetParent(h);

        Log.Line("  +" + atMs.ToString("F0").PadLeft(6) + "ms  hwnd=0x" + h.ToString("X"));
        Log.Line("            class=" + Win32Extra.GetClassName(h) +
                 "  title=\"" + Win32Extra.Truncate(Win32.GetWindowText(h), 46) + "\"");
        Log.Line("            pid=" + pid + " exe=" + Path.GetFileName(Win32Extra.GetWindowExe(h)) +
                 "  awareness=" + Win32Extra.GetWindowDpiAwareness(h));
        Log.Line("            visible=" + Win32.IsWindowVisible(h) +
                 " owner=0x" + owner.ToString("X") + " parent=0x" + parent.ToString("X") +
                 " rect=" + r +
                 "  style=0x" + style.ToString("X") + " ex=0x" + ex.ToString("X") +
                 (Win32Extra.LooksElevated(h) ? "  ELEVATED" : ""));
    }
}
