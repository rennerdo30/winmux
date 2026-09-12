using System.Diagnostics;
using WinMux.Core.Model;
using WinMux.Shell.Cwd;

namespace WinMux.Shell.Tests;

public sealed class ProcessWorkingDirectoryResolverTests
{
    [Fact]
    public void Resolve_WhenPebIsDisabledForWsl_DoesNotInspectTheProcess()
    {
        var result = ProcessWorkingDirectoryResolver.Resolve(Environment.ProcessId, disablePebForWsl: true);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.Equal(CwdSource.Unknown, result.Provenance);
        Assert.Contains("disabled for WSL", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WhenProcessDoesNotExist_ReturnsAnExplicitFailure()
    {
        var result = ProcessWorkingDirectoryResolver.Resolve(int.MaxValue, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.Equal(CwdSource.Unknown, result.Provenance);
        Assert.NotNull(result.Error);
        Assert.Contains(
            OperatingSystem.IsWindows() ? "does not exist" : "only on Windows",
            result.Error,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_OnWindows_ReadsARealCmdCurrentDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"winmux cwd resolver {Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        Process? commandPrompt = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                WorkingDirectory = Environment.SystemDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/q");
            startInfo.ArgumentList.Add("/k");

            commandPrompt = Process.Start(startInfo);
            Assert.NotNull(commandPrompt);
            await commandPrompt.StandardInput.WriteLineAsync($"cd /d \"{temporaryDirectory}\"");
            // Keep a real descendant alive after cmd changes directory. This exercises the
            // resolver's deepest-process path and avoids treating the console host as the shell.
            await commandPrompt.StandardInput.WriteLineAsync("ping.exe -n 30 127.0.0.1 > nul");

            ProcessWorkingDirectoryResult? captured = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                captured = ProcessWorkingDirectoryResolver.Resolve(
                    commandPrompt.Id,
                    disablePebForWsl: false);
                if (captured.Succeeded
                    && PathsEqual(temporaryDirectory, captured.Path!))
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.NotNull(captured);
            Assert.True(captured.Succeeded, captured.Error);
            Assert.True(
                PathsEqual(temporaryDirectory, captured.Path!),
                $"Expected '{temporaryDirectory}', captured '{captured.Path}'.");
            Assert.True(
                captured.Provenance is CwdSource.ProcessRoot or CwdSource.ProcessDeepest,
                $"Unexpected provenance: {captured.Provenance}.");
        }
        finally
        {
            if (commandPrompt is not null)
            {
                if (!commandPrompt.HasExited)
                {
                    commandPrompt.Kill(entireProcessTree: true);
                    commandPrompt.WaitForExit();
                }

                commandPrompt.Dispose();
            }

            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static bool PathsEqual(string expected, string actual) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            StringComparison.OrdinalIgnoreCase);
}
