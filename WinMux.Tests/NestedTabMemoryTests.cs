using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// A tab group inside a tab group keeps its place.
///
/// <para>
/// Asked for: "when switching from one upper tab group to another and back, it would be nice if the
/// sub tab group's tab from before would stay active instead of the first one of the group".
/// </para>
///
/// <para>
/// It did not, and the reason is worth stating because it is not obvious. Selecting an outer tab
/// focused the <em>first</em> leaf underneath it, and focusing a leaf reveals it — every tab group
/// above it is set to show it. So the act of returning to the outer tab was itself what reset the
/// inner one.
/// </para>
/// </summary>
public class NestedTabMemoryTests
{
    /// <summary>Two outer tabs; the second is a group of three.</summary>
    private static (LayoutTree Tree, StackNode Outer, StackNode Inner, Pane[] Panes) Nested()
    {
        var alone = Pane.Terminal("alone");
        var first = Pane.Terminal("inner-1");
        var second = Pane.Terminal("inner-2");
        var third = Pane.Terminal("inner-3");

        var inner = new StackNode(
            [new LeafNode(first), new LeafNode(second), new LeafNode(third)], activeIndex: 0);
        var outer = new StackNode([new LeafNode(alone), inner], activeIndex: 0);

        return (new LayoutTree(outer, alone.Id), outer, inner, [alone, first, second, third]);
    }

    [Fact]
    public void A_group_shows_the_leaf_its_active_tab_holds()
    {
        var (tree, outer, inner, panes) = Nested();

        // Before anything is focused, both groups are on their first tab.
        Assert.Equal(panes[1].Id, inner.ActiveLeaf().Pane.Id);
        Assert.Equal(panes[0].Id, outer.ActiveLeaf().Pane.Id);

        tree.Focus(panes[3].Id);

        Assert.Equal(2, inner.ActiveIndex);
        Assert.Equal(panes[3].Id, inner.ActiveLeaf().Pane.Id);

        // And the outer group now shows the group, which shows the third tab.
        Assert.Equal(panes[3].Id, outer.ActiveLeaf().Pane.Id);
    }

    [Fact]
    public void Leaving_an_inner_group_and_coming_back_keeps_its_tab()
    {
        // The reported case, in the order it happens: pick the third inner tab, go out to the
        // other outer tab, come back.
        var (tree, outer, inner, panes) = Nested();
        tree.Focus(panes[3].Id);
        Assert.Equal(2, inner.ActiveIndex);

        tree.Focus(panes[0].Id);
        tree.Focus(outer.Children[1].ActiveLeaf().Pane.Id);

        Assert.Equal(2, inner.ActiveIndex);
        Assert.Equal(panes[3].Id, tree.Focused);
    }

    [Fact]
    public void Focusing_the_first_leaf_is_what_used_to_reset_it()
    {
        // Kept as the shape of the bug rather than as a fault in Focus: revealing a leaf really
        // does mean setting every tab group above it to show that leaf, and asking for the first
        // leaf is therefore asking for the first tab.
        var (tree, outer, inner, panes) = Nested();
        tree.Focus(panes[3].Id);

        tree.Focus(outer.Children[1].Leaves().First().Pane.Id);

        Assert.Equal(0, inner.ActiveIndex);
    }

    [Fact]
    public void Cycling_into_a_group_lands_where_it_left_off()
    {
        // Ctrl+B n and p had the same fault, and a fix to the click path alone would have missed
        // it. Note that cycling acts on the group the focus is *in* — from a pane inside the inner
        // group it moves the inner tabs, which is why this starts from the lone one.
        var (tree, _, inner, panes) = Nested();
        tree.Focus(panes[3].Id);
        tree.Focus(panes[0].Id);

        tree.CycleTab(1);

        Assert.Equal(2, inner.ActiveIndex);
        Assert.Equal(panes[3].Id, tree.Focused);
    }

    [Fact]
    public void Three_deep_works_the_same_way()
    {
        var deepest = Pane.Terminal("deep");
        var beside = Pane.Terminal("beside");
        var elsewhere = Pane.Terminal("elsewhere");

        var innermost = new StackNode([new LeafNode(beside), new LeafNode(deepest)], activeIndex: 1);
        var middle = new StackNode([innermost], activeIndex: 0);
        var outer = new StackNode([new LeafNode(elsewhere), middle], activeIndex: 0);
        var tree = new LayoutTree(outer, elsewhere.Id);

        Assert.Equal(deepest.Id, middle.ActiveLeaf().Pane.Id);

        tree.Focus(middle.ActiveLeaf().Pane.Id);

        Assert.Equal(1, innermost.ActiveIndex);
        Assert.Equal(deepest.Id, tree.Focused);
    }

    [Fact]
    public void A_split_has_no_tab_to_remember_so_it_takes_its_first_pane()
    {
        // Both halves of a split are on screen, so there is nothing hidden and nothing to come back
        // to. Only tab groups have somewhere to return to.
        var left = Pane.Terminal("left");
        var right = Pane.Terminal("right");
        var split = new SplitNode(SplitDirection.Columns, [new LeafNode(left), new LeafNode(right)]);

        Assert.Equal(left.Id, split.ActiveLeaf().Pane.Id);
    }

    [Fact]
    public void A_lone_pane_is_its_own_active_leaf()
    {
        var pane = Pane.Terminal("only");

        Assert.Equal(pane.Id, new LeafNode(pane).ActiveLeaf().Pane.Id);
    }

    [Fact]
    public void Closing_the_remembered_tab_does_not_leave_the_group_pointing_at_nothing()
    {
        var (tree, _, inner, panes) = Nested();
        tree.Focus(panes[3].Id);

        tree.Close(panes[3].Id);

        Assert.InRange(inner.ActiveIndex, 0, inner.Children.Count - 1);
        Assert.Contains(tree.Panes, pane => pane.Id == tree.Focused);
    }
}
