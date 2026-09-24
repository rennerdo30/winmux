using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// "Turn this pane into a tab group", as distinct from "add a tab beside this pane".
///
/// <para>
/// The two are the same thing exactly once — the first time — and the toolbar button promising the
/// first was doing the second on every press after that. Reported as "tab group somehow adds a tab
/// to the currents pane tab view instead of creating a new tab group".
/// </para>
/// </summary>
public class AddTabGroupTests
{
    [Fact]
    public void A_lone_pane_becomes_a_group_of_two()
    {
        var pane = Pane.Terminal("one");
        var tree = new LayoutTree(pane);

        var id = tree.AddTabGroup(pane.Id, Pane.Empty());

        var stack = Assert.IsType<StackNode>(tree.Root);
        Assert.Equal(2, stack.Children.Count);
        Assert.Equal(id, tree.Focused);
    }

    [Fact]
    public void A_pane_already_in_a_group_gets_a_group_nested_inside_it()
    {
        // The reported case. AddTab would have made this a third tab in the outer group.
        var first = Pane.Terminal("one");
        var second = Pane.Terminal("two");
        var tree = new LayoutTree(first);
        tree.AddTab(first.Id, second);
        var outer = (StackNode)tree.Root;

        var id = tree.AddTabGroup(second.Id, Pane.Empty());

        Assert.Equal(2, outer.Children.Count);
        var inner = Assert.IsType<StackNode>(outer.Children[1]);
        Assert.Equal(2, inner.Children.Count);
        Assert.Equal(id, ((LeafNode)inner.Active).Pane.Id);
    }

    [Fact]
    public void Adding_a_tab_is_still_the_other_thing()
    {
        // The distinction, stated as a test so neither one drifts into the other.
        var first = Pane.Terminal("one");
        var second = Pane.Terminal("two");
        var tree = new LayoutTree(first);
        tree.AddTab(first.Id, second);
        var outer = (StackNode)tree.Root;

        tree.AddTab(second.Id, Pane.Empty());

        Assert.Equal(3, outer.Children.Count);
        Assert.All(outer.Children, child => Assert.IsType<LeafNode>(child));
    }

    [Fact]
    public void A_pane_in_a_split_keeps_its_share_of_the_split()
    {
        // The group replaces the pane exactly where it was, so the other half of the split does
        // not move or resize.
        var left = Pane.Terminal("left");
        var right = Pane.Terminal("right");
        var tree = new LayoutTree(left);
        tree.Split(left.Id, SplitDirection.Columns, right, 0.25);

        tree.AddTabGroup(right.Id, Pane.Empty());

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(2, split.Children.Count);
        Assert.IsType<LeafNode>(split.Children[0]);
        Assert.IsType<StackNode>(split.Children[1]);
    }

    [Fact]
    public void The_pane_that_was_there_keeps_its_place_and_its_identity()
    {
        var pane = Pane.Terminal("one");
        var tree = new LayoutTree(pane);

        tree.AddTabGroup(pane.Id, Pane.Empty());

        var stack = (StackNode)tree.Root;
        Assert.Same(pane, ((LeafNode)stack.Children[0]).Pane);
    }

    [Fact]
    public void An_unknown_pane_is_refused_loudly()
    {
        var tree = new LayoutTree(Pane.Terminal("one"));

        Assert.Throws<InvalidOperationException>(() => tree.AddTabGroup(PaneId.New(), Pane.Empty()));
    }
}
