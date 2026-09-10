using System.Diagnostics;

namespace ReparentSpike;

internal enum MatchMode
{
    /// <summary>The window belongs to the process we launched.</summary>
    Pid,
    /// <summary>The window is a newly appeared one of a given class, whoever owns it.</summary>
    ClassName,
    /// <summary>Any newly appeared adoptable window, whoever owns it. For shim launchers.</summary>
    Any,
    /// <summary>A new adoptable window whose owning process image has a given name.</summary>
    ProcessName,
}

internal sealed class TargetSpec
{
    public required string Name;
    public required string Exe;
    public string Args = "";
    public MatchMode Match = MatchMode.Pid;
    public string ClassName = "";
    public string ProcessName = "";
    public int TimeoutMs = 25000;
    public int SettleMs = 1200;
    /// <summary>Launch with __COMPAT_LAYER=DPIUNAWARE, to force a DPI-awareness mismatch with the host.</summary>
    public bool ForceDpiUnaware;
    public string Why = "";
}

internal static class Targets
{
    public static List<TargetSpec> Build()
    {
        var list = new List<TargetSpec>
        {
            new()
            {
                Name = "Notepad (packaged, via shim)",
                Exe = @"C:\WINDOWS\system32\notepad.exe",
                Match = MatchMode.ProcessName,
                ProcessName = "Notepad.exe",
                SettleMs = 1500,
                Why = "on Win11 notepad.exe is a SHIM — the window belongs to a different pid, so pid "
                    + "matching never finds it. Probed: class=Notepad, exe=Notepad.exe.",
            },
            new()
            {
                Name = "Character Map (classic Win32)",
                Exe = @"C:\WINDOWS\system32\charmap.exe",
                Why = "a genuinely classic Win32 app, launched directly — the real baseline",
            },
            // A "Character Map (forced DPI-unaware)" target lived here and was REMOVED: launching with
            // __COMPAT_LAYER=DPIUNAWARE did not change the process awareness at all (it still reported
            // PerMonitor), because the app's own manifest wins. It scored OK while testing nothing,
            // which is worse than not testing. The DPI axis is covered by the guinea pig below, whose
            // awareness we set ourselves. See ADR 0003.
            new()
            {
                Name = "Task Manager (higher integrity)",
                Exe = @"C:\WINDOWS\system32\taskmgr.exe",
                Match = MatchMode.ProcessName,
                ProcessName = "Taskmgr.exe",
                SettleMs = 1500,
                Why = "UIPI: a non-elevated host must detect and refuse cleanly, not fail silently",
            },
            new()
            {
                Name = "Explorer",
                Exe = @"C:\WINDOWS\explorer.exe",
                Args = @"C:\",
                Match = MatchMode.ClassName,
                ClassName = "CabinetWClass",
                SettleMs = 2000,
                Why = "the launched process exits immediately; the window belongs to the running shell",
            },
        };

        // Self-launched, so its DPI awareness is ours to choose. See GuineaPig.cs for why
        // __COMPAT_LAYER on a real app was not good enough.
        list.Add(new TargetSpec
        {
            Name = "Guinea pig (DPI-UNAWARE)",
            Exe = Environment.ProcessPath!,
            Args = "--role guineapig --awareness unaware",
            Match = MatchMode.ClassName,
            ClassName = "WinMuxGuineaPig",
            SettleMs = 800,
            Why = "a window we know is DPI-unaware, hosted by a PerMonitorV2 host — the actual mixed-DPI test",
        });
        list.Add(new TargetSpec
        {
            Name = "Guinea pig (system-aware)",
            Exe = Environment.ProcessPath!,
            Args = "--role guineapig --awareness system",
            Match = MatchMode.ClassName,
            ClassName = "WinMuxGuineaPig",
            SettleMs = 800,
            Why = "second awareness level, same host",
        });

        var code = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                @"Programs\Microsoft VS Code\Code.exe");
        if (File.Exists(code))
            list.Add(new TargetSpec
            {
                Name = "VS Code (Chromium)",
                Exe = code,
                Args = "--new-window",
                Match = MatchMode.ClassName,
                ClassName = "Chrome_WidgetWin_1",
                SettleMs = 3500,
                TimeoutMs = 40000,
                Why = "multi-process Chromium — several windows appear, only one is the real one",
            });

        list.Add(new TargetSpec
        {
            Name = "Calculator (packaged/UWP)",
            Exe = @"C:\WINDOWS\system32\calc.exe",
            Match = MatchMode.ClassName,
            ClassName = "ApplicationFrameWindow",
            SettleMs = 2500,
            TimeoutMs = 40000,
            Why = "lives under ApplicationFrameHost; README says reparenting is unreliable",
        });

        return list;
    }

    /// <summary>Launch a target, returning the process (which may exit immediately, as Explorer does).</summary>
    public static Process? Launch(TargetSpec t)
    {
        // Redirect and drain: VS Code writes a lot to stdout and would otherwise
        // interleave itself into this spike's log.
        var psi = new ProcessStartInfo(t.Exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (t.Args.Length > 0)
            foreach (var a in t.Args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        if (t.ForceDpiUnaware) psi.Environment["__COMPAT_LAYER"] = "DPIUNAWARE";

        var p = Process.Start(psi);
        if (p != null)
        {
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            try { p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch { }
        }
        return p;
    }
}
