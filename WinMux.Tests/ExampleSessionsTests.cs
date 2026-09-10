using System.Reflection;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// Every session file committed under `examples/` must load. An example that has quietly stopped
/// parsing is worse than no example — it is documentation that lies, and it would be found by a
/// user rather than by us.
/// </summary>
public class ExampleSessionsTests
{
    private static string ExamplesDir()
    {
        var path = typeof(ExampleSessionsTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ExamplesPath")?.Value;

        Assert.False(string.IsNullOrEmpty(path), "The test project must pass ExamplesPath through as assembly metadata.");
        Assert.True(Directory.Exists(path), $"examples directory not found at {path}");
        return path!;
    }

    public static TheoryData<string> ExampleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ExamplesDir(), "*.toml")) data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Every_committed_example_loads(string fileName)
    {
        var snapshot = SessionFile.Load(Path.Combine(ExamplesDir(), fileName));
        Assert.NotEmpty(snapshot.Windows);

        foreach (var window in snapshot.Windows)
        {
            var tree = SessionMapper.FromSnapshot(window);
            Assert.NotEmpty(tree.Panes);
            Assert.Contains(tree.Focused, tree.Panes.Select(p => p.Id));

            // and it must arrange without exploding at a realistic size
            tree.Bounds = new Rect(0, 0, 1920, 1040);
            Assert.NotEmpty(tree.Arrange().PaneRects);
        }
    }

    [Fact]
    public void There_is_at_least_one_example()
    {
        Assert.NotEmpty(Directory.GetFiles(ExamplesDir(), "*.toml"));
    }

    [Fact]
    public void The_cmd_and_explorer_example_is_what_it_claims_to_be()
    {
        var snapshot = SessionFile.Load(Path.Combine(ExamplesDir(), "cmd-and-explorer.toml"));
        var tree = SessionMapper.FromSnapshot(snapshot.Windows.Single());

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(SplitDirection.Columns, split.Direction);   // a vertical divider, side by side
        Assert.Equal(2, split.Children.Count);

        var cmd = tree.Panes.Single(p => p.Kind == PaneKind.Terminal);
        var explorer = tree.Panes.Single(p => p.Kind == PaneKind.ForeignApp);

        Assert.EndsWith("cmd.exe", cmd.Restore.Program, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("explorer.exe", explorer.Restore.Program, StringComparison.OrdinalIgnoreCase);

        // The point of the example: both panes open on the same directory.
        Assert.Equal(cmd.Restore.Cwd.Path, explorer.Restore.Cwd.Path);
        Assert.Contains(cmd.Restore.Cwd.Path, explorer.Restore.Args);

        // Explorer's window is found by class, per ADR 0003 — its launcher process exits at once.
        Assert.Equal("CabinetWClass", explorer.Restore.Extras["window_class"]);

        // and side by side really does mean side by side
        tree.Bounds = new Rect(0, 0, 1920, 1040);
        var arranged = tree.Arrange();
        var left = arranged[cmd.Id];
        var right = arranged[explorer.Id];
        Assert.True(left.Right <= right.Left, "the panes are not side by side");
        Assert.Equal(left.Height, right.Height);
        Assert.Equal(1920, left.Width + right.Width + Layouter.DividerThickness);
    }
}
