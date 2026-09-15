using Avalonia.Threading;
using WinMux.Core.Session;
using WinMux.Shell.Actions;

namespace WinMux.Shell;

/// <summary>Owns one session file across all of its top-level windows.</summary>
internal sealed class SessionController : IDisposable
{
    private readonly List<MainWindow> _windows = [];
    private readonly SessionAutosaver _autosaver;
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

    public string SessionPath { get; }

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
        if (!result.Succeeded)
            Dispatcher.UIThread.Post(() => Broadcast("could not auto-save session: " + result.Error?.Message));
    }

    private void Broadcast(string message)
    {
        foreach (var window in _windows) window.ShowMessage(message);
    }
}
