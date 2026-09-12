using Porta.Pty;

namespace WinMux.Pty;

internal sealed class PortaPtySession : IPtySession
{
    private readonly IPtyConnection _connection;
    private readonly TaskCompletionSource<int> _exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public PortaPtySession(IPtyConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.ProcessExited += OnProcessExited;

        // SpawnAsync may return after a short-lived child has already exited. Subscribe first,
        // then perform a non-blocking check so neither ordering can lose the exit notification.
        if (_connection.WaitForExit(0))
        {
            _exited.TrySetResult(_connection.ExitCode);
        }
    }

    public int ProcessId => _connection.Pid;

    public Stream Output => _connection.ReaderStream;

    public Task<int> Exited => _exited.Task;

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (data.IsEmpty)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _connection.WriterStream
                .WriteAsync(data, cancellationToken)
                .ConfigureAwait(false);
            await _connection.WriterStream
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        PtySession.ValidateDimensions(columns, rows);
        _connection.Resize(columns, rows);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _connection.Dispose();
        }
        finally
        {
            _connection.ProcessExited -= OnProcessExited;

            // Some provider implementations report process exit during Dispose; if this one did
            // not, cancel rather than leave callers awaiting a task that can never complete.
            _exited.TrySetCanceled();
        }
    }

    private void OnProcessExited(object? sender, PtyExitedEventArgs args)
    {
        _exited.TrySetResult(args.ExitCode);
    }
}
