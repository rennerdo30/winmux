using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// A stack's tab strip is geometry, not decoration.
///
/// The reason it has to be is CLAUDE.md section 6: a pane may host a native window, and a native
/// child window paints above anything the shell draws. Tabs drawn *over* a pane would vanish behind
/// Explorer the moment a foreign app opened in it. So the strip is carved out of the stack's own
/// rectangle before the active child is placed, and the two can never overlap — which is a property
/// worth asserting rather than hoping for.
/// </summary>
public class TabStripLayoutTests
{
    private static Pane P(string t) => Pane.Terminal(t);
    private const int Strip = Layouter.TabStripThickness;
    private const int VStrip = Layouter.VerticalTabStripThickness;

    [Fact]
    public void A_tree_with_no_stack_has_no_strips()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };

        Assert.Empty(tree.Arrange().TabStrips);
    }

    [Theory]
    [InlineData(TabStripPlacement.Top)]
    [InlineData(TabStripPlacement.Bottom)]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void The_strip_never_overlaps_the_pane_it_labels(TabStripPlacement placement)
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, b);
        StackOf(tree).TabStrip = placement;

        var arranged = tree.Arrange();
        var strip = Assert.Single(arranged.TabStrips);
        var pane = arranged[b.Id];

        Assert.Equal(placement, strip.Placement);
        Assert.False(
            strip.Rect.OverlapsHorizontally(pane) && strip.Rect.OverlapsVertically(pane),
            $"The {placement} strip {strip.Rect} overlaps the pane it labels at {pane}.");
    }

    [Theory]
    [InlineData(TabStripPlacement.Top, 0, 0, 800, Strip)]
    [InlineData(TabStripPlacement.Bottom, 0, 600 - Strip, 800, Strip)]
    // A vertical strip is much wider than a horizontal one is tall: it lists titles down the
    // side, and 28 pixels of truncated text would be the feature in name only.
    [InlineData(TabStripPlacement.Left, 0, 0, VStrip, 600)]
    [InlineData(TabStripPlacement.Right, 800 - VStrip, 0, VStrip, 600)]
    public void The_strip_takes_its_own_edge(TabStripPlacement placement, int x, int y, int w, int h)
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        StackOf(tree).TabStrip = placement;

        var strip = Assert.Single(tree.Arrange().TabStrips);

        Assert.Equal(new Rect(x, y, w, h), strip.Rect);
    }

    [Fact]
    public void The_content_keeps_everything_the_strip_did_not_take()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(10, 20, 800, 600) };
        tree.AddTab(a.Id, b);

        var arranged = tree.Arrange();

        Assert.Equal(new Rect(10, 20 + Strip, 800, 600 - Strip), arranged[b.Id]);
    }

    [Fact]
    public void Only_the_active_tab_has_any_geometry_at_all()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, b);

        var arranged = tree.Arrange();

        Assert.True(arranged.IsVisible(b.Id));
        Assert.False(arranged.IsVisible(a.Id));
    }

    [Fact]
    public void Every_stack_in_the_tree_gets_its_own_strip_where_it_actually_sits()
    {
        // The whole point of the change. One bar at the top of the window can only ever describe
        // one stack; a tree with two stacks side by side needs two, in two different places.
        var left = P("left");
        var right = P("right");
        var tree = new LayoutTree(left) { Bounds = new Rect(0, 0, 800, 600) };
        tree.Split(left.Id, SplitDirection.Columns, right);
        tree.AddTab(left.Id, P("left tab"));
        tree.AddTab(right.Id, P("right tab"));

        var strips = tree.Arrange().TabStrips;

        Assert.Equal(2, strips.Count);
        Assert.All(strips, s => Assert.Equal(Strip, s.Rect.Height));
        Assert.Distinct(strips.Select(s => s.Rect.X));
    }

    [Fact]
    public void A_stack_inside_a_stack_gets_a_strip_of_its_own()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, b);              // outer stack: a | b
        tree.Split(b.Id, SplitDirection.Rows, P("c"));
        var inner = P("inner tab");
        tree.AddTab(b.Id, inner);          // a stack nested inside the outer stack's active branch

        var arranged = tree.Arrange();

        Assert.Equal(2, arranged.TabStrips.Count);
        // The inner strip is below the outer one, because the outer one was reserved first.
        var outer = arranged.TabStrips[0];
        var nested = arranged.TabStrips[1];
        Assert.True(nested.Rect.Y > outer.Rect.Y, "A nested strip must sit inside its parent's content.");
        Assert.False(
            arranged[inner.Id].OverlapsVertically(nested.Rect) &&
            arranged[inner.Id].OverlapsHorizontally(nested.Rect),
            "The nested strip overlaps its own pane.");
    }

    [Fact]
    public void A_stack_squeezed_to_nothing_loses_its_tabs_rather_than_its_panes()
    {
        // The panes are what the user is looking at. Dropping the strip is recoverable by resizing;
        // a zero-height pane that has quietly stopped existing is not.
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 10) };
        tree.AddTab(a.Id, b);

        var arranged = tree.Arrange();

        Assert.Empty(arranged.TabStrips);
        Assert.Equal(new Rect(0, 0, 800, 10), arranged[b.Id]);
    }

    [Fact]
    public void A_caller_that_wants_no_strips_gets_none()
    {
        // The CLI renders the same tree into a character grid, where a 28-row tab strip is absurd.
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 80, 24) };
        tree.AddTab(a.Id, b);

        var arranged = Layouter.Arrange(tree.Root, tree.Bounds, LayoutMetrics.CharacterGrid);

        Assert.Empty(arranged.TabStrips);
        Assert.Equal(new Rect(0, 0, 80, 24), arranged[b.Id]);
    }

    [Fact]
    public void A_vertical_strip_says_so()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        StackOf(tree).TabStrip = TabStripPlacement.Left;

        Assert.True(Assert.Single(tree.Arrange().TabStrips).IsVertical);
    }

    [Fact]
    public void Cloning_a_tree_keeps_where_the_tabs_were()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };
        tree.AddTab(a.Id, P("b"));
        StackOf(tree).TabStrip = TabStripPlacement.Right;

        var clone = tree.Clone();

        Assert.Equal(TabStripPlacement.Right, StackOf(clone).TabStrip);
    }

    private static StackNode StackOf(LayoutTree tree) =>
        Descend(tree.Root) ?? throw new InvalidOperationException("The tree has no stack.");

    private static StackNode? Descend(LayoutNode node) => node switch
    {
        StackNode stack => stack,
        SplitNode split => split.Children.Select(Descend).FirstOrDefault(found => found is not null),
        _ => null,
    };
}
