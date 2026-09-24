using WinMux.Platform;
using WinMux.Shell.Cwd;

namespace WinMux.Shell.Tests;

/// <summary>
/// The cache exists because of one measurement: a process snapshot costs ~120 ms for ~330
/// processes, while reading a working directory from it costs 0.12 ms. The shell took a snapshot
/// per terminal pane, and again on every terminal title change — 821 ms of frozen UI per capture
/// pass with seven panes open.
///
/// These assert call counts rather than elapsed time. A timing assertion would be flaky on a busy
/// machine, and "how many times did we ask the operating system" is the property that actually
/// matters.
/// </summary>
public sealed class CachedProcessInspectorTests
{
    [Fact]
    public void Panes_in_one_capture_pass_share_a_single_snapshot()
    {
        var inner = new CountingInspector();
        var cache = new CachedProcessInspector(inner, TimeSpan.FromSeconds(30));

        for (var pane = 0; pane < 8; pane++) _ = cache.SnapshotProcesses(out _);

        Assert.Equal(1, inner.SnapshotCalls);
    }

    [Fact]
    public void The_first_caller_still_gets_real_data()
    {
        var inner = new CountingInspector { Processes = [new(10, 0, "a.exe"), new(11, 10, "b.exe")] };
        var cache = new CachedProcessInspector(inner, TimeSpan.FromSeconds(30));

        var processes = cache.SnapshotProcesses(out var error);

        Assert.Null(error);
        Assert.Equal(2, processes.Count);
    }

    [Fact]
    public async Task A_stale_cache_serves_at_once_and_refreshes_behind_the_caller()
    {
        var inner = new CountingInspector { Processes = [new(10, 0, "a.exe")] };
        var cache = new CachedProcessInspector(inner, TimeSpan.FromMilliseconds(1));
        _ = cache.SnapshotProcesses(out _);          // cold: pays for it
        inner.Processes = [new(10, 0, "a.exe"), new(11, 10, "b.exe")];

        await Task.Delay(30);
        inner.Block = new SemaphoreSlim(0, 1);
        var whileRefreshing = cache.SnapshotProcesses(out _);

        // The refresh is still blocked, so this call cannot have waited for it.
        Assert.Single(whileRefreshing);

        inner.Block.Release();
        await WaitFor(() => inner.SnapshotCalls >= 2);
        await WaitFor(() => cache.SnapshotProcesses(out _).Count == 2);
    }

    [Fact]
    public void Reading_a_working_directory_is_never_cached()
    {
        // 0.12 ms, and it is the actual answer. Caching it would buy nothing and go stale.
        var inner = new CountingInspector();
        var cache = new CachedProcessInspector(inner, TimeSpan.FromSeconds(30));

        for (var i = 0; i < 5; i++) _ = cache.TryReadWorkingDirectory(10, out _);

        Assert.Equal(5, inner.DirectoryCalls);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_list()
    {
        // "Every pane's process has vanished" is a far worse answer than a few seconds of staleness,
        // and it would make the cwd strategy silently fall back for every pane at once.
        var inner = new CountingInspector { Processes = [new(10, 0, "a.exe")] };
        var cache = new CachedProcessInspector(inner, TimeSpan.FromMilliseconds(1));
        _ = cache.SnapshotProcesses(out _);

        inner.Processes = [];
        inner.Error = "CreateToolhelp32Snapshot failed (5)";
        await Task.Delay(30);
        _ = cache.SnapshotProcesses(out _);
        await WaitFor(() => inner.SnapshotCalls >= 2);

        var processes = cache.SnapshotProcesses(out var error);

        Assert.Single(processes);
        Assert.Equal("CreateToolhelp32Snapshot failed (5)", error);
    }

    [Fact]
    public async Task An_inspector_that_throws_in_the_background_does_not_take_the_shell_down()
    {
        var inner = new CountingInspector { Processes = [new(10, 0, "a.exe")] };
        var cache = new CachedProcessInspector(inner, TimeSpan.FromMilliseconds(1));
        _ = cache.SnapshotProcesses(out _);

        inner.Throw = true;
        await Task.Delay(30);
        _ = cache.SnapshotProcesses(out _);
        await WaitFor(() => inner.SnapshotCalls >= 2);

        Assert.Single(cache.SnapshotProcesses(out _));
    }

    [Fact]
    public async Task Concurrent_callers_do_not_stack_up_refreshes()
    {
        var inner = new CountingInspector { Processes = [new(10, 0, "a.exe")] };
        var cache = new CachedProcessInspector(inner, TimeSpan.FromMilliseconds(1));
        _ = cache.SnapshotProcesses(out _);
        inner.Block = new SemaphoreSlim(0, 1);

        await Task.Delay(30);
        for (var i = 0; i < 20; i++) _ = cache.SnapshotProcesses(out _);

        inner.Block.Release();
        await WaitFor(() => inner.SnapshotCalls >= 2);
        await Task.Delay(50);

        // One refresh in flight at a time; twenty queued refreshes would just be twenty × 120 ms.
        Assert.True(inner.SnapshotCalls <= 3, $"started {inner.SnapshotCalls} snapshots for 20 calls");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("condition was not met within 5 seconds");
    }

    private sealed class CountingInspector : IProcessInspector
    {
        private int _snapshotCalls;
        private int _directoryCalls;

        public IReadOnlyList<ProcessSnapshotEntry> Processes { get; set; } = [];
        public string? Error { get; set; }
        public bool Throw { get; set; }
        public SemaphoreSlim? Block { get; set; }

        private int _consoleCalls;

        public HashSet<int> Windowed { get; } = [];

        public int ConsoleCalls => Volatile.Read(ref _consoleCalls);

        public bool IsConsoleProcess(int processId)
        {
            Interlocked.Increment(ref _consoleCalls);
            return !Windowed.Contains(processId);
        }

        public int SnapshotCalls => Volatile.Read(ref _snapshotCalls);
        public int DirectoryCalls => Volatile.Read(ref _directoryCalls);

        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
        {
            Interlocked.Increment(ref _snapshotCalls);
            Block?.Wait(TimeSpan.FromSeconds(5));
            if (Throw) throw new InvalidOperationException("the snapshot handle was invalid");
            error = Error;
            return Processes;
        }

        public string? TryReadWorkingDirectory(int processId, out string error)
        {
            Interlocked.Increment(ref _directoryCalls);
            error = string.Empty;
            return @"C:\work";
        }
    }
}
