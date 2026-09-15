using System.Diagnostics;
using WinMux.Core.Model;
using WinMux.Platform.Win32.Windows;
using WinMux.Shell.Cwd;

namespace WinMux.Shell.Tests;

public sealed class ProcessWorkingDirectoryResolverTests
{
    // The real Windows inspector on purpose: this file is the end-to-end check that the extracted
    // implementation still reads a live PEB. The policy itself is exercised against a fake tree in
    // ProcessWorkingDirectoryPolicyTests.
    private static readonly ProcessWorkingDirectoryResolver Resolver = new(new Win32ProcessInspector());

    [Fact]
    public void Resolve_WhenPebIsDisabledForWsl_DoesNotInspectTheProcess()
    {
        var result = Resolver.Resolve(Environment.ProcessId, disablePebForWsl: true);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.Equal(CwdSource.Unknown, result.Provenance);
        Assert.Contains("disabled for WSL", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WhenProcessDoesNotExist_ReturnsAnExplicitFailure()
    {
        var result = Resolver.Resolve(int.MaxValue, disablePebForWsl: false);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.Equal(CwdSource.Unknown, result.Provenance);
        Assert.NotNull(result.Error);
        Assert.Contains("does not exist", result.Error, StringComparison.OrdinalIgnoreCase);
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
                captured = Resolver.Resolve(
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

            DeleteWhenWindowsLetsGo(temporaryDirectory);
        }
    }

    /// <summary>
    /// Delete the temporary directory, retrying while Windows still has it open.
    ///
    /// This test makes a real `cmd.exe` sit in the directory and start a real `ping.exe` inside it,
    /// which is the whole point — the resolver walks to the deepest child. Killing the tree does not
    /// make that synchronous: <c>WaitForExit</c> waits for `cmd` alone, the child is killed
    /// separately, and Windows releases a working-directory handle when the kernel gets round to it
    /// rather than when the process object goes away.
    ///
    /// So the delete raced the teardown and failed with "used by another process" — on CI, where a
    /// loaded runner widens the gap, and only sometimes. Retrying is the fix; waiting longer up
    /// front would be slower and still a guess.
    /// </summary>
    private static void DeleteWhenWindowsLetsGo(string directory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline) throw;
                Thread.Sleep(100);
            }
        }
    }

    private static bool PathsEqual(string expected, string actual) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            StringComparison.OrdinalIgnoreCase);
}
