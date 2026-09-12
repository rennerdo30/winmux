using WinMux.Core.Model;

namespace WinMux.Tests;

public sealed class WorkingDirectoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Shell_report_beats_root_peb_even_when_root_is_newer()
    {
        var shell = new WorkingDirectory(@"C:\shell", CwdSource.ShellReported, Now);
        var root = new WorkingDirectory(@"C:\launch", CwdSource.ProcessRoot, Now.AddMinutes(1));

        Assert.Same(shell, WorkingDirectory.Better(shell, root));
    }

    [Fact]
    public void Fresh_deepest_child_beats_stale_shell_report()
    {
        var shell = new WorkingDirectory(@"C:\outer", CwdSource.ShellReported, Now);
        var child = new WorkingDirectory(@"C:\nested", CwdSource.ProcessDeepest, Now.AddSeconds(1));

        Assert.Same(child, WorkingDirectory.Better(shell, child));
        Assert.Same(child, WorkingDirectory.Better(child, shell));
    }

    [Fact]
    public void Fresh_shell_report_beats_older_deepest_child()
    {
        var child = new WorkingDirectory(@"C:\nested", CwdSource.ProcessDeepest, Now);
        var shell = new WorkingDirectory(@"C:\outer", CwdSource.ShellReported, Now.AddSeconds(1));

        Assert.Same(shell, WorkingDirectory.Better(child, shell));
    }
}
