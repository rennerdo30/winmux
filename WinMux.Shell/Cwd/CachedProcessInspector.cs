using WinMux.Platform;

namespace WinMux.Shell.Cwd;

/// <summary>
/// Keeps one recent process list and hands it to every caller, refreshing it off the calling thread.
///
/// The measurement that forced this: <c>SnapshotProcesses</c> costs **123 ms** for ~330 processes,
/// while reading one process's working directory costs **0.12 ms**. The snapshot is a thousand
/// times the price of the answer it enables, and the shell was paying it once per terminal pane —
/// then again on every terminal title change, because capturing a pane's restore descriptor
/// refreshes its working directory. Eight panes in normal use froze the UI for the best part of a
/// second at a time.
///
/// Two consequences follow, and both are deliberate:
///
/// - **Callers share one list.** A capture pass asks once per pane and the answer is identical for
///   all of them.
/// - **A stale list is served immediately** and refreshed in the background. Staleness is bounded
///   by <see cref="Ttl"/>, and it degrades gracefully rather than lying: the process *tree* may be
///   a few seconds old, but the working directory itself is always read live from whichever process
///   the tree points at. The worst case is picking a pane's root rather than a child spawned
///   moments ago — exactly the answer we would give if that child did not exist — and it corrects
///   itself on the next pass.
///
/// Only the snapshot is cached. <see cref="TryReadWorkingDirectory"/> goes straight through: at
/// 0.12 ms it is not worth the staleness.
/// </summary>
internal sealed class CachedProcessInspector : IProcessInspector
{
    /// <summary>How old the process list may be before a refresh is started.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    private readonly IProcessInspector _inner;
    private readonly TimeSpan _ttl;
    private readonly Lock _gate = new();

    private IReadOnlyList<ProcessSnapshotEntry> _processes = [];
    private string? _error;
    private long _capturedAtMs = long.MinValue;
    private bool _refreshing;

    public CachedProcessInspector(IProcessInspector inner, TimeSpan? ttl = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl ?? Ttl;
    }

    /// <summary>How many times the underlying inspector has actually been asked.</summary>
    internal int SnapshotCount { get; private set; }

    public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
    {
        bool cold;
        lock (_gate)
        {
            cold = _capturedAtMs == long.MinValue;
            if (!cold && !IsStale())
            {
                error = _error;
                return _processes;
            }
        }

        // The very first call has nothing to serve, so it pays the cost. Every later call gets the
        // previous list at once while a refresh runs behind it.
        if (cold)
        {
            Refresh();
            lock (_gate)
            {
                error = _error;
                return _processes;
            }
        }

        StartBackgroundRefresh();
        lock (_gate)
        {
            error = _error;
            return _processes;
        }
    }

    public string? TryReadWorkingDirectory(int processId, out string error) =>
        _inner.TryReadWorkingDirectory(processId, out error);

    private bool IsStale() =>
        Environment.TickCount64 - _capturedAtMs > (long)_ttl.TotalMilliseconds;

    private void StartBackgroundRefresh()
    {
        lock (_gate)
        {
            // One refresh at a time. A queue of them would just take the same 123 ms repeatedly.
            if (_refreshing) return;
            _refreshing = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Refresh();
            }
            finally
            {
                lock (_gate) _refreshing = false;
            }
        });
    }

    private void Refresh()
    {
        IReadOnlyList<ProcessSnapshotEntry> processes;
        string? error;
        try
        {
            processes = _inner.SnapshotProcesses(out error);
        }
        catch (Exception exception)
        {
            // Never let a background refresh take the process down; the stale list stays usable.
            processes = [];
            error = exception.Message;
        }

        lock (_gate)
        {
            SnapshotCount++;
            // An empty result with an error is a failure, not a machine with no processes: keep
            // what we had rather than reporting that every pane's process has vanished.
            if (processes.Count > 0 || error is null)
            {
                _processes = processes;
                _error = error;
            }
            else
            {
                _error = error;
            }
            _capturedAtMs = Environment.TickCount64;
        }
    }
}
