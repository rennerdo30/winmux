using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// Swapping a pane in place is how an empty pane becomes something. The property that matters is
/// that the layout the user built does not move: only the contents change.
/// </summary>
public class ReplacePaneTests
{
    private static Pane P(string t) => Pane.Terminal(t);

    [Fact]
    public void The_replacement_takes_the_same_rectangle()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.Split(a.Id, SplitDirection.Columns, b, ratio: 0.3);
        var before = tree.Arrange()[b.Id];

        var c = P("c");
        Assert.True(tree.ReplacePane(b.Id, c));

        Assert.Equal(before, tree.Arrange()[c.Id]);
    }

    [Fact]
    public void An_uneven_split_keeps_its_ratio()
    {
        // The share of the split belongs to the position, not to the pane that was in it.
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 1000, 400) };
        tree.Split(a.Id, SplitDirection.Columns, b, ratio: 0.25);
        var widthBefore = tree.Arrange()[a.Id].Width;

        tree.ReplacePane(a.Id, P("c"));

        Assert.Equal(widthBefore, tree.Arrange().PaneRects.Values.First().Width);
    }

    [Fact]
    public void A_pane_inside_a_tab_group_keeps_its_tab()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, b);

        var c = P("c");
        Assert.True(tree.ReplacePane(b.Id, c));

        var stack = (StackNode)tree.Root;
        Assert.Equal(2, stack.Children.Count);
        Assert.Contains(c.Id, tree.Panes.Select(p => p.Id));
        Assert.DoesNotContain(b.Id, tree.Panes.Select(p => p.Id));
    }

    [Fact]
    public void Replacing_the_focused_pane_moves_focus_to_the_replacement()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.Split(a.Id, SplitDirection.Rows, P("b"));
        tree.Focus(a.Id);

        var c = P("c");
        tree.ReplacePane(a.Id, c);

        Assert.Equal(c.Id, tree.Focused);
    }

    [Fact]
    public void Replacing_an_unfocused_pane_leaves_focus_alone()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.Split(a.Id, SplitDirection.Rows, b);
        tree.Focus(a.Id);

        tree.ReplacePane(b.Id, P("c"));

        Assert.Equal(a.Id, tree.Focused);
    }

    [Fact]
    public void The_only_pane_in_the_tree_can_be_replaced()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };

        var b = P("b");
        Assert.True(tree.ReplacePane(a.Id, b));

        Assert.Equal(b.Id, Assert.Single(tree.Panes).Id);
        Assert.Equal(b.Id, tree.Focused);
    }

    [Fact]
    public void A_pane_that_is_not_in_the_tree_is_refused()
    {
        var tree = new LayoutTree(P("a")) { Bounds = new Rect(0, 0, 800, 600) };

        Assert.False(tree.ReplacePane(PaneId.New(), P("b")));
        Assert.Single(tree.Panes);
    }

    [Fact]
    public void An_empty_pane_is_a_pane_like_any_other()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        var empty = Pane.Empty();

        tree.ReplacePane(a.Id, empty);

        Assert.Equal(PaneKind.Empty, Assert.Single(tree.Panes).Kind);
        Assert.True(tree.Arrange().IsVisible(empty.Id));
    }
}
