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
    private MainWindow? _activeWindow;
    private int _disposed;

    public SessionController(string path)
    {
        _autosaver = new SessionAutosaver(path);
        _autosaver.SaveCompleted += OnSaveCompleted;
        _commandServer = new CommandServer(DispatchRemoteAsync);
        SessionPath = Path.GetFullPath(path);
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

    public void RequestSave()
    {
        if (_disposed != 0 || _windows.Count == 0) return;
        _autosaver.RequestSave(CaptureSnapshot());
    }

    public SessionSaveResult SaveNow()
    {
        if (_disposed != 0) return new SessionSaveResult(false, new ObjectDisposedException(nameof(SessionController)));
        if (_windows.Count == 0) return new SessionSaveResult(false, new InvalidOperationException("The session has no windows."));
        _autosaver.RequestSave(CaptureSnapshot());
        return _autosaver.FlushAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Persist the last window before app exit. Closing one of several windows removes only that
    /// window from the next restore and leaves the others running.
    /// </summary>
    public SessionSaveResult WindowClosing(MainWindow window)
    {
        if (!_windows.Contains(window)) return new SessionSaveResult(true);
        if (_windows.Count == 1)
        {
            return SaveNow();
        }

        _windows.Remove(window);
        if (ReferenceEquals(_activeWindow, window)) _activeWindow = _windows.LastOrDefault();
        RequestSave();
        return new SessionSaveResult(true);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
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
        Dispatcher.UIThread.Post(() =>
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

            var result = target.DispatchNamedAction(actionName);
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
