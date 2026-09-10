using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

public class LayoutTreeTests
{
    private static Pane P(string title) => Pane.Terminal(title);

    private static LayoutTree TreeOf(out Pane a)
    {
        a = P("a");
        return new LayoutTree(a);
    }

    // ---------------- splitting ----------------

    [Fact]
    public void A_new_tree_is_a_single_focused_leaf()
    {
        var tree = TreeOf(out var a);
        Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(a.Id, tree.Focused);
        Assert.Single(tree.Panes);
    }

    [Fact]
    public void Splitting_a_leaf_makes_a_split_and_focuses_the_new_pane()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Columns, b);

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(SplitDirection.Columns, split.Direction);
        Assert.Equal(2, split.Children.Count);
        Assert.Equal(b.Id, tree.Focused);
    }

    [Fact]
    public void Splitting_again_in_the_same_direction_stays_flat_rather_than_nesting()
    {
        // "Split the same way again" should widen the existing row, not build a nested tree —
        // nesting makes the ratios behave in a way users read as a bug.
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.Split(b.Id, SplitDirection.Columns, c);

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(3, split.Children.Count);
        Assert.All(split.Children, ch => Assert.IsType<LeafNode>(ch));
    }

    [Fact]
    public void Splitting_in_the_other_direction_nests()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.Split(b.Id, SplitDirection.Rows, c);

        var root = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(2, root.Children.Count);
        var nested = Assert.IsType<SplitNode>(root.Children[1]);
        Assert.Equal(SplitDirection.Rows, nested.Direction);
    }

    [Fact]
    public void Split_ratio_gives_the_requested_share_to_the_new_pane()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Columns, b, ratio: 0.25);

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(0.75, split.Ratios[0], 6);
        Assert.Equal(0.25, split.Ratios[1], 6);
    }

    // ---------------- ratios ----------------

    [Fact]
    public void Ratios_always_sum_to_one()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.Split(a.Id, SplitDirection.Columns, b, 0.3);
        tree.Split(b.Id, SplitDirection.Columns, c, 0.7);

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(1.0, split.Ratios.Sum(), 6);
    }

    [Fact]
    public void Resize_moves_share_between_neighbours_and_conserves_the_total()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Columns, b);

        Assert.True(tree.Resize(a.Id, 0.2));

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.Equal(0.7, split.Ratios[0], 6);
        Assert.Equal(0.3, split.Ratios[1], 6);
        Assert.Equal(1.0, split.Ratios.Sum(), 6);
    }

    [Fact]
    public void Resize_cannot_squeeze_a_pane_out_of_existence()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Columns, b);

        tree.Resize(a.Id, 10.0);   // absurd drag

        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.True(split.Ratios[1] >= SplitNode.MinRatio,
            $"neighbour collapsed to {split.Ratios[1]}, below the {SplitNode.MinRatio} floor");
        Assert.Equal(1.0, split.Ratios.Sum(), 6);
    }

    [Fact]
    public void Resize_returns_false_for_a_lone_root_pane()
    {
        var tree = TreeOf(out var a);
        Assert.False(tree.Resize(a.Id, 0.1));
    }

    [Fact]
    public void Equalize_gives_every_sibling_the_same_share()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.Split(a.Id, SplitDirection.Columns, b, 0.1);
        tree.Split(b.Id, SplitDirection.Columns, c, 0.8);

        Assert.True(tree.EqualizeSiblings(a.Id));
        var split = Assert.IsType<SplitNode>(tree.Root);
        Assert.All(split.Ratios, r => Assert.Equal(1.0 / 3, r, 6));
    }

    // ---------------- closing and canonical form ----------------

    [Fact]
    public void Closing_one_of_two_panes_collapses_the_split_away()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Columns, b);

        Assert.True(tree.Close(b.Id));

        var leaf = Assert.IsType<LeafNode>(tree.Root);
        Assert.Equal(a.Id, leaf.Pane.Id);
        Assert.Equal(a.Id, tree.Focused);
    }

    [Fact]
    public void Closing_the_last_pane_is_refused()
    {
        var tree = TreeOf(out var a);
        Assert.False(tree.Close(a.Id));
        Assert.Single(tree.Panes);
    }

    [Fact]
    public void Closing_the_focused_pane_moves_focus_to_a_neighbour()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.Split(b.Id, SplitDirection.Columns, c);

        tree.Focus(b.Id);
        tree.Close(b.Id);

        Assert.NotEqual(b.Id, tree.Focused);
        Assert.Contains(tree.Focused, tree.Panes.Select(p => p.Id));
    }

    [Fact]
    public void Closing_never_leaves_a_container_with_one_child()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        var d = P("d");
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.Split(b.Id, SplitDirection.Rows, c);
        tree.AddTab(c.Id, d);

        tree.Close(d.Id);
        tree.Close(c.Id);

        AssertCanonical(tree.Root);
        Assert.Equal(2, tree.Panes.Count());
    }

    private static void AssertCanonical(LayoutNode node)
    {
        switch (node)
        {
            case SplitNode s:
                Assert.True(s.Children.Count >= 2, "a split retained fewer than two children");
                Assert.Equal(s.Children.Count, s.Ratios.Count);
                foreach (var c in s.Children) { Assert.Same(s, c.Parent); AssertCanonical(c); }
                break;
            case StackNode st:
                Assert.True(st.Children.Count >= 2, "a stack retained fewer than two children");
                Assert.InRange(st.ActiveIndex, 0, st.Children.Count - 1);
                foreach (var c in st.Children) { Assert.Same(st, c.Parent); AssertCanonical(c); }
                break;
        }
    }

    // ---------------- tabs ----------------

    [Fact]
    public void AddTab_wraps_a_leaf_in_a_stack_and_activates_the_new_tab()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.AddTab(a.Id, b);

        var stack = Assert.IsType<StackNode>(tree.Root);
        Assert.Equal(2, stack.Children.Count);
        Assert.Equal(1, stack.ActiveIndex);
        Assert.Equal(b.Id, tree.Focused);
    }

    [Fact]
    public void Only_the_active_tab_is_visible()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.AddTab(a.Id, b);

        var visible = tree.VisiblePanes().Select(p => p.Id).ToArray();
        Assert.Equal([b.Id], visible);
        Assert.Equal(2, tree.Panes.Count());
    }

    [Fact]
    public void CycleTab_wraps_around_and_follows_focus()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        var c = P("c");
        tree.AddTab(a.Id, b);
        tree.AddTab(b.Id, c);       // stack: a, b, c with c active

        Assert.True(tree.CycleTab(1));
        Assert.Equal(a.Id, tree.Focused);   // wrapped past the end

        Assert.True(tree.CycleTab(-1));
        Assert.Equal(c.Id, tree.Focused);   // wrapped back
    }

    [Fact]
    public void Focusing_a_pane_inside_an_inactive_tab_reveals_it()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.AddTab(a.Id, b);
        Assert.DoesNotContain(a.Id, tree.VisiblePanes().Select(p => p.Id));

        Assert.True(tree.Focus(a.Id));

        Assert.Contains(a.Id, tree.VisiblePanes().Select(p => p.Id));
    }

    // ---------------- invariants that must hold at construction ----------------

    [Fact]
    public void A_split_cannot_be_built_with_a_single_child()
    {
        Assert.Throws<ArgumentException>(() =>
            new SplitNode(SplitDirection.Columns, [new LeafNode(P("only"))]));
    }

    [Fact]
    public void A_split_rejects_a_mismatched_ratio_count()
    {
        Assert.Throws<ArgumentException>(() =>
            new SplitNode(SplitDirection.Columns,
                [new LeafNode(P("a")), new LeafNode(P("b"))],
                [0.5, 0.3, 0.2]));
    }

    [Fact]
    public void Clone_is_deep_and_rewires_parents()
    {
        var tree = TreeOf(out var a);
        var b = P("b");
        tree.Split(a.Id, SplitDirection.Rows, b);

        var copy = tree.Clone();
        Assert.NotSame(tree.Root, copy.Root);
        AssertCanonical(copy.Root);
        Assert.Equal(
            tree.Panes.Select(p => p.Id).OrderBy(x => x.Value),
            copy.Panes.Select(p => p.Id).OrderBy(x => x.Value));
    }
}
