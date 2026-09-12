using System.Text;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Pty;

namespace WinMux.Shell.Tests;

public sealed class SessionRestoreLaunchTests
{
    [Fact]
    public async Task Toml_restored_program_args_environment_and_cwd_reach_a_real_process()
    {
        if (!OperatingSystem.IsWindows()) return;

        var directory = Path.Combine(Path.GetTempPath(), "winmux restore launch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var argumentMarker = "WINMUX_ARG_" + Guid.NewGuid().ToString("N");
            var environmentMarker = "WINMUX_ENV_" + Guid.NewGuid().ToString("N");
            var command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var pane = new Pane(PaneId.New(), PaneKind.Terminal, "restore-e2e", new RestoreDescriptor
            {
                Kind = PaneKind.Terminal,
                Title = "restore-e2e",
                Program = command,
                Args = ["/d", "/q", "/c", $"echo {argumentMarker} & echo %WINMUX_RESTORE_TEST% & cd"],
                EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["WINMUX_RESTORE_TEST"] = environmentMarker,
                },
                Cwd = new WorkingDirectory(directory, CwdSource.ShellReported, DateTimeOffset.UtcNow),
            });
            var source = new SessionSnapshot
            {
                SavedAt = DateTimeOffset.UtcNow,
                Windows = [SessionMapper.ToSnapshot(new LayoutTree(pane), "e2e")],
            };
            var restored = SessionMapper.FromSnapshot(
                SessionFile.Deserialize(SessionFile.Serialize(source)).Windows.Single())
                .Panes.Single().Restore;

            using var session = await PtySession.StartAsync(new PtySessionOptions
            {
                Application = restored.Program!,
                Arguments = restored.Args,
                Environment = restored.EnvOverrides,
                WorkingDirectory = restored.Cwd.Path,
                Columns = 300,
                Rows = 30,
            });
            var output = await ReadUntilAsync(
                session.Output,
                text => text.Contains(argumentMarker, StringComparison.Ordinal) &&
                        text.Contains(environmentMarker, StringComparison.Ordinal) &&
                        text.Contains(directory, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));

            Assert.Contains(argumentMarker, output, StringComparison.Ordinal);
            Assert.Contains(environmentMarker, output, StringComparison.Ordinal);
            Assert.True(output.Contains(directory, StringComparison.OrdinalIgnoreCase), output);
            Assert.Equal(0, await session.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> ReadUntilAsync(
        Stream stream,
        Func<string, bool> complete,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        var output = new StringBuilder();
        while (!complete(output.ToString()))
        {
            var read = await stream.ReadAsync(buffer, cancellation.Token);
            if (read == 0) break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return output.ToString();
    }
}
