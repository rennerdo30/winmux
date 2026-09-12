using System.IO.Pipes;
using WinMux.Shell;

namespace WinMux.Shell.Tests;

public sealed class CommandChannelTests
{
    [Fact]
    public async Task Client_and_server_exchange_one_named_action()
    {
        var pipeName = "winmux-test-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        var server = new CommandServer((action, _) =>
        {
            received.TrySetResult(action);
            return ValueTask.FromResult<string?>("done");
        }, pipeName);
        var listener = server.RunAsync(shutdown.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, leaveOpen: true);
        await writer.WriteLineAsync("split-columns");
        var result = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("OK done", result);
        Assert.Equal("split-columns", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        shutdown.Cancel();
        await listener.WaitAsync(TimeSpan.FromSeconds(5));
    }

}
