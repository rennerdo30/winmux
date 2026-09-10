using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Tests;

public class ArrangementTests
{
    private static Pane P(string t) => Pane.Terminal(t);
    private const int D = Layouter.DividerThickness;

    [Fact]
    public void A_single_pane_fills_the_bounds()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 800, 600) };

        var arranged = tree.Arrange();

        Assert.Equal(new Rect(0, 0, 800, 600), arranged[a.Id]);
        Assert.Empty(arranged.Dividers);
    }

    [Fact]
    public void An_even_column_split_shares_the_space_minus_the_divider()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 100, 50) };
        tree.Split(a.Id, SplitDirection.Columns, b);

        var arranged = tree.Arrange();
        var ra = arranged[a.Id];
        var rb = arranged[b.Id];

        Assert.Equal(0, ra.X);
        Assert.Equal(ra.Right + D, rb.X);
        Assert.Equal(100, rb.Right);
        Assert.Equal(100 - D, ra.Width + rb.Width);
        Assert.Equal(50, ra.Height);
        Assert.Equal(50, rb.Height);
    }

    [Fact]
    public void Rows_stack_top_to_bottom()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 100, 100) };
        tree.Split(a.Id, SplitDirection.Rows, b);

        var arranged = tree.Arrange();
        Assert.Equal(arranged[a.Id].Bottom + D, arranged[b.Id].Y);
        Assert.Equal(100, arranged[a.Id].Width);
        Assert.Equal(100, arranged[b.Id].Width);
    }

    /// <summary>
    /// Per-child rounding loses or gains pixels as the child count grows, and the drift shows up
    /// as a divider that wobbles while you drag. Cumulative rounding is what prevents it.
    /// </summary>
    [Theory]
    [InlineData(2, 1000)]
    [InlineData(3, 1000)]
    [InlineData(5, 997)]
    [InlineData(7, 1023)]
    [InlineData(11, 1279)]
    public void No_pixel_is_ever_lost_or_invented(int paneCount, int width)
    {
        var first = P("p0");
        var tree = new LayoutTree(first) { Bounds = new Rect(0, 0, width, 400) };
        var last = first.Id;
        for (int i = 1; i < paneCount; i++)
        {
            var pane = P("p" + i);
            last = tree.Split(last, SplitDirection.Columns, pane);
        }

        var arranged = tree.Arrange();
        var rects = arranged.PaneRects.Values.OrderBy(r => r.X).ToArray();

        Assert.Equal(paneCount, rects.Length);
        var totalPanes = rects.Sum(r => r.Width);
        var totalDividers = (paneCount - 1) * D;
        Assert.Equal(width, totalPanes + totalDividers);

        // and they must tile without gaps or overlaps
        for (int i = 1; i < rects.Length; i++)
            Assert.Equal(rects[i - 1].Right + D, rects[i].X);
        Assert.Equal(0, rects[0].X);
        Assert.Equal(width, rects[^1].Right);
    }

    [Fact]
    public void Ratios_are_honoured_proportionally()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 406, 100) };
        tree.Split(a.Id, SplitDirection.Columns, b, ratio: 0.25);

        var arranged = tree.Arrange();
        // 406 - 6 divider = 400 available; 75/25 split
        Assert.Equal(300, arranged[a.Id].Width);
        Assert.Equal(100, arranged[b.Id].Width);
    }

    [Fact]
    public void Panes_hidden_behind_an_inactive_tab_have_no_rectangle_at_all()
    {
        var a = P("a");
        var b = P("b");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 200, 200) };
        tree.AddTab(a.Id, b);

        var arranged = tree.Arrange();

        Assert.True(arranged.IsVisible(b.Id));
        Assert.False(arranged.IsVisible(a.Id));
        Assert.Equal(Rect.Empty, arranged[a.Id]);
        Assert.Equal(new Rect(0, 0, 200, 200), arranged[b.Id]);
    }

    [Fact]
    public void A_divider_is_reported_between_each_pair_of_siblings()
    {
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 600, 200) };
        var last = a.Id;
        for (int i = 0; i < 3; i++) last = tree.Split(last, SplitDirection.Columns, P("x" + i));

        var arranged = tree.Arrange();

        Assert.Equal(3, arranged.Dividers.Count);
        Assert.All(arranged.Dividers, d =>
        {
            Assert.Equal(D, d.Rect.Width);
            Assert.Equal(200, d.Rect.Height);
            Assert.Equal(SplitDirection.Columns, d.Direction);
        });
    }

    [Fact]
    public void Dragging_a_divider_changes_the_neighbouring_widths_only()
    {
        var a = P("a");
        var b = P("b");
        var c = P("c");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 612, 100) };
        tree.Split(a.Id, SplitDirection.Columns, b);
        tree.Split(b.Id, SplitDirection.Columns, c);

        var before = tree.Arrange();
        var widthC = before[c.Id].Width;

        var divider = before.Dividers[0];     // between a and b
        Assert.True(tree.ResizeDivider(divider, 0.1));

        var after = tree.Arrange();
        Assert.True(after[a.Id].Width > before[a.Id].Width);
        Assert.True(after[b.Id].Width < before[b.Id].Width);
        Assert.Equal(widthC, after[c.Id].Width);
    }

    [Fact]
    public void A_degenerate_bounds_does_not_throw_or_produce_negative_sizes()
    {
        // The shell can hand us a zero or tiny rect mid-resize; layout must survive it.
        var a = P("a");
        var tree = new LayoutTree(a) { Bounds = new Rect(0, 0, 4, 4) };
        var last = a.Id;
        for (int i = 0; i < 4; i++) last = tree.Split(last, SplitDirection.Columns, P("x" + i));

        var arranged = tree.Arrange();

        Assert.All(arranged.PaneRects.Values, r =>
        {
            Assert.True(r.Width >= 0, $"negative width {r.Width}");
            Assert.True(r.Height >= 0, $"negative height {r.Height}");
        });
    }
}
