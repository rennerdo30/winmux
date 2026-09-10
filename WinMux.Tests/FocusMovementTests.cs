using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

/// <summary>
/// Focus movement is geometric, not structural: the tree shape is not what the user sees, so
/// "the pane to the left" has to be answered from the arranged rectangles.
/// </summary>
public class FocusMovementTests
{
    private static Pane P(string t) => Pane.Terminal(t);

    /// <summary>
    /// Builds a 2x2 grid:
    ///     topLeft  | topRight
    ///     ---------+----------
    ///     botLeft  | botRight
    /// </summary>
    private static LayoutTree Grid(out Pane topLeft, out Pane topRight, out Pane botLeft, out Pane botRight)
    {
        topLeft = P("tl");
        topRight = P("tr");
        botLeft = P("bl");
        botRight = P("br");

        var tree = new LayoutTree(topLeft) { Bounds = new Rect(0, 0, 400, 400) };
        tree.Split(topLeft.Id, SplitDirection.Columns, topRight);
        tree.Split(topLeft.Id, SplitDirection.Rows, botLeft);
        tree.Split(topRight.Id, SplitDirection.Rows, botRight);
        return tree;
    }

    [Fact]
    public void The_grid_really_is_a_grid()
    {
        var tree = Grid(out var tl, out var tr, out var bl, out var br);
        var a = tree.Arrange();

        Assert.True(a[tl.Id].Right <= a[tr.Id].Left, "top-left is not left of top-right");
        Assert.True(a[bl.Id].Right <= a[br.Id].Left, "bottom-left is not left of bottom-right");
        Assert.True(a[tl.Id].Bottom <= a[bl.Id].Top, "top-left is not above bottom-left");
        Assert.True(a[tr.Id].Bottom <= a[br.Id].Top, "top-right is not above bottom-right");
    }

    [Theory]
    [InlineData("tl", FocusDirection.Right, "tr")]
    [InlineData("tl", FocusDirection.Down, "bl")]
    [InlineData("tr", FocusDirection.Left, "tl")]
    [InlineData("tr", FocusDirection.Down, "br")]
    [InlineData("bl", FocusDirection.Up, "tl")]
    [InlineData("bl", FocusDirection.Right, "br")]
    [InlineData("br", FocusDirection.Left, "bl")]
    [InlineData("br", FocusDirection.Up, "tr")]
    public void Focus_moves_to_the_neighbour_in_that_direction(string from, FocusDirection dir, string expected)
    {
        var tree = Grid(out var tl, out var tr, out var bl, out var br);
        var byName = new Dictionary<string, Pane> { ["tl"] = tl, ["tr"] = tr, ["bl"] = bl, ["br"] = br };

        tree.Focus(byName[from].Id);
        Assert.True(tree.MoveFocus(dir), $"no pane found {dir} of {from}");
        Assert.Equal(byName[expected].Id, tree.Focused);
    }

    [Theory]
    [InlineData("tl", FocusDirection.Left)]
    [InlineData("tl", FocusDirection.Up)]
    [InlineData("br", FocusDirection.Right)]
    [InlineData("br", FocusDirection.Down)]
    public void Focus_does_not_move_off_the_edge(string from, FocusDirection dir)
    {
        var tree = Grid(out var tl, out var tr, out var bl, out var br);
        var byName = new Dictionary<string, Pane> { ["tl"] = tl, ["tr"] = tr, ["bl"] = bl, ["br"] = br };

        tree.Focus(byName[from].Id);
        Assert.False(tree.MoveFocus(dir));
        Assert.Equal(byName[from].Id, tree.Focused);
    }

    [Fact]
    public void Focus_crosses_a_nesting_boundary_when_the_geometry_says_so()
    {
        // Structurally tr and bl are in different subtrees; geometrically bl is directly below tl.
        var tree = Grid(out var tl, out _, out var bl, out _);
        tree.Focus(tl.Id);
        Assert.True(tree.MoveFocus(FocusDirection.Down));
        Assert.Equal(bl.Id, tree.Focused);
    }

    [Fact]
    public void With_uneven_panes_the_nearest_aligned_neighbour_wins()
    {
        // Left column is one tall pane; right column is split into three.
        var left = P("left");
        var r1 = P("r1");
        var r2 = P("r2");
        var r3 = P("r3");

        var tree = new LayoutTree(left) { Bounds = new Rect(0, 0, 400, 300) };
        tree.Split(left.Id, SplitDirection.Columns, r1);
        tree.Split(r1.Id, SplitDirection.Rows, r2);
        tree.Split(r2.Id, SplitDirection.Rows, r3);

        var arranged = tree.Arrange();
        tree.Focus(left.Id);
        Assert.True(tree.MoveFocus(FocusDirection.Right));

        // Whichever of the right-hand panes is closest to the tall pane's vertical centre.
        var expected = new[] { r1, r2, r3 }
            .OrderBy(p => Math.Abs(arranged[p.Id].CenterY - arranged[left.Id].CenterY))
            .First();
        Assert.Equal(expected.Id, tree.Focused);
    }

    [Fact]
    public void Focus_movement_ignores_panes_hidden_in_inactive_tabs()
    {
        var a = P("a");
        var b = P("b");
        var hidden = P("hidden");

        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 400, 200) };
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.AddTab(b.Id, hidden);   // hidden is now active; b is behind it

        tree.Focus(a.Id);
        Assert.True(tree.MoveFocus(FocusDirection.Right));
        Assert.Equal(hidden.Id, tree.Focused);   // the visible one, not the buried one
    }

    [Fact]
    public void Directional_focus_is_a_no_op_before_the_shell_has_supplied_bounds()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a);          // Bounds left empty on purpose
        tree.Split(a.Id, SplitDirection.Columns, b);

        Assert.False(tree.MoveFocus(FocusDirection.Left));
    }

    [Fact]
    public void Moving_focus_back_and_forth_returns_to_the_starting_pane()
    {
        var tree = Grid(out var tl, out _, out _, out _);
        tree.Focus(tl.Id);

        tree.MoveFocus(FocusDirection.Right);
        tree.MoveFocus(FocusDirection.Left);

        Assert.Equal(tl.Id, tree.Focused);
    }
}
