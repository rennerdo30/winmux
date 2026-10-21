using WinMux.Core.Session;
using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Shell.Tests;

public sealed class StartupArgumentsTests
{
    [Theory]
    [InlineData("--open-directory")]
    [InlineData("--open-directory", "")]
    [InlineData("--open-directory", " ")]
    [InlineData("--open-directory", "bad\0path")]
    [InlineData("--open-directory", "--no-prefix")]
    [InlineData("--open-directory", @"\\server\share")]
    [InlineData("one.toml", "two.toml")]
    [InlineData("--keymap")]
    [InlineData("--keymap", "--open-directory")]
    public void InvalidArgumentsFailBeforeStartup(params string[] argv) =>
        Assert.Throws<ArgumentException>(() => Program.ShellArguments.Parse(argv));

    [Fact]
    public void UnavailableDirectoryAndFileAreRejectedWithoutCreatingThem()
    {
        var missing = Path.Combine(Path.GetTempPath(), "winmux-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("unavailable", Assert.Throws<ArgumentException>(() =>
            Program.ShellArguments.Parse(["--open-directory", missing])).Message);
        Assert.False(Directory.Exists(missing));
        Assert.Contains("unavailable", Assert.Throws<ArgumentException>(() =>
            Program.ShellArguments.Parse(["--open-directory", typeof(Program).Assembly.Location])).Message);
    }

    [Fact]
    public void RelativeDirectoryResolvesAgainstCapturedCaller()
    {
        var caller = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        var arguments = Program.ShellArguments.Parse(["--open-directory", "."], caller);
        Assert.Equal(Path.GetFullPath(caller), arguments.OpenDirectory);
        Assert.Equal(arguments.OpenDirectory, arguments.SecondaryRequest(Path.GetTempPath()).Directory);
    }

    [Fact]
    public void ExplicitSessionAndRepeatedFolderIntentAreClearlyRefused()
    {
        var folder = Path.GetTempPath();
        foreach (var argv in new[]
        {
            new[] { "named.toml", "--open-directory", folder },
            new[] { "--open-directory", folder, "named.toml" },
        })
            Assert.Contains("explicit session", Assert.Throws<ArgumentException>(() => Program.ShellArguments.Parse(argv)).Message);
        Assert.Contains("only once", Assert.Throws<ArgumentException>(() =>
            Program.ShellArguments.Parse(["--open-directory", folder, "--open-directory", folder])).Message);
    }

    [Theory]
    [InlineData("named.toml")]
    [InlineData("--keymap", "custom.json")]
    [InlineData("--no-prefix")]
    [InlineData("named.toml", "--keymap", "custom.json", "--no-prefix")]
    public void ExistingPrimaryStartupOptionsRemainParsedButRefusedWhenWarm(params string[] argv)
    {
        var arguments = Program.ShellArguments.Parse(argv);
        Assert.Null(arguments.OpenDirectory);
        Assert.Equal(argv.Contains("named.toml") ? "named.toml" : null, arguments.SessionPath);
        Assert.Equal(argv.Contains("custom.json") ? "custom.json" : null, arguments.KeymapPath);
        Assert.Equal(argv.Contains("--no-prefix"), arguments.NoPrefix);
        Assert.Contains("cannot be applied", Assert.Throws<ArgumentException>(() => arguments.SecondaryRequest(Path.GetTempPath())).Message);
    }

    [Fact]
    public void FolderCanUsePrimaryKeymapOptionsButCannotDiscardThemWhenWarm()
    {
        var arguments = Program.ShellArguments.Parse(["--open-directory", Path.GetTempPath(), "--keymap", "custom.json", "--no-prefix"]);
        Assert.Equal("custom.json", arguments.KeymapPath);
        Assert.True(arguments.NoPrefix);
        Assert.Throws<ArgumentException>(() => arguments.SecondaryRequest(Path.GetTempPath()));
    }

    [Fact]
    public void ExplicitSessionLoadsItsOwnLayoutWithoutWritingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "winmux-session-" + Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            var source = new SessionSnapshot
            {
                SavedAt = DateTimeOffset.UtcNow,
                Windows = [SessionMapper.ToSnapshot(new LayoutTree(Pane.FileBrowser(Path.GetTempPath())), "named layout")],
            };
            SessionFile.Save(path, source);
            var savedBytes = File.ReadAllBytes(path);
            var arguments = Program.ShellArguments.Parse([path, "--no-prefix"]);
            var loaded = Program.LoadStartupSession(arguments.SessionPath!, arguments, Path.GetTempPath());
            Assert.True(loaded.Restored);
            Assert.Equal("named layout", Assert.Single(loaded.Snapshot.Windows).Title);
            Assert.Equal(PaneKind.FileBrowser, Assert.Single(SessionMapper.FromSnapshot(loaded.Snapshot.Windows[0]).Panes).Kind);
            Assert.Equal(savedBytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
