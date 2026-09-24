using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// A tab group with a name of its own.
///
/// <para>
/// Reported from a screenshot: "tab name cannot be different from first child tab name, which is
/// bad!" It could not. A tab holding a group showed its first pane's title, so renaming that tab
/// renamed that pane — which renamed the inner tab too, because it was the same pane. A group of
/// six machines called "CAD" could only ever be called whatever its first machine was called.
/// </para>
/// </summary>
public class NamedGroupTests
{
    private static SessionSnapshot Snapshot(LayoutTree tree) => new()
    {
        SavedAt = DateTimeOffset.UtcNow,
        Windows = [SessionMapper.ToSnapshot(tree, "named")],
    };

    private static (LayoutTree Tree, StackNode Group, Pane First) Group()
    {
        var first = Pane.Terminal("CAD");
        var second = Pane.Terminal("INSPECT");
        var stack = new StackNode([new LeafNode(first), new LeafNode(second)], activeIndex: 0);
        return (new LayoutTree(stack, first.Id), stack, first);
    }

    [Fact]
    public void A_group_starts_with_no_name_of_its_own()
    {
        // Empty means "describe yourself by what is inside", which is what an unnamed group should
        // do — and what every session written before this existed says.
        var (_, group, _) = Group();

        Assert.Equal(string.Empty, group.Title);
    }

    [Fact]
    public void A_named_group_keeps_its_name_when_the_pane_inside_is_renamed()
    {
        // The reported bug, as a test: the two names are independent in both directions.
        var (_, group, first) = Group();
        group.Title = "CAD";

        first.Title = "something else";

        Assert.Equal("CAD", group.Title);
    }

    [Fact]
    public void A_group_name_survives_the_round_trip()
    {
        var (tree, group, _) = Group();
        group.Title = "CAD";

        var restored = SessionMapper.FromSnapshot(
            SessionFile.Deserialize(SessionFile.Serialize(Snapshot(tree))).Windows[0]);

        Assert.Equal("CAD", ((StackNode)restored.Root).Title);
    }

    [Fact]
    public void A_split_can_be_named_too()
    {
        // A tab can hold a split rather than a group, and it had exactly the same problem.
        var left = Pane.Terminal("left");
        var right = Pane.Terminal("right");
        var split = new SplitNode(SplitDirection.Columns, [new LeafNode(left), new LeafNode(right)])
        {
            Title = "Workbench",
        };
        var tree = new LayoutTree(split, left.Id);

        var restored = SessionMapper.FromSnapshot(
            SessionFile.Deserialize(SessionFile.Serialize(Snapshot(tree))).Windows[0]);

        Assert.Equal("Workbench", ((SplitNode)restored.Root).Title);
    }

    [Fact]
    public void An_unnamed_group_writes_no_title_at_all()
    {
        // The nodes table is the part of the file meant to be read, and a derived name written
        // into it would freeze what is supposed to follow the contents. Panes have titles of their
        // own, so this looks at the node tables rather than at the whole file.
        var (tree, _, _) = Group();

        Assert.DoesNotContain("title", NodeTables(SessionFile.Serialize(Snapshot(tree))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_group_writes_one()
    {
        var (tree, group, _) = Group();
        group.Title = "CAD";

        Assert.Contains("title", NodeTables(SessionFile.Serialize(Snapshot(tree))), StringComparison.Ordinal);
    }

    /// <summary>Only the <c>[[windows.nodes]]</c> tables, which is where a group's name would go.</summary>
    private static string NodeTables(string file) =>
        string.Join(
            Environment.NewLine,
            file.Split("[[windows.")
                .Where(part => part.StartsWith("nodes]]", StringComparison.Ordinal)));

    [Fact]
    public void A_session_written_before_groups_had_names_loads_unnamed()
    {
        var (tree, _, _) = Group();

        var restored = SessionMapper.FromSnapshot(
            SessionFile.Deserialize(SessionFile.Serialize(Snapshot(tree))).Windows[0]);

        Assert.Equal(string.Empty, ((StackNode)restored.Root).Title);
    }

    [Fact]
    public void Cloning_a_group_keeps_its_name()
    {
        // Clone is how snapshots and undo are made; a name lost there is a name lost on the next
        // thing that touches the tree.
        var (_, group, _) = Group();
        group.Title = "CAD";

        Assert.Equal("CAD", group.Clone().Title);
    }
}
