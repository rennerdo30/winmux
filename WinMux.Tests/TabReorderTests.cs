using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// Moving a tab within its group.
///
/// Listed as missing since Phase 6 began, and the reason it matters is not symmetry with other
/// multiplexers: a tab strip whose tabs cannot be put in an order is a list the user does not
/// control, and the order is the only thing a strip of four identically-named shells has.
/// </summary>
public sealed class TabReorderTests
{
    private static Pane Terminal(string title) =>
        new(PaneId.New(), PaneKind.Terminal, title, new RestoreDescriptor { Kind = PaneKind.Terminal, Title = title });

    private static LayoutTree Tabbed(out PaneId[] ids)
    {
        var panes = new[]
        {
            Terminal("one"),
            Terminal("two"),
            Terminal("three"),
        };

        ids = panes.Select(p => p.Id).ToArray();
        var stack = new StackNode(panes.Select(p => new LeafNode(p)));
        return new LayoutTree(stack, ids[0]);
    }

    private static string[] Order(LayoutTree tree) =>
        ((StackNode)Root(tree)).Children.Select(c => c.Leaves().First().Pane.Title).ToArray();

    private static LayoutNode Root(LayoutTree tree) => tree.Find(tree.Panes.First().Id)!.Parent!;

    [Fact]
    public void A_tab_moves_later_in_the_strip()
    {
        var tree = Tabbed(out var ids);
        tree.Focus(ids[0]);

        Assert.True(tree.MoveTab(1));

        Assert.Equal(["two", "one", "three"], Order(tree));
    }

    [Fact]
    public void A_tab_moves_earlier_in_the_strip()
    {
        var tree = Tabbed(out var ids);
        tree.Focus(ids[2]);

        Assert.True(tree.MoveTab(-1));

        Assert.Equal(["one", "three", "two"], Order(tree));
    }

    [Fact]
    public void Moving_a_tab_does_not_change_which_tab_is_shown()
    {
        // The whole reason MoveChild tracks the active child by identity: reordering is not
        // selecting, and a strip that switched tabs as you dragged would be unusable.
        var tree = Tabbed(out var ids);
        tree.Focus(ids[1]);
        var stack = (StackNode)Root(tree);
        var activeBefore = stack.Active;

        tree.MoveTab(-1);

        Assert.Same(activeBefore, stack.Active);
        Assert.Equal(ids[1], tree.Focused);
    }

    [Fact]
    public void A_tab_at_the_end_does_not_wrap_around()
    {
        // The opposite of CycleTab on purpose: cycling past the end is right when selecting a tab,
        // and teleporting one from one end of the strip to the other is never a drag you meant.
        var tree = Tabbed(out var ids);
        tree.Focus(ids[2]);

        Assert.False(tree.MoveTab(1));
        Assert.Equal(["one", "two", "three"], Order(tree));
    }

    [Fact]
    public void A_tab_at_the_start_does_not_wrap_around()
    {
        var tree = Tabbed(out var ids);
        tree.Focus(ids[0]);

        Assert.False(tree.MoveTab(-1));
        Assert.Equal(["one", "two", "three"], Order(tree));
    }

    [Fact]
    public void Moving_by_nothing_changes_nothing()
    {
        var tree = Tabbed(out var ids);
        tree.Focus(ids[1]);

        Assert.False(tree.MoveTab(0));
    }

    [Fact]
    public void A_pane_that_is_not_in_a_tab_group_cannot_be_reordered()
    {
        var tree = new LayoutTree(Terminal("alone"));

        Assert.False(tree.MoveTab(1));
    }

    [Fact]
    public void A_tab_can_be_moved_to_an_absolute_position()
    {
        // What a drop knows: where it landed, not how far it came.
        var tree = Tabbed(out var ids);

        Assert.True(tree.MoveTabTo(ids[0], 2));

        Assert.Equal(["two", "three", "one"], Order(tree));
    }

    [Fact]
    public void An_out_of_range_drop_lands_at_the_nearest_end()
    {
        // A pointer past the last tab means "put it last", not "do nothing".
        var tree = Tabbed(out var ids);

        Assert.True(tree.MoveTabTo(ids[0], 99));

        Assert.Equal(["two", "three", "one"], Order(tree));
    }

    [Fact]
    public void Dropping_a_tab_where_it_already_is_changes_nothing()
    {
        var tree = Tabbed(out var ids);

        Assert.False(tree.MoveTabTo(ids[1], 1));
    }

    [Fact]
    public void Moving_an_unknown_pane_does_nothing()
    {
        var tree = Tabbed(out _);

        Assert.False(tree.MoveTabTo(PaneId.New(), 0));
    }

    [Fact]
    public void Reordering_survives_a_round_trip_through_the_layout()
    {
        // The order has to be real geometry, not a display detail: the arrangement must agree.
        var tree = Tabbed(out var ids);
        tree.Focus(ids[0]);
        tree.MoveTab(1);

        var arrangement = tree.Arrange(new Rect(0, 0, 800, 600));
        var stack = (StackNode)Root(tree);

        Assert.Single(arrangement.TabStrips);
        Assert.Equal(stack, arrangement.TabStrips[0].Stack);
        Assert.Equal(["two", "one", "three"], Order(tree));
    }
}
