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

/// <summary>
/// Telling a console application from a windowed one, which is how the working-directory walk knows
/// where a pane's process tree stops being about the pane.
///
/// Checked against files every Windows has rather than against whatever is running, so the test
/// says the same thing on a developer's desktop and on a CI runner with no desktop at all.
/// </summary>
public class ConsoleImageTests
{
    private static string System32(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name);

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("where.exe")]
    [InlineData("tasklist.exe")]
    public void A_console_application_is_recognised(string name) =>
        Assert.True(Win32ProcessInspector.IsConsoleImage(System32(name)));

    // Not mspaint.exe: on Windows 11 it is a stub for the Store application and does not answer
    // for itself. notepad.exe is the same kind of shim and does still report subsystem 2, which is
    // the reminder that this is a property of the file in front of you and not of the product name.
    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("charmap.exe")]
    public void A_windowed_application_is_not(string name) =>
        Assert.False(Win32ProcessInspector.IsConsoleImage(System32(name)));

    [Fact]
    public void Explorer_is_a_window_even_though_it_is_not_in_system32() =>
        Assert.False(Win32ProcessInspector.IsConsoleImage(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));

    [Fact]
    public void A_file_that_is_not_an_executable_is_treated_as_a_console_process()
    {
        // Unknown means yes: being wrong this way costs one pane the wrong directory, and being
        // wrong the other way throws away the strategy for every pane (ADR 0004).
        var text = Path.Combine(Path.GetTempPath(), $"winmux-not-a-pe-{Guid.NewGuid():N}.txt");
        File.WriteAllText(text, "not an executable");
        try
        {
            Assert.True(Win32ProcessInspector.IsConsoleImage(text));
        }
        finally
        {
            File.Delete(text);
        }
    }

    [Fact]
    public void A_file_that_is_not_there_is_treated_as_a_console_process() =>
        Assert.True(Win32ProcessInspector.IsConsoleImage(
            Path.Combine(Path.GetTempPath(), $"winmux-missing-{Guid.NewGuid():N}.exe")));

    [Fact]
    public void The_running_test_process_is_a_console_application() =>
        Assert.True(new Win32ProcessInspector().IsConsoleProcess(Environment.ProcessId));

    [Fact]
    public void A_process_that_does_not_exist_is_not_claimed_to_be_a_window() =>
        Assert.True(new Win32ProcessInspector().IsConsoleProcess(int.MaxValue - 1));
}
