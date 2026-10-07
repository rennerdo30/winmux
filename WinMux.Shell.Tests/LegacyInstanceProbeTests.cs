using System.IO.Pipes;

namespace WinMux.Shell.Tests;

public sealed class LegacyInstanceProbeTests
{
    [Fact]
    public async Task The_probe_detects_a_same_user_listener_without_sending_an_action()
    {
        var endpoint = "winmux-probe-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(endpoint, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connection = server.WaitForConnectionAsync(deadline.Token);
        Assert.True(await LegacyInstanceProbe.IsListeningAsync(endpoint));
        await connection;
        Assert.Equal(0, await server.ReadAsync(new byte[1], deadline.Token));
    }

    [Fact]
    public async Task An_absent_listener_does_not_block_a_new_instance() =>
        Assert.False(await LegacyInstanceProbe.IsListeningAsync("winmux-probe-test-" + Guid.NewGuid().ToString("N")));
}
