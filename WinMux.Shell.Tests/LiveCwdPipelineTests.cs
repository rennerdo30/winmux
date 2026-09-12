using System.Text;
using WinMux.Pty;
using WinMux.Terminal;

namespace WinMux.Shell.Tests;

public sealed class LiveCwdPipelineTests
{
    [Fact]
    public async Task Real_cmd_osc_report_crosses_conpty_and_updates_the_terminal_engine()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "winmux osc pipeline " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var session = await PtySession.StartAsync(new PtySessionOptions
            {
                Application = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = ["/d", "/q"],
                WorkingDirectory = Environment.SystemDirectory,
                Columns = 160,
                Rows = 30,
            });
            var engine = new TerminalEmulationEngine(160, 30);
            var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.WorkingDirectoryChanged += path =>
            {
                if (PathsEqual(directory, path)) captured.TrySetResult(path);
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var pump = PumpAsync(session.Output, engine, timeout.Token);

            await session.WriteAsync(Encoding.UTF8.GetBytes("prompt $e]9;9;$P$e\\$P$G\r\n"), timeout.Token);
            await session.WriteAsync(Encoding.UTF8.GetBytes($"cd /d \"{directory}\"\r\n"), timeout.Token);

            var path = await captured.Task.WaitAsync(timeout.Token);
            Assert.True(PathsEqual(directory, path), $"Expected '{directory}', captured '{path}'.");
            await session.WriteAsync(
                Encoding.UTF8.GetBytes($"cd /d \"{Environment.SystemDirectory}\"\r\nexit\r\n"),
                timeout.Token);
            Assert.Equal(0, await session.Exited.WaitAsync(timeout.Token));
            timeout.Cancel();
            await pump;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task PumpAsync(Stream output, ITerminalEngine engine, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;
                engine.Write(buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool PathsEqual(string expected, string actual) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            StringComparison.OrdinalIgnoreCase);
}
