using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public sealed class WrappedTabNameTests
{
    [Theory]
    [InlineData("nested")]
    [InlineData("tabs")]
    [InlineData("split")]
    public void A_custom_tab_name_becomes_the_container_name_when_wrapped(string operation)
    {
        var first = Pane.Terminal("My workspace");
        first.Restore = first.Restore with { Title = first.Title, TitleIsCustom = true };
        var tree = new LayoutTree(first);
        Wrap(tree, first, operation);

        Assert.Equal("My workspace", tree.Root.Title);
        first.Title = "inner renamed";
        first.Restore = first.Restore with { Title = first.Title };
        Assert.Equal("My workspace", tree.Root.Title);
        Assert.Equal("inner renamed", tree.Root.Leaves().First().Pane.Title);

        var snapshot = new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree)],
        };
        var restored = SessionMapper.FromSnapshot(SessionFile.Deserialize(SessionFile.Serialize(snapshot)).Windows[0]);
        Assert.Equal("My workspace", restored.Root.Title);
        Assert.Equal("inner renamed", restored.Root.Leaves().First().Pane.Title);
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("tabs")]
    [InlineData("split")]
    public void An_automatic_terminal_title_does_not_become_an_explicit_container_name(string operation)
    {
        var first = Pane.Terminal("cmd");
        var tree = new LayoutTree(first);
        Wrap(tree, first, operation);
        Assert.Equal(string.Empty, tree.Root.Title);
    }

    [Fact]
    public void Nesting_a_collapsed_named_group_preserves_its_label_without_sharing_the_inner_label()
    {
        var first = Pane.Terminal("shell");
        var second = Pane.Terminal("temporary");
        var outside = Pane.Terminal("outside");
        var group = new StackNode([new LeafNode(first), new LeafNode(second)]) { Title = "Workspace" };
        var outer = new StackNode([group, new LeafNode(outside)]);
        var tree = new LayoutTree(outer, first.Id);
        tree.Close(second.Id);
        Assert.Equal("Workspace", tree.Find(first.Id)!.Title);

        tree.AddTabGroup(first.Id, Pane.Terminal("new inner"));
        var nested = Assert.IsType<StackNode>(outer.Children[0]);
        Assert.Equal("Workspace", nested.Title);
        Assert.Equal(string.Empty, tree.Find(first.Id)!.Title);
        first.Title = "renamed inner";
        Assert.Equal("Workspace", nested.Title);
    }

    private static void Wrap(LayoutTree tree, Pane first, string operation)
    {
        var incoming = Pane.Terminal("new");
        switch (operation)
        {
            case "nested": tree.AddTabGroup(first.Id, incoming); break;
            case "tabs": tree.AddTab(first.Id, incoming); break;
            case "split": tree.Split(first.Id, SplitDirection.Columns, incoming); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}
