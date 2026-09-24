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

/// <summary>
/// What happens to a group's name when the group stops being one.
///
/// Reported as "if only one subtab is present somehow the name of the upper tab changes": closing a
/// group's second tab collapses it into the pane that is left, and the name the user gave the group
/// went with the group — so the tab was suddenly called whatever that pane was called, with nothing
/// saying anything had been discarded.
/// </summary>
public class CollapsedGroupNameTests
{
    private static (LayoutTree Tree, StackNode Group, Pane First, Pane Second) Group()
    {
        var first = Pane.Terminal("inner-1");
        var second = Pane.Terminal("inner-2");
        var other = Pane.Terminal("beside");

        var group = new StackNode([new LeafNode(first), new LeafNode(second)], activeIndex: 0);
        var root = new SplitNode(SplitDirection.Columns, [group, new LeafNode(other)]);
        return (new LayoutTree(root, first.Id), group, first, second);
    }

    [Fact]
    public void A_named_group_hands_its_name_to_the_pane_that_is_left()
    {
        var (tree, group, first, second) = Group();
        group.Title = "CAD";

        tree.Close(second.Id);

        var leaf = tree.Root.Leaves().First(node => node.Pane.Id == first.Id);
        Assert.Equal("CAD", leaf.Title);
    }

    [Fact]
    public void An_unnamed_group_hands_over_nothing()
    {
        var (tree, _, first, second) = Group();

        tree.Close(second.Id);

        Assert.Equal(string.Empty, tree.Root.Leaves().First(node => node.Pane.Id == first.Id).Title);
    }

    [Fact]
    public void A_pane_that_has_its_own_name_keeps_it()
    {
        // The group's name is a fallback for a pane that has none, not something that overwrites
        // a name the user typed on the pane itself.
        var (tree, group, first, second) = Group();
        group.Title = "CAD";
        tree.Root.Leaves().First(node => node.Pane.Id == first.Id).Title = "kept";

        tree.Close(second.Id);

        Assert.Equal("kept", tree.Root.Leaves().First(node => node.Pane.Id == first.Id).Title);
    }

    [Fact]
    public void A_named_split_hands_its_name_over_too()
    {
        var left = Pane.Terminal("left");
        var right = Pane.Terminal("right");
        var other = Pane.Terminal("beside");
        var split = new SplitNode(SplitDirection.Columns, [new LeafNode(left), new LeafNode(right)])
        {
            Title = "Workbench",
        };
        var root = new SplitNode(SplitDirection.Rows, [split, new LeafNode(other)]);
        var tree = new LayoutTree(root, left.Id);

        tree.Close(right.Id);

        Assert.Equal("Workbench", tree.Root.Leaves().First(node => node.Pane.Id == left.Id).Title);
    }
}
