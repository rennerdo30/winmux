using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public sealed class TabStripWidthTests
{
    private static StackNode Stack(int? width, TabStripPlacement edge = TabStripPlacement.Left) =>
        new([new LeafNode(Pane.Terminal("a")), new LeafNode(Pane.Terminal("b"))], tabStrip: edge)
        { TabStripWidth = width };

    [Theory]
    [InlineData(TabStripPlacement.Left)]
    [InlineData(TabStripPlacement.Right)]
    public void IndependentSiblingWidthsReserveTheirOwnSpace(TabStripPlacement edge)
    {
        var a = Stack(120, edge);
        var b = Stack(300, edge);
        var root = new SplitNode(SplitDirection.Columns, [a, b]);
        var layout = Layouter.Arrange(root, new Rect(0, 0, 1600, 600));
        Assert.Equal(120, layout.TabStrips.Single(strip => strip.Stack == a).Rect.Width);
        Assert.Equal(300, layout.TabStrips.Single(strip => strip.Stack == b).Rect.Width);
        Assert.Equal(797 - 120, layout[a.ActiveLeaf().Pane.Id].Width);
        Assert.Equal(797 - 300, layout[b.ActiveLeaf().Pane.Id].Width);
    }

    [Fact]
    public void NarrowWindowPreservesContentAndRemembersRequestedWidth()
    {
        var stack = Stack(400);
        var layout = Layouter.Arrange(stack, new Rect(0, 0, 300, 600));
        Assert.Equal(292, Assert.Single(layout.TabStrips).Rect.Width);
        Assert.Equal(8, layout[stack.ActiveLeaf().Pane.Id].Width);
        Assert.Equal(400, stack.TabStripWidth);
        Assert.Equal(400, Assert.Single(Layouter.Arrange(stack, new Rect(0, 0, 800, 600)).TabStrips).Rect.Width);
        Assert.Empty(Layouter.Arrange(stack, new Rect(0, 0, 80, 600)).TabStrips);
    }

    [Fact]
    public void CustomWidthDoesNotAffectHorizontalOrCharacterGridStrips()
    {
        var stack = Stack(300, TabStripPlacement.Top);
        Assert.Equal(40, Assert.Single(Layouter.Arrange(stack, new Rect(0, 0, 800, 600)).TabStrips).Rect.Height);
        stack.TabStrip = TabStripPlacement.Left;
        Assert.Empty(Layouter.Arrange(stack, new Rect(0, 0, 80, 24), LayoutMetrics.CharacterGrid).TabStrips);
    }

    [Fact]
    public void WidthClampsAndCloneRetainsIt()
    {
        var stack = Stack(2);
        Assert.Equal(StackNode.MinimumTabStripWidth, stack.TabStripWidth);
        stack.TabStripWidth = int.MaxValue;
        Assert.Equal(StackNode.MaximumTabStripWidth, ((StackNode)stack.Clone()).TabStripWidth);
    }

    [Fact]
    public void SessionRoundTripRetainsIndependentWidthsAndOldFileDefaults()
    {
        var a = Stack(120);
        var b = Stack(300, TabStripPlacement.Right);
        var tree = new LayoutTree(new SplitNode(SplitDirection.Columns, [a, b]), a.ActiveLeaf().Pane.Id);
        var snapshot = new SessionSnapshot { Windows = [SessionMapper.ToSnapshot(tree)] };
        var text = SessionFile.Serialize(snapshot);
        var loaded = SessionMapper.FromSnapshot(TomlSessionReader.Read(text).Windows[0]);
        var children = ((SplitNode)loaded.Root).Children;
        Assert.Equal(120, ((StackNode)children[0]).TabStripWidth);
        Assert.Equal(300, ((StackNode)children[1]).TabStripWidth);
        var oldText = string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith("tab_width")));
        var old = SessionMapper.FromSnapshot(TomlSessionReader.Read(oldText).Windows[0]);
        Assert.All(((SplitNode)old.Root).Children, child => Assert.Null(((StackNode)child).TabStripWidth));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99999999999")]
    [InlineData("'wide'")]
    public void InvalidPersistedWidthIsReported(string invalid)
    {
        var stack = Stack(120);
        var tree = new LayoutTree(stack, stack.ActiveLeaf().Pane.Id);
        var text = SessionFile.Serialize(new SessionSnapshot { Windows = [SessionMapper.ToSnapshot(tree)] });
        var line = text.Split('\n').Single(row => row.TrimStart().StartsWith("tab_width"));
        var broken = text.Replace(line, "tab_width = " + invalid);
        Assert.Contains("tab_width", Assert.Throws<SessionFormatException>(() => TomlSessionReader.Read(broken)).Message);
    }
}
