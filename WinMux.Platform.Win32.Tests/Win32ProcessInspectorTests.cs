using System.Diagnostics;
using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Platform.Win32.Tests;

/// <summary>
/// The Windows implementation against the real OS. The *policy* that consumes it is tested against
/// a fake tree in <c>WinMux.Shell.Tests.ProcessWorkingDirectoryPolicyTests</c>; what is left worth
/// asserting here is that the Toolhelp walk and the x64 PEB read still work, and that a refusal
/// arrives as words rather than as a silent null.
/// </summary>
public sealed class Win32ProcessInspectorTests
{
    private readonly Win32ProcessInspector _inspector = new();

    [Fact]
    public void The_snapshot_contains_this_process_and_its_parent_link()
    {
        var processes = _inspector.SnapshotProcesses(out var error);

        Assert.Null(error);
        var self = Assert.Single(processes, p => p.ProcessId == Environment.ProcessId);
        Assert.EndsWith(".exe", self.ExecutableName, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0, self.ParentProcessId);
    }

    [Fact]
    public void The_executable_name_is_a_file_name_and_never_a_path()
    {
        // The cwd policy compares it against "conhost.exe" and friends by equality, so a path here
        // would silently disable the console-infrastructure rule rather than fail.
        var processes = _inspector.SnapshotProcesses(out _);

        Assert.All(processes, p => Assert.Equal(p.ExecutableName, Path.GetFileName(p.ExecutableName)));
    }

    [Fact]
    public void Reading_this_process_returns_its_actual_working_directory()
    {
        var path = _inspector.TryReadWorkingDirectory(Environment.ProcessId, out var error);

        Assert.True(path is not null, error);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_process_that_does_not_exist_fails_with_a_reason_rather_than_throwing()
    {
        var path = _inspector.TryReadWorkingDirectory(int.MaxValue, out var error);

        Assert.Null(path);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_protected_process_is_refused_in_words_a_user_could_act_on()
    {
        // The System process is unopenable for everyone, elevated or not. ADR 0004's whole point is
        // that a pane whose directory cannot be read degrades to the next strategy and says why.
        var system = Process.GetProcessesByName("System").FirstOrDefault();
        if (system is null) return;

        using (system)
        {
            var path = _inspector.TryReadWorkingDirectory(system.Id, out var error);

            Assert.Null(path);
            Assert.Contains("denied", error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
