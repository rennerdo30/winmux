using Avalonia.Threading;
using WinMux.Core.Session;
using WinMux.Shell.Actions;

namespace WinMux.Shell;

/// <summary>Owns one session file across all of its top-level windows.</summary>
internal sealed class SessionController : IDisposable
{
    private readonly List<MainWindow> _windows = [];
    private SessionAutosaver _autosaver;
    private readonly CommandServer _commandServer;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _captureDebounce;
    private MainWindow? _activeWindow;
    private int _disposed;

    public SessionController(string path)
    {
        _autosaver = new SessionAutosaver(path);
        _autosaver.SaveCompleted += OnSaveCompleted;
        _commandServer = new CommandServer(DispatchRemoteAsync);
        SessionPath = Path.GetFullPath(path);

        // Capturing a snapshot is expensive: it asks every pane for its restore descriptor, and a
        // terminal pane answers by walking the machine's whole process list and reading a PEB
        // (ADR 0004, strategy 2). The autosaver already debounces the *write*, but the capture ran
        // synchronously on the UI thread at every call — so dragging the window, which fires
        // PositionChanged per mouse move, enumerated every process on the system per frame.
        _captureDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _captureDebounce.Tick += (_, _) => { _captureDebounce.Stop(); CaptureAndQueue(); };
    }

    public string SessionPath { get; private set; }

    /// <summary>
    /// When the session was last written, or null if it has not been yet.
    ///
    /// Exposed because persistence is priority 1 and was entirely invisible in the interface: there
    /// was no way for anyone to tell whether the thing this product exists to do had happened.
    /// </summary>
    public DateTimeOffset? LastSavedAt { get; private set; }

    /// <summary>How many top-level windows this session currently owns.</summary>
    public int WindowCount => _windows.Count;

    public void Register(MainWindow window)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _windows.Add(window);
        _activeWindow ??= window;
    }

    public void Activate(MainWindow window)
    {
        if (_windows.Contains(window)) _activeWindow = window;
    }

    public void StartCommandServer() => _ = RunCommandServerAsync();

    /// <summary>
    /// Ask for a save. Cheap and coalescing: the snapshot is taken once the caller stops asking.
    /// </summary>
    public void RequestSave()
    {
        if (_disposed != 0 || _windows.Count == 0) return;
        _captureDebounce.Stop();
        _captureDebounce.Start();
    }

    private void CaptureAndQueue()
    {
        if (_disposed != 0 || _windows.Count == 0) return;
        _autosaver.RequestSave(CaptureSnapshot());
    }

    public SessionSaveResult SaveNow()
    {
        if (_disposed != 0) return new SessionSaveResult(false, new ObjectDisposedException(nameof(SessionController)));
        if (_windows.Count == 0) return new SessionSaveResult(false, new InvalidOperationException("The session has no windows."));
        // An explicit save is not a request; take the snapshot now rather than waiting out a debounce.
        _captureDebounce.Stop();
        _autosaver.RequestSave(CaptureSnapshot());
        return _autosaver.FlushAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Save to a different file and keep working there, the way Save As has always behaved.
    ///
    /// The old file is flushed first: a pending debounced write against it would otherwise either
    /// be lost or land after the rename, and losing the last edit is exactly what priority 1 forbids.
    /// </summary>
    public async Task<SessionSaveResult> SaveAsAsync(string path)
    {
        if (_disposed != 0)
            return new SessionSaveResult(false, new ObjectDisposedException(nameof(SessionController)));
        if (_windows.Count == 0)
            return new SessionSaveResult(false, new InvalidOperationException("The session has no windows."));

        var destination = Path.GetFullPath(path);
        if (string.Equals(destination, SessionPath, StringComparison.OrdinalIgnoreCase)) return SaveNow();

        _captureDebounce.Stop();
        var pending = await _autosaver.FlushAsync().ConfigureAwait(true);
        if (!pending.Succeeded) return pending;

        _autosaver.SaveCompleted -= OnSaveCompleted;
        await _autosaver.DisposeAsync().ConfigureAwait(true);

        _autosaver = new SessionAutosaver(destination);
        _autosaver.SaveCompleted += OnSaveCompleted;
        SessionPath = destination;
        return SaveNow();
    }

    /// <summary>
    /// Start working in a different file **without** writing the current layout into it.
    ///
    /// This is what opening a session needs and <see cref="SaveAsAsync"/> is not: the old layout
    /// belongs in the old file. It is flushed there first, so nothing pending is lost, and the new
    /// file is left exactly as it was on disk until the caller asks for a save.
    /// </summary>
    public async Task<SessionSaveResult> SwitchFileAsync(string path)
    {
        if (_disposed != 0)
            return new SessionSaveResult(false, new ObjectDisposedException(nameof(SessionController)));

        var destination = Path.GetFullPath(path);
        _captureDebounce.Stop();

        var pending = await _autosaver.FlushAsync().ConfigureAwait(true);
        if (!pending.Succeeded) return pending;

        if (string.Equals(destination, SessionPath, StringComparison.OrdinalIgnoreCase))
            return new SessionSaveResult(true);

        _autosaver.SaveCompleted -= OnSaveCompleted;
        await _autosaver.DisposeAsync().ConfigureAwait(true);

        _autosaver = new SessionAutosaver(destination);
        _autosaver.SaveCompleted += OnSaveCompleted;
        SessionPath = destination;
        return new SessionSaveResult(true);
    }

    /// <summary>Persist current intent before any foreign application is detached.</summary>
    public SessionSaveResult PrepareWindowClosing(MainWindow window)
    {
        if (!_windows.Contains(window)) return new SessionSaveResult(true);
        return SaveNow();
    }

    /// <summary>
    /// Remove a successfully closed window from a multi-window session. The last window remains in
    /// the snapshot so the next launch restores the session the user just closed.
    /// </summary>
    public void CompleteWindowClosing(MainWindow window)
    {
        if (!_windows.Contains(window) || _windows.Count == 1) return;
        _windows.Remove(window);
        if (ReferenceEquals(_activeWindow, window)) _activeWindow = _windows.LastOrDefault();
        RequestSave();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        // Stop before the autosaver goes away, or a pending tick captures against a disposed sink.
        _captureDebounce.Stop();
        _autosaver.SaveCompleted -= OnSaveCompleted;
        _autosaver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lifetime.Dispose();
    }

    private SessionSnapshot CaptureSnapshot() => new()
    {
        SavedAt = DateTimeOffset.UtcNow,
        Windows = _windows.Select(window => window.CaptureSnapshot()).ToArray(),
    };

    private async ValueTask<string?> DispatchRemoteAsync(string actionName, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            var target = _activeWindow ?? _windows.FirstOrDefault();
            if (target is null)
            {
                completion.TrySetException(new InvalidOperationException("WinMux has no open window."));
                return;
            }

            var result = await target.DispatchNamedActionAsync(actionName, cancellationToken);
            if (result.Succeeded) completion.TrySetResult(result.ActionName);
            else completion.TrySetException(result.Exception ?? new InvalidOperationException(result.Error));
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task RunCommandServerAsync()
    {
        try
        {
            await _commandServer.RunAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dispatcher.UIThread.Post(() => Broadcast("CLI actions unavailable: " + ex.Message));
        }
    }

    private void OnSaveCompleted(SessionSaveResult result)
    {
        if (result.Succeeded) LastSavedAt = DateTimeOffset.UtcNow;
        else Dispatcher.UIThread.Post(() => Broadcast("could not auto-save session: " + result.Error?.Message));
    }

    private void Broadcast(string message)
    {
        foreach (var window in _windows) window.ShowMessage(message);
    }
}
