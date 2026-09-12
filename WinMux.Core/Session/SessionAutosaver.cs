namespace WinMux.Core.Session;

/// <summary>The outcome of one automatic or explicit session write.</summary>
public sealed record SessionSaveResult(bool Succeeded, Exception? Error = null);

/// <summary>
/// Coalesces frequent layout/cwd changes and serializes atomic session writes. The latest snapshot
/// always wins, and failures are observable rather than disappearing on a background task.
/// </summary>
public sealed class SessionAutosaver : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeSpan _debounce;
    private readonly Action<string, SessionSnapshot> _save;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private CancellationTokenSource? _delayCancellation;
    private SessionSnapshot? _latest;
    private long _latestVersion;
    private long _savedVersion;
    private Task _scheduled = Task.CompletedTask;
    private bool _disposed;

    public SessionAutosaver(
        string path,
        TimeSpan? debounce = null,
        Action<string, SessionSnapshot>? save = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _save = save ?? SessionFile.Save;
    }

    public event Action<SessionSaveResult>? SaveCompleted;

    public SessionSaveResult? LastResult { get; private set; }

    /// <summary>Replace the pending snapshot and restart the debounce interval.</summary>
    public void RequestSave(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _latest = snapshot;
            _latestVersion++;
            _delayCancellation?.Cancel();
            _delayCancellation?.Dispose();
            _delayCancellation = new CancellationTokenSource();
            _scheduled = DebounceAndSaveAsync(_delayCancellation.Token);
        }
    }

    /// <summary>Cancel the debounce and persist the freshest snapshot before returning.</summary>
    public Task<SessionSaveResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _delayCancellation?.Cancel();
        }
        return SaveLatestAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Task scheduled;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _delayCancellation?.Cancel();
            scheduled = _scheduled;
        }

        // Let a debounce task that already entered the writer gate unwind before disposing the
        // semaphore. Otherwise it can resume after DisposeAsync and fault unobserved.
        await scheduled.ConfigureAwait(false);
        await SaveLatestAsync(CancellationToken.None).ConfigureAwait(false);

        lock (_gate)
        {
            _delayCancellation?.Dispose();
            _delayCancellation = null;
        }
        _writeGate.Dispose();
    }

    private async Task DebounceAndSaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
            await SaveLatestAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<SessionSaveResult> SaveLatestAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                SessionSnapshot? snapshot;
                long version;
                lock (_gate)
                {
                    snapshot = _latest;
                    version = _latestVersion;
                    if (snapshot is null || version <= _savedVersion)
                    {
                        return LastResult ?? new SessionSaveResult(true);
                    }
                }

                var result = await SaveSnapshotWithoutGateAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded) return result;

                lock (_gate) _savedVersion = Math.Max(_savedVersion, version);
                // A request may arrive while the filesystem write is in flight. Loop while
                // holding the writer gate so Flush/Dispose cannot return before that latest
                // snapshot is durable.
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SessionSaveResult> SaveSnapshotWithoutGateAsync(
        SessionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        SessionSaveResult result;
        try
        {
            await Task.Run(() => _save(_path, snapshot), cancellationToken).ConfigureAwait(false);
            result = new SessionSaveResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new SessionSaveResult(false, ex);
        }

        LastResult = result;
        SaveCompleted?.Invoke(result);
        return result;
    }
}
