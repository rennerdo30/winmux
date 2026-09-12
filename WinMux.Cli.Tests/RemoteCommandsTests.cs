using System.IO.Pipes;
using WinMux.Cli;

namespace WinMux.Cli.Tests;

public sealed class RemoteCommandsTests
{
    [Fact]
    public async Task Client_sends_action_and_parses_success()
    {
        var pipeName = "winmux-cli-test-" + Guid.NewGuid().ToString("N");
        var server = Task.Run(async () =>
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            Assert.Equal("split-columns", await reader.ReadLineAsync());
            await writer.WriteLineAsync("OK split-columns");
        });

        var result = await RemoteCommands.InvokeAsync("split-columns", pipeName, TimeSpan.FromSeconds(5));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("split-columns", result.Message);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Client_reports_a_missing_shell_in_plain_words()
    {
        var result = await RemoteCommands.InvokeAsync(
            "save-session",
            "winmux-missing-" + Guid.NewGuid().ToString("N"),
            TimeSpan.FromMilliseconds(40));

        Assert.False(result.Succeeded);
        Assert.Contains("Timed out", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
