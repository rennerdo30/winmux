using WinMux.Core;
using WinMux.Core.Session;
using WinMux.Core.Settings;
using System.IO.Pipes;

namespace WinMux.Shell.Tests;

public sealed class IsolatedDataDirectoryTests
{
    [Fact]
    public void Profile_data_and_endpoints_follow_the_isolated_directory()
    {
        var root = WinMuxDataDirectory.OverridePath!;
        Assert.True(WinMuxDataDirectory.IsIsolated);
        Assert.Equal(Path.Combine(root, "settings.toml"), SettingsFile.DefaultPath);
        Assert.Equal(Path.Combine(root, "profiles.toml"), ProfilesFile.DefaultPath);
        Assert.Equal(Path.Combine(root, "session.toml"), SessionLocation.DefaultPath);
        Assert.Equal(Path.Combine(root, "crash.log"), CrashLog.Path);
        Assert.StartsWith(CommandServer.DefaultPipeName + "-", WinMuxDataDirectory.CommandPipeName);
    }

    [Fact]
    public async Task Command_pipe_reaches_only_this_profile()
    {
        using var stop = new CancellationTokenSource();
        var server = new CommandServer((action, _) => ValueTask.FromResult<string?>(action));
        var running = server.RunAsync(stop.Token);
        try
        {
            using var client = new NamedPipeClientStream(".", WinMuxDataDirectory.CommandPipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, leaveOpen: true);
            await writer.WriteLineAsync("save-session");
            Assert.Equal("OK save-session", await reader.ReadLineAsync(timeout.Token));
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }
}
