using WinMux.Platform;
using WinMux.Shell.Panes;

namespace WinMux.Shell.Tests;

public sealed class TerminalCloseProtectionTests
{
    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("PWSH.EXE")]
    public void IdleShellDoesNotRequireConfirmation(string shell)
    {
        var result = TerminalCloseProtection.Assess(10,
            [new(10, 1, shell), new(11, 10, "conhost.exe"), new(12, 11, "OpenConsole.exe")]);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void WorkThroughInfrastructureAndNestedShellIsProtected()
    {
        var result = TerminalCloseProtection.Assess(10,
            [new(10, 1, "cmd.exe"), new(11, 10, "conhost.exe"),
             new(12, 11, "pwsh.exe"), new(13, 12, "claude.exe"), new(14, 13, "node.exe")]);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("claude.exe", result.Description);
        Assert.Contains("node.exe", result.Description);
        Assert.DoesNotContain("conhost.exe", result.Description);
    }

    [Theory]
    [InlineData("codex.exe")]
    [InlineData("ssh.exe")]
    [InlineData("wsl.exe")]
    public void DirectProgramOrUninspectableWslRequiresConfirmation(string program)
    {
        var result = TerminalCloseProtection.Assess(10, [new(10, 1, program)]);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains(program, result.Description);
    }

    [Fact]
    public void GuiChildIsProtectedConservatively()
    {
        var result = TerminalCloseProtection.Assess(10,
            [new(10, 1, "cmd.exe"), new(11, 10, "editor.exe")]);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("editor.exe", result.Description);
    }

    [Fact]
    public void FreshSnapshotOfExitedProgramIsSafeAndUnrelatedWorkIsIgnored()
    {
        var result = TerminalCloseProtection.Assess(10, [new(20, 1, "claude.exe")]);
        Assert.False(result.RequiresConfirmation);
    }

    [Fact]
    public void ProcessGraphCyclesDoNotHang()
    {
        var result = TerminalCloseProtection.Assess(10,
            [new(10, 11, "cmd.exe"), new(11, 10, "node.exe")]);
        Assert.True(result.RequiresConfirmation);
        Assert.Contains("node.exe", result.Description);
    }

    [Fact]
    public void MultiplePanesShareOneFreshSnapshot()
    {
        var inspector = new Inspector { Processes = [new(10, 1, "cmd.exe"), new(20, 1, "pwsh.exe"), new(21, 20, "dotnet.exe")] };
        var result = TerminalCloseProtection.Inspect([10, 20, 10], inspector);
        Assert.Equal(1, inspector.Snapshots);
        Assert.False(result[10].RequiresConfirmation);
        Assert.True(result[20].RequiresConfirmation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InspectionFailureRequiresConfirmation(bool throws)
    {
        var inspector = new Inspector { Error = "access denied", Throws = throws };
        var result = TerminalCloseProtection.Inspect([10], inspector);
        Assert.True(result[10].RequiresConfirmation);
        Assert.Contains("could not check", result[10].Description);
        Assert.Contains("access denied", result[10].Description);
    }

    [Fact]
    public void NoTerminalProcessesDoNotTakeASnapshot()
    {
        var inspector = new Inspector();
        Assert.Empty(TerminalCloseProtection.Inspect([], inspector));
        Assert.Equal(0, inspector.Snapshots);
    }

    private sealed class Inspector : IProcessInspector
    {
        public IReadOnlyList<ProcessSnapshotEntry> Processes { get; init; } = [];
        public string? Error { get; init; }
        public bool Throws { get; init; }
        public int Snapshots { get; private set; }
        public IReadOnlyList<ProcessSnapshotEntry> SnapshotProcesses(out string? error)
        {
            Snapshots++;
            if (Throws) throw new InvalidOperationException("access denied");
            error = Error;
            return Processes;
        }
        public string? TryReadWorkingDirectory(int id, out string error) => throw new NotSupportedException();
        public bool IsConsoleProcess(int id) => throw new NotSupportedException();
    }
}
