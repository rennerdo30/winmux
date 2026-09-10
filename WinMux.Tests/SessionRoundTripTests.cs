using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// Priority 1 is session persistence, so this round trip is the load-bearing test of the project.
/// Deliberately format-neutral: JSON vs TOML is still open (CLAUDE.md section 9) and the tree must
/// survive the trip regardless of which is chosen.
/// </summary>
public class SessionRoundTripTests
{
    private static Pane P(string t) => Pane.Terminal(t);

    private static LayoutTree BuildBusyTree(out Pane focused)
    {
        var a = P("shell");
        var tree = new LayoutTree(a) { Bounds = new Rect(10, 20, 1280, 720) };

        var b = P("editor");
        var c = P("logs");
        var d = P("notes");
        var e = P("browser");

        tree.Split(a.Id, SplitDirection.Columns, b, 0.4);
        tree.Split(b.Id, SplitDirection.Rows, c, 0.3);
        tree.AddTab(c.Id, d);
        tree.Split(a.Id, SplitDirection.Rows, e, 0.25);

        focused = c;
        tree.Focus(c.Id);
        return tree;
    }

    private static void AssertSameShape(LayoutNode expected, LayoutNode actual, string path = "root")
    {
        switch (expected)
        {
            case LeafNode le:
                var la = Assert.IsType<LeafNode>(actual);
                Assert.Equal(le.Pane.Id, la.Pane.Id);
                Assert.Equal(le.Pane.Kind, la.Pane.Kind);
                Assert.Equal(le.Pane.Title, la.Pane.Title);
                Assert.Equal(le.Pane.Restore, la.Pane.Restore);
                break;

            case SplitNode se:
                var sa = Assert.IsType<SplitNode>(actual);
                Assert.Equal(se.Direction, sa.Direction);
                Assert.Equal(se.Children.Count, sa.Children.Count);
                for (int i = 0; i < se.Children.Count; i++)
                {
                    Assert.Equal(se.Ratios[i], sa.Ratios[i], 9);
                    AssertSameShape(se.Children[i], sa.Children[i], $"{path}/split[{i}]");
                }
                break;

            case StackNode ste:
                var sta = Assert.IsType<StackNode>(actual);
                Assert.Equal(ste.ActiveIndex, sta.ActiveIndex);
                Assert.Equal(ste.Children.Count, sta.Children.Count);
                for (int i = 0; i < ste.Children.Count; i++)
                    AssertSameShape(ste.Children[i], sta.Children[i], $"{path}/stack[{i}]");
                break;

            default:
                Assert.Fail($"unknown node at {path}");
                break;
        }
    }

    [Fact]
    public void A_tree_survives_the_round_trip_unchanged()
    {
        var tree = BuildBusyTree(out var focused);

        var snapshot = SessionMapper.ToSnapshot(tree, "main");
        var restored = SessionMapper.FromSnapshot(snapshot);

        AssertSameShape(tree.Root, restored.Root);
        Assert.Equal(tree.Focused, restored.Focused);
        Assert.Equal(focused.Id, restored.Focused);
        Assert.Equal(tree.Bounds, restored.Bounds);
    }

    [Fact]
    public void The_round_trip_is_idempotent()
    {
        var tree = BuildBusyTree(out _);

        var once = SessionMapper.FromSnapshot(SessionMapper.ToSnapshot(tree));
        var twice = SessionMapper.FromSnapshot(SessionMapper.ToSnapshot(once));

        AssertSameShape(once.Root, twice.Root);
        Assert.Equal(once.Focused, twice.Focused);
    }

    [Fact]
    public void Arrangement_after_restore_matches_arrangement_before()
    {
        var tree = BuildBusyTree(out _);
        var before = tree.Arrange();

        var restored = SessionMapper.FromSnapshot(SessionMapper.ToSnapshot(tree));
        var after = restored.Arrange();

        Assert.Equal(before.PaneRects.Count, after.PaneRects.Count);
        foreach (var (id, rect) in before.PaneRects)
            Assert.Equal(rect, after[id]);
    }

    [Fact]
    public void The_restore_descriptor_survives_intact_including_cwd_provenance()
    {
        var captured = new WorkingDirectory(@"C:\work\project", CwdSource.ShellReported,
            new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));

        var pane = new Pane(PaneId.New(), PaneKind.Terminal, "build", new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = "build",
            Program = @"C:\Program Files\PowerShell\7\pwsh.exe",
            Args = ["-NoLogo", "-NoProfile"],
            EnvOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["WINMUX_PANE"] = "1" },
            Cwd = captured,
            Strategy = HostStrategy.Attach,
            Extras = new Dictionary<string, string>(StringComparer.Ordinal) { ["profile"] = "pwsh" },
        });

        var tree = new LayoutTree(pane);
        var restored = SessionMapper.FromSnapshot(SessionMapper.ToSnapshot(tree));
        var back = Assert.IsType<LeafNode>(restored.Root).Pane;

        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", back.Restore.Program);
        Assert.Equal(["-NoLogo", "-NoProfile"], back.Restore.Args);
        Assert.Equal("1", back.Restore.EnvOverrides["WINMUX_PANE"]);
        Assert.Equal(captured, back.Restore.Cwd);
        Assert.Equal(CwdSource.ShellReported, back.Restore.Cwd.Source);
        Assert.Equal(HostStrategy.Attach, back.Restore.Strategy);
        Assert.Equal("pwsh", back.Restore.Extras["profile"]);
    }

    // ---------------- refusing to guess ----------------

    [Fact]
    public void A_split_with_one_child_is_rejected_rather_than_quietly_repaired()
    {
        var bad = new NodeSnapshot
        {
            Kind = NodeKinds.Split,
            Direction = SplitDirection.Columns,
            Children = [Leaf()],
        };
        var ex = Assert.Throws<SessionFormatException>(() => SessionMapper.FromSnapshot(bad));
        Assert.Contains("at least 2 children", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_mismatched_ratio_count_is_rejected()
    {
        var bad = new NodeSnapshot
        {
            Kind = NodeKinds.Split,
            Direction = SplitDirection.Columns,
            Children = [Leaf(), Leaf()],
            Ratios = [0.5, 0.3, 0.2],
        };
        Assert.Throws<SessionFormatException>(() => SessionMapper.FromSnapshot(bad));
    }

    [Fact]
    public void An_unknown_node_kind_is_rejected()
    {
        var bad = new NodeSnapshot { Kind = "carousel" };
        var ex = Assert.Throws<SessionFormatException>(() => SessionMapper.FromSnapshot(bad));
        Assert.Contains("carousel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_future_version_is_refused_with_advice_rather_than_discarded()
    {
        var snapshot = new SessionSnapshot
        {
            Version = SessionSnapshot.CurrentVersion + 1,
            Windows = [new WindowSnapshot { Root = Leaf() }],
        };

        var ex = Assert.Throws<SessionFormatException>(() => SessionMapper.Validate(snapshot));
        Assert.Contains("Upgrade WinMux", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unversioned_file_is_refused()
    {
        var snapshot = new SessionSnapshot { Version = 0, Windows = [new WindowSnapshot { Root = Leaf() }] };
        Assert.Throws<SessionFormatException>(() => SessionMapper.Validate(snapshot));
    }

    [Fact]
    public void A_valid_snapshot_passes_validation()
    {
        var tree = BuildBusyTree(out _);
        var snapshot = new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, "main")],
        };
        Assert.Same(snapshot, SessionMapper.Validate(snapshot));
    }

    private static NodeSnapshot Leaf() => new()
    {
        Kind = NodeKinds.Leaf,
        Pane = new PaneSnapshot
        {
            Id = Guid.NewGuid(),
            Kind = PaneKind.Terminal,
            Title = "t",
            Restore = new RestoreDescriptor { Kind = PaneKind.Terminal },
        },
    };
}
