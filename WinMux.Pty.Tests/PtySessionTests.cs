using System.Text;
using WinMux.Pty;

namespace WinMux.Pty.Tests;

public sealed class PtySessionTests
{
    [Fact]
    public async Task Cmd_round_trip_resize_and_exit_code_use_owned_contract()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = "WINMUX_PTY_" + Guid.NewGuid().ToString("N");
        var command = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        using var session = await PtySession.StartAsync(new PtySessionOptions
        {
            Application = command,
            Arguments = ["/d", "/q"],
            WorkingDirectory = Environment.CurrentDirectory,
            Columns = 80,
            Rows = 25,
        });

        session.Resize(96, 31);
        await session.WriteAsync(Encoding.UTF8.GetBytes($"echo {marker} & exit /b 23\r\n"));

        var output = await ReadUntilAsync(session.Output, marker, TimeSpan.FromSeconds(10));
        Assert.Contains(marker, output, StringComparison.Ordinal);
        Assert.Equal(23, await session.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Invalid_dimensions_are_rejected_before_process_start()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PtySession.StartAsync(new PtySessionOptions
        {
            Application = "ignored",
            WorkingDirectory = Environment.CurrentDirectory,
            Columns = 0,
        }));
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var bytes = new byte[4096];
        var text = new StringBuilder();
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var count = await stream.ReadAsync(bytes, cancellation.Token);
            if (count == 0) break;
            text.Append(Encoding.UTF8.GetString(bytes, 0, count));
        }
        return text.ToString();
    }
}
