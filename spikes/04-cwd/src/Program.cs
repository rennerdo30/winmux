using System.Text;

namespace CwdSpike;

internal sealed record Measurement(
    string Shell, bool OscEnabled, string Scenario, string Expected,
    string? Osc99, string? Osc7, string? PebRoot, string? PebDeepest, string LaunchCwd,
    string Tree, string PebNote)
{
    public bool OscOk => Match(Osc99) || Match(Osc7);
    public bool PebRootOk => Match(PebRoot);
    public bool PebDeepestOk => Match(PebDeepest);
    public bool LaunchOk => Match(LaunchCwd);
    public string? OscValue => Osc99 ?? Osc7;

    private bool Match(string? got) =>
        got != null && OscWatcher.Canonical(got).Equals(OscWatcher.Canonical(Expected), StringComparison.OrdinalIgnoreCase);

    public static string Mark(bool ok, string? got) => got == null ? "  -  " : ok ? " HIT " : " MISS";
}

internal static class Program
{
    private static readonly List<Measurement> All = [];
    private static bool Verbose;
    private static Func<string>? Tail;

    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var a = new Args(argv);
        var only = a.Str("only", "");
        var includeWsl = !a.Flag("nowsl");
        Verbose = a.Flag("verbose");

        Console.WriteLine("Spike 4 — capturing the working directory");
        Console.WriteLine("OS " + Environment.OSVersion.Version + "   .NET " + Environment.Version);
        Console.WriteLine(new string('=', 100));

        Shells.PrepareWindowsDirs();
        var shells = Shells.Build(includeWsl);
        if (only.Length > 0)
            shells = shells.Where(s => s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var shell in shells)
            foreach (var withOsc in new[] { false, true })
            {
                try { RunShell(shell, withOsc); }
                catch (Exception e) { Console.WriteLine("  !! " + shell.Name + " (osc=" + withOsc + "): " + e.Message); }
            }

        Report();
        return 0;
    }

    private static void RunShell(ShellSpec s, bool withOsc)
    {
        Console.WriteLine();
        Console.WriteLine("### " + s.Name + "   cwd reporting " + (withOsc ? "ENABLED (profile snippet)" : "off (stock shell)"));

        var launchCwd = s.IsWsl ? "/" : Path.GetTempPath().TrimEnd('\\');
        var osc = new OscWatcher();
        var col = new Collector();

        using var pty = new PtySession(s.CommandLine, 120, 30, s.IsWsl ? null : Path.GetTempPath());
        pty.DataReceived += col.Feed;
        pty.DataReceived += osc.Feed;
        Tail = () => { var t = col.TailSnapshot(); return t.Length > 420 ? t[^420..] : t; };

        if (!col.WaitQuiet(s.PromptQuietMs, 25000)) { Console.WriteLine("  shell never settled"); return; }

        if (s.IsWsl)
        {
            Send(pty, col, Shells.WslPrepareCommand, s);
            Send(pty, col, "cd " + Shells.WslRoot, s);
        }

        if (withOsc) Send(pty, col, s.OscSnippet, s);
        osc.Reset();
        // Fire one prompt after the reset, or scenario 0 would score OSC as "never reported"
        // when in fact it simply had not been asked yet.
        Sync(pty, col, s);

        string Dir(string rel) => s.IsWsl
            ? Shells.WslRoot + "/" + rel
            : Path.Combine(s.Root, rel.Replace('/', Path.DirectorySeparatorChar));

        // --- 0: nothing has moved yet, so the launch cwd IS the answer ---
        Record(s, withOsc, "0 fresh (no cd)", s.IsWsl ? Shells.WslRoot : launchCwd, osc, pty, launchCwd);

        // --- A: plain directory ---
        Send(pty, col, s.Cd(Dir(Shells.SimpleDir)), s);
        Record(s, withOsc, "A plain cd", Dir(Shells.SimpleDir), osc, pty, launchCwd);

        // --- B: spaces and non-ASCII ---
        Send(pty, col, s.Cd(Dir(Shells.AwkwardDir)), s);
        Record(s, withOsc, "B spaces+unicode", Dir(Shells.AwkwardDir), osc, pty, launchCwd);

        // --- C: a child process is running, so no new prompt fires ---
        Send(pty, col, s.Cd(Dir(Shells.SimpleDir)), s);
        var expectedC = Dir(Shells.SimpleDir);
        pty.Write(s.BusyChild + "\r");
        Thread.Sleep(2500);                       // measure WHILE the child runs
        Record(s, withOsc, "C child running", expectedC, osc, pty, launchCwd);
        Sync(pty, col, s);                        // wait for the child to ACTUALLY finish

        // --- D: a nested shell, moved deeper ---
        pty.Write(s.NestedCd(Dir(Shells.DeeperDir)) + "\r");
        col.WaitQuiet(s.PromptQuietMs + 1200, 20000);
        Sync(pty, col, s);
        Record(s, withOsc, "D nested shell", Dir(Shells.DeeperDir), osc, pty, launchCwd);
        pty.Write(s.Exit + "\r");
        col.WaitQuiet(s.PromptQuietMs, 8000);

        pty.Write(s.Exit + "\r");
        pty.WaitForExit(4000);

        if (withOsc && osc.AllOsc.Count > 0)
            Console.WriteLine("  OSC seen: " + string.Join(" | ", osc.AllOsc.Distinct().Take(3)));
        else if (withOsc)
            Console.WriteLine("  OSC seen: NONE — the snippet produced nothing that survived ConPTY");
    }

    /// <summary>
    /// Block until the shell is genuinely back at a prompt.
    ///
    /// WaitQuiet alone is not enough: a sleeping child (ping, sleep) emits nothing, so "quiet for
    /// 400ms" is satisfied instantly and the next command is merely buffered as input. That is
    /// exactly how the nested-shell scenario silently never ran.
    /// </summary>
    private static void Sync(PtySession pty, Collector col, ShellSpec s, int timeoutMs = 30000)
    {
        var marker = "WMXSYNC" + Random.Shared.Next(100000, 999999);
        var from = col.Position;
        pty.Write("echo " + marker + "\r");
        // Two occurrences: the echoed command line, then the shell's own output.
        col.WaitForOccurrences(marker, from, 2, timeoutMs);
        col.WaitQuiet(s.PromptQuietMs, 5000);
    }

    private static void Send(PtySession pty, Collector col, string cmd, ShellSpec s, int extraQuietMs = 0)
    {
        pty.Write(cmd + "\r");
        col.WaitQuiet(s.PromptQuietMs + extraQuietMs, 20000);
        // WaitQuiet is not proof the command RAN. PSReadLine renders the typed line, then its
        // predictive autosuggestion, with gaps longer than the quiet threshold — which made the
        // unicode scenario measure a stale prompt and look like a cwd-capture failure.
        Sync(pty, col, s);
    }

    private static void Record(ShellSpec s, bool withOsc, string scenario, string expected,
                               OscWatcher osc, PtySession pty, string launchCwd)
    {
        var deepest = ProcessTree.Deepest(pty.ProcessId);
        var pebRoot = Peb.TryReadCurrentDirectory(pty.ProcessId);
        var rootNote = Peb.LastError;
        var pebDeep = deepest.Pid == pty.ProcessId ? pebRoot : Peb.TryReadCurrentDirectory(deepest.Pid);
        var deepNote = deepest.Pid == pty.ProcessId ? rootNote : Peb.LastError;

        var m = new Measurement(s.Name, withOsc, scenario, expected,
            osc.LastOsc99, osc.LastOsc7, pebRoot, pebDeep, launchCwd,
            ProcessTree.Describe(pty.ProcessId),
            string.IsNullOrEmpty(deepNote) ? rootNote : deepNote);
        All.Add(m);

        Console.WriteLine("  " + scenario.PadRight(18) +
            " OSC" + Measurement.Mark(m.OscOk, m.OscValue) +
            "  PEB(root)" + Measurement.Mark(m.PebRootOk, m.PebRoot) +
            "  PEB(deep)" + Measurement.Mark(m.PebDeepestOk, m.PebDeepest) +
            "  launch" + Measurement.Mark(m.LaunchOk, m.LaunchCwd));
        Console.WriteLine("      want " + expected);
        if (m.OscValue != null) Console.WriteLine("      osc  " + m.OscValue);
        if (m.PebRoot != null) Console.WriteLine("      peb  " + m.PebRoot + (m.PebDeepest != m.PebRoot ? "   deepest: " + m.PebDeepest : ""));
        else if (m.PebNote.Length > 0) Console.WriteLine("      peb  unavailable: " + m.PebNote);
        Console.WriteLine("      tree " + m.Tree);
        if (Verbose)
        {
            var tail = Tail?.Invoke() ?? "";
            var vis = new StringBuilder();
            foreach (var c in tail) vis.Append(c < 32 ? (char)0x00B7 : c);
            Console.WriteLine("      | " + vis);
        }
    }

    private static void Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 100));
        Console.WriteLine("SUCCESS RATE BY STRATEGY (CLAUDE.md section 4: best available wins)");
        Console.WriteLine();
        Console.WriteLine("shell            osc?  scenario            OSC 9;9/7   PEB(root)   PEB(deepest)  launch cwd");
        foreach (var m in All)
            Console.WriteLine("  " + m.Shell.PadRight(16) + (m.OscEnabled ? "yes " : "no  ") + " " +
                m.Scenario.PadRight(20) +
                Measurement.Mark(m.OscOk, m.OscValue).PadRight(12) +
                Measurement.Mark(m.PebRootOk, m.PebRoot).PadRight(12) +
                Measurement.Mark(m.PebDeepestOk, m.PebDeepest).PadRight(14) +
                Measurement.Mark(m.LaunchOk, m.LaunchCwd));

        Console.WriteLine();
        foreach (var grp in All.GroupBy(m => (m.Shell, m.OscEnabled)))
        {
            var n = grp.Count();
            Console.WriteLine("  " + (grp.Key.Shell + (grp.Key.OscEnabled ? " +osc" : " stock")).PadRight(24) +
                "OSC " + Pct(grp.Count(m => m.OscOk), n) +
                "   PEB(root) " + Pct(grp.Count(m => m.PebRootOk), n) +
                "   PEB(deep) " + Pct(grp.Count(m => m.PebDeepestOk), n) +
                "   launch " + Pct(grp.Count(m => m.LaunchOk), n));
        }

        var best = All.Count(m => m.OscOk || m.PebRootOk || m.PebDeepestOk || m.LaunchOk);
        Console.WriteLine();
        Console.WriteLine("  layered strategy (any one succeeds): " + Pct(best, All.Count) + " of " + All.Count + " measurements");
        Console.WriteLine(new string('=', 100));
    }

    private static string Pct(int hit, int total) =>
        (total == 0 ? 0 : hit * 100 / total).ToString().PadLeft(3) + "% (" + hit + "/" + total + ")";
}
