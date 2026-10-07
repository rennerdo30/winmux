using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class WrappedTabLabelTests
{
    [Fact]
    public Task Renaming_the_first_nested_tab_keeps_the_named_outer_tab_label() => Headless.RunSync(() =>
    {
        var first = Pane.Terminal("Workspace");
        first.Restore = first.Restore with { Title = first.Title, TitleIsCustom = true };
        var tree = new LayoutTree(first);
        tree.AddTab(first.Id, Pane.Terminal("outside"));
        var outer = (StackNode)tree.Root;
        tree.AddTabGroup(first.Id, Pane.Terminal("inner two"));
        var inner = Assert.IsType<StackNode>(outer.Children[0]);
        LayoutNode? renamed = null;
        var commands = new TabStripCommands(_ => { }, node =>
        {
            renamed = node;
            var leaf = Assert.IsType<LeafNode>(node);
            leaf.Pane.Title = "Inner renamed";
            leaf.Pane.Restore = leaf.Pane.Restore with { Title = leaf.Pane.Title, TitleIsCustom = true };
        }, _ => { }, _ => { }, (_, _, _) => { }, _ => { }, (_, _) => { }, _ => { });
        var outerStrip = new WinMux.Core.Layout.TabStrip(outer, new Rect(0, 0, 480, 40), TabStripPlacement.Top);
        var innerStrip = new WinMux.Core.Layout.TabStrip(inner, new Rect(0, 0, 480, 40), TabStripPlacement.Top);
        var outerView = TabStripView.Build(outerStrip, tree.Focused, commands);
        var innerView = TabStripView.Build(innerStrip, tree.Focused, commands);
        var window = new Window
        {
            Width = 480, Height = 120,
            Content = new StackPanel { Children = { outerView, innerView } },
        };
        try
        {
            window.Show();
            window.Measure(new Avalonia.Size(480, 120));
            window.Arrange(new Avalonia.Rect(0, 0, 480, 120));
            Dispatcher.UIThread.RunJobs();
            var firstInnerTab = innerView.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains(Theme.Tab));
            firstInnerTab.ContextMenu!.Items.OfType<MenuItem>().First()
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(tree.Find(first.Id), renamed);
            Assert.True(TabStripView.TryRefresh(outerView, outerStrip, tree.Focused));
            Assert.True(TabStripView.TryRefresh(innerView, innerStrip, tree.Focused));
            var parentTab = outerView.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains(Theme.Tab));
            Assert.Contains(parentTab.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Workspace");
            Assert.Contains(firstInnerTab.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Inner renamed");
        }
        finally { window.Close(); }
    });
}
