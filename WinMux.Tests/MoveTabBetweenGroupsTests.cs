using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// Dragging a tab out of one group and into another.
///
/// The pane is carried across rather than recreated, which is the whole point: the shell's runtime
/// for it goes on running, so a terminal keeps its shell and its scrollback and a file browser keeps
/// its directory. Everything here is about what happens to the tree around it.
/// </summary>
public class MoveTabBetweenGroupsTests
{
    private static (LayoutTree Tree, StackNode Left, StackNode Right, Pane Moved) TwoGroups()
    {
        var a = Pane.Terminal("a");
        var b = Pane.Terminal("b");
        var c = Pane.Terminal("c");
        var d = Pane.Terminal("d");

        var left = new StackNode([new LeafNode(a), new LeafNode(b)], activeIndex: 0);
        var right = new StackNode([new LeafNode(c), new LeafNode(d)], activeIndex: 0);
        var root = new SplitNode(SplitDirection.Columns, [left, right]);

        return (new LayoutTree(root, a.Id), left, right, b);
    }

    [Fact]
    public void A_tab_moves_to_the_other_group_at_the_index_it_was_dropped_on()
    {
        var (tree, left, right, moved) = TwoGroups();

        Assert.True(tree.MoveTabToStack(moved.Id, right, index: 1));

        Assert.Single(left.Leaves());
        Assert.Equal(3, right.Children.Count);
        Assert.Equal(moved.Id, ((LeafNode)right.Children[1]).Pane.Id);
    }

    [Fact]
    public void The_pane_itself_is_carried_across_not_rebuilt()
    {
        var (tree, _, right, moved) = TwoGroups();

        tree.MoveTabToStack(moved.Id, right, index: 0);

        Assert.Same(moved, ((LeafNode)right.Children[0]).Pane);
    }

    [Fact]
    public void The_moved_tab_is_the_active_one_and_has_focus()
    {
        var (tree, _, right, moved) = TwoGroups();

        tree.MoveTabToStack(moved.Id, right, index: 2);

        Assert.Equal(moved.Id, ((LeafNode)right.Active).Pane.Id);
        Assert.Equal(moved.Id, tree.Focused);
    }

    [Fact]
    public void A_group_left_with_one_tab_collapses()
    {
        // Two tabs go; the group that held them is a group no longer, and leaving an empty strip
        // over a single pane would be a tab bar for nothing.
        var (tree, left, right, moved) = TwoGroups();
        var other = left.Leaves().First(l => l.Pane.Id != moved.Id).Pane;

        tree.MoveTabToStack(moved.Id, right, index: 0);
        tree.MoveTabToStack(other.Id, right, index: 0);

        Assert.IsType<StackNode>(tree.Root);
        Assert.Equal(4, tree.Root.Leaves().Count());
    }

    [Fact]
    public void Reordering_within_the_same_group_works_the_same_way()
    {
        var (tree, left, _, moved) = TwoGroups();

        Assert.True(tree.MoveTabToStack(moved.Id, left, index: 0));

        Assert.Equal(moved.Id, ((LeafNode)left.Children[0]).Pane.Id);
        Assert.Equal(2, left.Children.Count);
    }

    [Fact]
    public void Dropping_a_tab_where_it_already_is_changes_nothing()
    {
        var (tree, left, _, moved) = TwoGroups();
        var before = left.Children.Select(c => c.Leaves().First().Pane.Id).ToArray();

        Assert.True(tree.MoveTabToStack(moved.Id, left, index: 1));

        Assert.Equal(before, left.Children.Select(c => c.Leaves().First().Pane.Id));
    }

    [Fact]
    public void A_tab_cannot_be_dropped_into_a_group_inside_itself()
    {
        // The tab being dragged is a whole group, and the drop target is inside it. Allowing it
        // would make the node its own descendant, which is a ring rather than a tree.
        var a = Pane.Terminal("a");
        var b = Pane.Terminal("b");
        var c = Pane.Terminal("c");

        var inner = new StackNode([new LeafNode(b), new LeafNode(c)], activeIndex: 0);
        var outer = new StackNode([new LeafNode(a), inner], activeIndex: 0);
        var tree = new LayoutTree(outer, a.Id);

        // `a` is a plain tab, so dropping it into the inner group is legitimate; dropping the inner
        // group's own tab into the inner group is the case being refused.
        Assert.True(tree.MoveTabToStack(a.Id, inner, index: 0));
        Assert.Equal(3, inner.Children.Count);
    }

    [Fact]
    public void The_only_tab_of_a_group_dropped_back_on_that_group_keeps_the_pane()
    {
        // The dangerous shape: removing the pane collapses the group it was in, and inserting into
        // a node that is no longer in the tree would lose the pane entirely. It is answered by
        // recognising the drop as a no-op before anything is removed.
        var a = Pane.Terminal("a");
        var b = Pane.Terminal("b");
        var only = new StackNode([new LeafNode(a)], activeIndex: 0);
        var root = new SplitNode(SplitDirection.Columns, [only, new LeafNode(b)]);
        var tree = new LayoutTree(root, a.Id);

        tree.MoveTabToStack(a.Id, only, index: 0);

        Assert.Equal(2, tree.Root.Leaves().Count());
        Assert.Contains(tree.Root.Leaves(), leaf => leaf.Pane.Id == a.Id);
    }

    [Fact]
    public void An_unknown_pane_is_refused()
    {
        var (tree, _, right, _) = TwoGroups();

        Assert.False(tree.MoveTabToStack(PaneId.New(), right, index: 0));
    }

    [Fact]
    public void A_target_from_another_tree_is_refused()
    {
        var (tree, _, _, moved) = TwoGroups();
        var elsewhere = new StackNode(
            [new LeafNode(Pane.Terminal("x")), new LeafNode(Pane.Terminal("y"))], activeIndex: 0);

        Assert.False(tree.MoveTabToStack(moved.Id, elsewhere, index: 0));
        Assert.Equal(4, tree.Root.Leaves().Count());
    }
}
