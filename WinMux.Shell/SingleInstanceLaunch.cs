using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinMux.Shell;

/// <summary>Per-user launch ownership and serialized directory delivery, including before the UI is ready.</summary>
internal sealed class SingleInstanceLaunch : IDisposable
{
    private sealed record Pending(LaunchRequest Request, TaskCompletionSource<LaunchReply> Reply);
    private readonly Mutex _instance;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Queue<Pending> _queue = new();
    private readonly HashSet<Pending> _pending = [];
    private Func<LaunchRequest, Task>? _handler;
    private bool _draining;
    private bool _disposed;
    public bool IsPrimary { get; }
    public event Action<string>? LaunchFailed;

    private SingleInstanceLaunch(string endpoint)
    {
        _pipeName = endpoint;
        // The named object itself is the ownership marker. Holding its handle (without taking a
        // thread-affine lock) keeps it alive until shutdown and permits disposal from any thread.
        _instance = new Mutex(false, @"Global\" + endpoint, out var created);
        IsPrimary = created;
        if (!IsPrimary) return;
        try
        {
            // Construct before startup/session restore; ConnectAsync can wait through the tiny
            // race between creating the ownership marker and this first pipe instance.
            var first = CreateServer();
            _ = ListenAsync(first);
        }
        catch
        {
            _instance.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public static SingleInstanceLaunch Open(string? endpoint = null) => new(endpoint ?? UserEndpoint());

    private static string UserEndpoint()
    {
        var user = (Environment.UserDomainName + "\\" + Environment.UserName).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
        return "winmux-launch-v1-" + hash;
    }

    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public Task<LaunchReply> QueueInitial(LaunchRequest request) => Enqueue(request);

    public void SetHandler(Func<LaunchRequest, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsPrimary) throw new InvalidOperationException("Only the primary WinMux can handle launch requests.");
            if (_handler is not null) throw new InvalidOperationException("A WinMux launch handler is already registered.");
            _handler = handler;
            StartDrain();
        }
    }

    private Task<LaunchReply> Enqueue(LaunchRequest request)
    {
        if (request.ValidationError() is { } error) return Task.FromResult(new LaunchReply(false, error));
        lock (_gate)
        {
            if (!IsPrimary || _disposed) return Task.FromResult(new LaunchReply(false, "WinMux closed before the folder could be opened. Start WinMux again."));
            if (_pending.Count >= 64) return Task.FromResult(new LaunchReply(false, "WinMux has too many pending launches. Wait for startup to finish and try again."));
            var pending = new Pending(request, new TaskCompletionSource<LaunchReply>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending.Add(pending);
            _queue.Enqueue(pending);
            StartDrain();
            return pending.Reply.Task;
        }
    }

    // Called with _gate held. User callbacks run outside it and never on the listener thread.
    private void StartDrain()
    {
        if (_draining || _handler is null || _disposed) return;
        _draining = true;
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Pending pending;
            Func<LaunchRequest, Task> handler;
            lock (_gate)
            {
                if (_disposed || _queue.Count == 0) { _draining = false; return; }
                pending = _queue.Dequeue();
                handler = _handler!;
            }
            LaunchReply reply;
            try
            {
                await handler(pending.Request).ConfigureAwait(false);
                reply = new LaunchReply(true, "Opened the folder in the existing WinMux.");
            }
            catch (Exception ex)
            {
                reply = new LaunchReply(false, "WinMux could not open the folder: " + ex.Message);
                CrashLog.Write(reply.Message, ex);
                LaunchFailed?.Invoke(reply.Message);
            }
            lock (_gate) _pending.Remove(pending);
            pending.Reply.TrySetResult(reply);
        }
    }

    public async Task<LaunchReply> ForwardAsync(LaunchRequest request, TimeSpan? timeout = null)
    {
        if (IsPrimary) throw new InvalidOperationException("The primary WinMux should queue its launch locally.");
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await LaunchProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
            return await LaunchProtocol.ReadAsync<LaunchReply>(pipe, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            return new LaunchReply(false, "The existing WinMux did not complete this launch. It may still be starting or closing. " +
                "No duplicate instance was started; check WinMux before trying again. " + ex.Message);
        }
    }

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? listening = first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await listening.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                var connected = listening;
                listening = null;
                _ = ServeAsync(connected);
                listening = CreateServer();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                CrashLog.Write("WinMux launch listener stopped", ex);
                LaunchFailed?.Invoke("WinMux cannot receive folder launches: " + ex.Message);
            }
        }
        finally { if (listening is not null) await listening.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await LaunchProtocol.ReadAsync<LaunchRequest>(pipe, readDeadline.Token).ConfigureAwait(false);
                var reply = await Enqueue(request).ConfigureAwait(false);
                // Shutdown completes pending requests first so clients can receive an explanation.
                using var writeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await LaunchProtocol.WriteAsync(pipe, reply, writeDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or ArgumentException)
            {
                // Malformed or disconnected launch clients must not stop the listener.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _pending)
                pending.Reply.TrySetResult(new LaunchReply(false, "WinMux stopped during startup before the folder was opened. Start WinMux again."));
            _pending.Clear();
            _queue.Clear();
        }
        _stop.Cancel();
        _instance.Dispose();
        // Listener continuations still read the cancellation token; do not dispose its source
        // until those continuations finish. It owns no timer and the process lifetime is ending.
    }
}
