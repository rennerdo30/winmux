using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// Where a tab group's own "+" puts its new tab.
///
/// <para>
/// Reported from a screenshot: a tab group inside a tab group, and the <em>outer</em> strip's "+"
/// added its tab to the inner group. The button named a pane inside its active child and asked for
/// a tab beside that pane — which is the right answer only while the active child is a single pane,
/// and the wrong one the moment it is a group or a split of its own.
/// </para>
/// </summary>
public class AddTabToStackTests
{
    /// <summary>
    /// The shape in the report: an outer group of two tabs whose second tab is itself a group.
    /// Built directly, because nothing on the tree makes a group inside a group — the shell does it
    /// by constructing the nodes, and so does this.
    /// </summary>
    private static (LayoutTree Tree, StackNode Outer, StackNode Inner) Nested()
    {
        var onceNet = Pane.Terminal("OnceNet");
        var worker = Pane.Terminal("universalWORKER");
        var other = Pane.Terminal("inner-other");

        var inner = new StackNode([new LeafNode(worker), new LeafNode(other)], activeIndex: 0);
        var outer = new StackNode([new LeafNode(onceNet), inner], activeIndex: 1);

        return (new LayoutTree(outer, worker.Id), outer, inner);
    }

    [Fact]
    public void The_outer_plus_adds_to_the_outer_group()
    {
        var (tree, outer, inner) = Nested();
        var before = inner.Children.Count;

        var id = tree.AddTabToStack(outer, Pane.Empty());

        Assert.Equal(3, outer.Children.Count);
        Assert.Equal(before, inner.Children.Count);
        Assert.Contains(outer.Children.OfType<LeafNode>(), leaf => leaf.Pane.Id == id);
    }

    [Fact]
    public void The_inner_plus_still_adds_to_the_inner_group()
    {
        var (tree, outer, inner) = Nested();

        var id = tree.AddTabToStack(inner, Pane.Empty());

        Assert.Equal(2, outer.Children.Count);
        Assert.Equal(3, inner.Children.Count);
        Assert.Contains(inner.Children.OfType<LeafNode>(), leaf => leaf.Pane.Id == id);
    }

    [Fact]
    public void The_new_tab_is_the_active_one_and_lands_beside_the_tab_that_was_showing()
    {
        var (tree, outer, _) = Nested();
        tree.Focus(((LeafNode)outer.Children[0]).Pane.Id);
        Assert.Equal(0, outer.ActiveIndex);

        var id = tree.AddTabToStack(outer, Pane.Empty());

        Assert.Equal(1, outer.ActiveIndex);
        Assert.Equal(id, ((LeafNode)outer.Active).Pane.Id);
        Assert.Equal(id, tree.Focused);
    }

    [Fact]
    public void Asking_for_a_tab_beside_a_pane_is_a_different_question()
    {
        // What the "+" used to ask, kept here because it is the shape of the bug rather than a
        // fault in AddTab: naming the first pane of the active child and asking for a tab beside it
        // is a correct request that lands in the inner group, which is not what the outer group's
        // button meant.
        var (tree, outer, inner) = Nested();

        tree.AddTab(outer.Active.Leaves().First().Pane.Id, Pane.Empty());

        Assert.Equal(3, inner.Children.Count);
        Assert.Equal(2, outer.Children.Count);
    }

    [Fact]
    public void A_stack_holding_a_split_is_the_same_case()
    {
        // The other shape that used to go wrong: the active child is a split, so naming a pane
        // inside it wrapped that pane in a new group rather than adding a tab to this one.
        var one = Pane.Terminal("one");
        var two = Pane.Terminal("two");
        var three = Pane.Terminal("three");

        var split = new SplitNode(SplitDirection.Columns, [new LeafNode(two), new LeafNode(three)]);
        var stack = new StackNode([new LeafNode(one), split], activeIndex: 1);
        var tree = new LayoutTree(stack, two.Id);

        tree.AddTabToStack(stack, Pane.Empty());

        Assert.Equal(3, stack.Children.Count);
        Assert.IsType<SplitNode>(stack.Children[1]);
    }
}
