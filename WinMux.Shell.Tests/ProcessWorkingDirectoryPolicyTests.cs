using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.Cwd;

namespace WinMux.Shell.Tests;

/// <summary>
/// Strategy 2 of the layered cwd capture (CLAUDE.md section 4), against an invented process tree.
///
/// Every rule here was paid for with measurement in spike 4, and until Phase 5 none of them could
/// be asserted: the resolver read the live machine, so a test could only hope the right processes
/// happened to exist. With <see cref="IProcessInspector"/> in front of the OS, the policy is
/// ordinary logic — which is what it always was.
/// </summary>
public sealed class ProcessWorkingDirectoryPolicyTests
{
    [Fact]
    public void The_deepest_descendant_wins_over_the_root()
    {
        // The shell itself is stale the moment it spawns something: spike 4 measured PowerShell's
        // own PEB as permanently wrong after a `cd`, and the child's as right.
        var inspector = new FakeInspector
        {
            Tree = [Process(100, 0, "pwsh.exe"), Process(200, 100, "git.exe")],
            Directories = { [100] = @"C:\stale", [200] = @"C:\work\repo" },
        };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(@"C:\work\repo", result.Path);
        Assert.Equal(CwdSource.ProcessDeepest, result.Provenance);
    }

    [Fact]
    public void Console_infrastructure_is_never_mistaken_for_the_workload()
    {
        // conhost sits at the same depth as the real child and reports C:\WINDOWS. Without this
        // rule the PID tie-break can pick it, and a confidently wrong cwd is worse than none.
        var inspector = new FakeInspector
        {
            Tree =
            [
                Process(100, 0, "cmd.exe"),
                Process(200, 100, "ping.exe"),
                Process(300, 100, "conhost.exe"),
            ],
            Directories = { [100] = @"C:\launch", [200] = @"C:\work", [300] = @"C:\WINDOWS" },
        };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\work", result.Path);
    }

    [Fact]
    public void An_unreadable_descendant_falls_back_to_the_root()
    {
        var inspector = new FakeInspector
        {
            Tree = [Process(100, 0, "cmd.exe"), Process(200, 100, "elevated.exe")],
            Directories = { [100] = @"C:\launch" },
            Errors = { [200] = "access denied (the process may be elevated or protected)" },
        };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\launch", result.Path);
        Assert.Equal(CwdSource.ProcessRoot, result.Provenance);
    }

    [Fact]
    public void When_nothing_can_be_read_the_failure_names_both_processes_and_both_reasons()
    {
        // "No silent failure around embedding or persistence" (CLAUDE.md section 8): a pane that
        // restores to the wrong directory must at least be able to say why.
        var inspector = new FakeInspector
        {
            Tree = [Process(100, 0, "cmd.exe"), Process(200, 100, "wow64app.exe")],
            Errors =
            {
                [100] = "the PEB CurrentDirectory is empty",
                [200] = "target is 32-bit (WOW64); this reader supports only the x64 PEB layout",
            },
        };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Contains("wow64app.exe (200)", result.Error);
        Assert.Contains("WOW64", result.Error);
        Assert.Contains("cmd.exe (100)", result.Error);
        Assert.Contains("empty", result.Error);
    }

    [Fact]
    public void A_wsl_pane_is_never_inspected_at_all()
    {
        // The Windows PEB of a WSL shell reports a Windows path for a directory that lives in the
        // Linux VM — meaningless, not merely unreliable (ADR 0004). Asking is the bug.
        var inspector = new FakeInspector { Tree = [Process(100, 0, "wsl.exe")] };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: true);

        Assert.False(result.Succeeded);
        Assert.Equal(CwdSource.Unknown, result.Provenance);
        Assert.Equal(0, inspector.SnapshotCount);
    }

    [Fact]
    public void A_cycle_in_the_reported_tree_does_not_hang_the_walk()
    {
        // Toolhelp reuses PIDs, so a snapshot can genuinely describe a parent loop.
        var inspector = new FakeInspector
        {
            Tree = [Process(100, 200, "a.exe"), Process(200, 100, "b.exe")],
            Directories = { [200] = @"C:\work" },
        };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\work", result.Path);
    }

    [Fact]
    public void A_process_that_has_already_exited_says_so()
    {
        var inspector = new FakeInspector { Tree = [Process(100, 0, "cmd.exe")] };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(999, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_snapshot_is_reported_rather_than_read_as_an_empty_machine()
    {
        var inspector = new FakeInspector { SnapshotError = "CreateToolhelp32Snapshot failed (5)" };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Contains("CreateToolhelp32Snapshot", result.Error);
    }

    [Fact]
    public void An_inspector_that_throws_degrades_instead_of_failing_the_whole_save()
    {
        var inspector = new FakeInspector { ThrowOnSnapshot = true };

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Contains("Could not inspect process 100", result.Error);
    }

    private static ProcessSnapshotEntry Process(int id, int parent, string name) => new(id, parent, name);

    private sealed class FakeInspector : IProcessInspector
    {
        public List<ProcessSnapshotEntry> Tree { get; init; } = [];
        public Dictionary<int, string> Directories { get; } = [];
        public Dictionary<int, string> Errors { get; } = [];
        public string? SnapshotError { get; init; }

        /// <summary>Process ids that are windowed applications. Everything else is a console one.</summary>
        public HashSet<int> Windowed { get; } = [];

        public bool IsConsoleProcess(int processId) => !Windowed.Contains(processId);
        public bool ThrowOnSnapshot { get; init; }
        public int SnapshotCount { get; private set; }

        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
        {
            SnapshotCount++;
            if (ThrowOnSnapshot) throw new InvalidOperationException("the snapshot handle was invalid");
            error = SnapshotError;
            return Tree;
        }

        public string? TryReadWorkingDirectory(int processId, out string error)
        {
            if (Directories.TryGetValue(processId, out var path))
            {
                error = string.Empty;
                return path;
            }

            error = Errors.TryGetValue(processId, out var reason) ? reason : "no PEB";
            return null;
        }
    }
}

/// <summary>
/// The walk stops where the pane stops.
///
/// <para>
/// Reported on 2026-09-24: "running claude code can cause our saved workdir to change… often on a
/// session resume the workdir is then somehow in a chrome dir". Claude Code starts Chrome, Chrome
/// starts a renderer per tab, and a renderer sits deeper in the tree than any shell — so "the
/// deepest descendant" stopped being the program in the pane and became a browser, whose working
/// directory is wherever it was installed. Priority 1 is session persistence, and this quietly
/// restored panes into Chrome's program folder.
/// </para>
/// </summary>
public sealed class ProcessWalkStopsAtWindowsTests
{
    private static ProcessSnapshotEntry P(int id, int parent, string name) => new(id, parent, name);

    /// <summary>The reported tree: a shell, Claude Code, and the browser it started.</summary>
    private static FakeInspectorForWalk ChromeUnderClaude()
    {
        var inspector = new FakeInspectorForWalk
        {
            Tree =
            [
                P(100, 1, "cmd.exe"),
                P(200, 100, "node.exe"),
                P(300, 200, "chrome.exe"),
                P(400, 300, "chrome.exe"),   // renderer, deeper than anything real
                P(500, 400, "chrome.exe"),   // and another
            ],
        };

        inspector.Directories[100] = @"C:\work\winmux";
        inspector.Directories[200] = @"C:\work\winmux";
        inspector.Directories[300] = @"C:\Program Files\Google\Chrome\Application";
        inspector.Directories[400] = @"C:\Program Files\Google\Chrome\Application";
        inspector.Directories[500] = @"C:\Program Files\Google\Chrome\Application";
        inspector.Windowed.Add(300);
        inspector.Windowed.Add(400);
        inspector.Windowed.Add(500);
        return inspector;
    }

    [Fact]
    public void A_browser_a_program_started_is_not_the_pane()
    {
        var inspector = ChromeUnderClaude();

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\work\winmux", result.Path);
    }

    [Fact]
    public void Nor_is_anything_the_browser_starts()
    {
        // Pruned rather than skipped. A console helper under a window is still under a window, and
        // the window is where the pane's process tree stopped being about the pane.
        var inspector = ChromeUnderClaude();
        inspector.Tree.Add(P(600, 500, "crashpad_handler.exe"));
        inspector.Directories[600] = @"C:\Program Files\Google\Chrome\Application";

        var deepest = ProcessWorkingDirectoryResolver.FindDeepestDescendant(
            100, inspector.Tree, inspector.IsConsoleProcess);

        Assert.Equal(200, deepest?.ProcessId);
    }

    [Fact]
    public void A_nested_shell_is_still_found()
    {
        // The case the deepest-descendant walk exists for, and the one a blunter fix would break:
        // ADR 0004 measured it carrying 60% of pwsh panes and 100% of cmd.
        var inspector = new FakeInspectorForWalk
        {
            Tree = [P(100, 1, "cmd.exe"), P(200, 100, "pwsh.exe"), P(300, 200, "cmd.exe")],
        };
        inspector.Directories[100] = @"C:\start";
        inspector.Directories[300] = @"C:\deep";

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\deep", result.Path);
    }

    [Fact]
    public void Not_knowing_is_treated_as_a_console_process()
    {
        // The inspector answers true when it cannot tell, and this pins that the walk honours it:
        // losing the strategy is a worse failure than the one it was hardened against.
        var inspector = new FakeInspectorForWalk
        {
            Tree = [P(100, 1, "cmd.exe"), P(200, 100, "mystery.exe")],
        };
        inspector.Directories[100] = @"C:\start";
        inspector.Directories[200] = @"C:\deep";

        var result = new ProcessWorkingDirectoryResolver(inspector).Resolve(100, disablePebForWsl: false);

        Assert.Equal(@"C:\deep", result.Path);
    }

    internal sealed class FakeInspectorForWalk : IProcessInspector
    {
        public List<ProcessSnapshotEntry> Tree { get; init; } = [];
        public Dictionary<int, string> Directories { get; } = [];
        public HashSet<int> Windowed { get; } = [];

        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
        {
            error = null;
            return Tree;
        }

        public string? TryReadWorkingDirectory(int processId, out string error)
        {
            error = string.Empty;
            if (Directories.TryGetValue(processId, out var path)) return path;
            error = "no directory";
            return null;
        }

        public bool IsConsoleProcess(int processId) => !Windowed.Contains(processId);
    }
}
