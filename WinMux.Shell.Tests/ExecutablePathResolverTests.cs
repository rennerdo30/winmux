using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Shell.Tests;

public sealed class ExecutablePathResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "winmux-executable-resolver-" + Guid.NewGuid().ToString("N"));

    public ExecutablePathResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Existing_absolute_path_is_preserved_exactly()
    {
        var executable = CreateFile("tool.exe");

        var result = ExecutablePathResolver.Resolve(executable, path: string.Empty, pathExtensions: ".EXE");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(executable, result.AbsolutePath);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Bare_name_is_resolved_through_path_and_pathext()
    {
        var executable = CreateFile("phase-two.CMD");
        var unrelated = Path.Combine(_directory, "unrelated");
        Directory.CreateDirectory(unrelated);
        var searchPath = string.Join(Path.PathSeparator, unrelated, $"\"{_directory}\"");

        var result = ExecutablePathResolver.Resolve("phase-two", searchPath, ".EXE;.CMD");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(executable, result.AbsolutePath, ignoreCase: true);
    }

    [Fact]
    public void Missing_program_returns_a_surfaceable_failure()
    {
        var result = ExecutablePathResolver.Resolve(
            "definitely-not-installed-winmux-tool",
            _directory,
            ".EXE;.CMD");

        Assert.False(result.Succeeded);
        Assert.Null(result.AbsolutePath);
        Assert.Contains("definitely-not-installed-winmux-tool", result.Error);
        Assert.Contains("PATH/PATHEXT", result.Error);
    }

    [Fact]
    public void Terminal_descriptor_is_normalized_before_persistence_or_after_restore()
    {
        var executable = CreateFile("restorable.EXE");
        var original = new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = "restorable",
            Program = "restorable",
            Args = ["--flag"],
            EnvOverrides = new Dictionary<string, string> { ["WINMUX_TEST"] = "1" },
            Cwd = new WorkingDirectory(_directory, CwdSource.LaunchDirectory, DateTimeOffset.UnixEpoch),
        };

        var result = ExecutablePathResolver.ResolveTerminalDescriptor(original, _directory, ".EXE");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(executable, result.Descriptor.Program, ignoreCase: true);
        Assert.NotNull(result.Descriptor.Program);
        Assert.True(Path.IsPathFullyQualified(result.Descriptor.Program));
        Assert.Equal(original.Args, result.Descriptor.Args);
        Assert.Equal(original.EnvOverrides, result.Descriptor.EnvOverrides);
        Assert.Equal(original.Cwd, result.Descriptor.Cwd);

        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "restorable", result.Descriptor);
        var snapshot = new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(new LayoutTree(pane), "main")],
        };
        var restored = SessionMapper.FromSnapshot(
            SessionFile.Deserialize(SessionFile.Serialize(snapshot)).Windows.Single());

        var restoredProgram = restored.Panes.Single().Restore.Program;
        Assert.NotNull(restoredProgram);
        Assert.Equal(executable, restoredProgram, ignoreCase: true);
        Assert.True(Path.IsPathFullyQualified(restoredProgram));
    }

    [Fact]
    public void Descriptor_resolution_failure_retains_original_data_and_explains_the_problem()
    {
        var original = new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Program = "missing-terminal",
            Args = ["/k"],
        };

        var result = ExecutablePathResolver.ResolveTerminalDescriptor(original, _directory, ".EXE");

        Assert.False(result.Succeeded);
        Assert.Same(original, result.Descriptor);
        Assert.Contains("missing-terminal", result.Error);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
