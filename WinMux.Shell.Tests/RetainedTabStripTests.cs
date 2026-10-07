using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class RetainedTabStripTests
{
    private static TabStripCommands Commands(Action<PaneId>? activate = null, Action<LayoutNode>? rename = null) =>
        new(activate ?? (_ => { }), rename ?? (_ => { }), _ => { }, _ => { },
            (_, _, _) => { }, _ => { }, (_, _) => { }, _ => { });

    private static TabStrip Strip(StackNode stack) => new(stack, new Rect(0, 0, 480, 40), TabStripPlacement.Top);

    private static Window Show(Control content)
    {
        var window = new Window { Width = 480, Height = 80, Content = content };
        window.Show();
        window.Measure(new Avalonia.Size(480, 80));
        window.Arrange(new Avalonia.Rect(0, 0, 480, 80));
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button[] Tabs(Control strip) => strip.GetVisualDescendants().OfType<Button>()
        .Where(b => b.Classes.Contains(Theme.Tab)).ToArray();

    [Fact]
    public Task Repeated_title_updates_preserve_the_open_menu_and_its_actions() => Headless.RunSync(() =>
    {
        var pane = Pane.Terminal("before");
        var stack = new StackNode([new LeafNode(pane), new LeafNode(Pane.Terminal("other"))]);
        LayoutNode? renamed = null;
        var view = TabStripView.Build(Strip(stack), pane.Id, Commands(rename: node => renamed = node));
        using var window = new WindowScope(Show(view));
        var tab = Tabs(view)[0];
        tab.ContextMenu!.Open(tab);
        for (var i = 0; i < 100; i++)
        {
            pane.Title = $"output {i}";
            Assert.True(TabStripView.TryRefresh(view, Strip(stack), pane.Id));
        }
        Assert.Same(tab, Tabs(view)[0]);
        Assert.True(tab.ContextMenu.IsOpen);
        Assert.Contains(tab.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == "output 99");
        tab.ContextMenu.Items.OfType<MenuItem>().First()
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Same(stack.Children[0], renamed);
        tab.ContextMenu.Close();
    });

    [Fact]
    public Task Clicking_the_outer_strip_returns_to_the_last_inner_tab() => Headless.RunSync(() =>
    {
        var alone = Pane.Terminal("alone");
        var first = Pane.Terminal("first");
        var last = Pane.Terminal("last");
        var inner = new StackNode([new LeafNode(first), new LeafNode(last)]);
        var outer = new StackNode([new LeafNode(alone), inner]);
        var tree = new LayoutTree(outer, alone.Id);
        var view = TabStripView.Build(Strip(outer), tree.Focused, Commands(id => tree.Focus(id)));
        using var window = new WindowScope(Show(view));
        var tabs = Tabs(view);
        tree.Focus(last.Id);
        Assert.True(TabStripView.TryRefresh(view, Strip(outer), tree.Focused));
        tabs[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(alone.Id, tree.Focused);
        Assert.True(TabStripView.TryRefresh(view, Strip(outer), tree.Focused));
        tabs[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(last.Id, tree.Focused);
        Assert.Equal(1, inner.ActiveIndex);
        Assert.True(TabStripView.TryRefresh(view, Strip(outer), tree.Focused));
        Assert.Contains(Theme.ActiveTab, tabs[1].Classes);
        Assert.DoesNotContain(Theme.ActiveTab, tabs[0].Classes);
    });

    [Fact]
    public Task Pinning_or_changing_the_tree_requires_new_controls() => Headless.RunSync(() =>
    {
        var pane = Pane.Terminal("pane");
        var other = Pane.Terminal("other");
        var tree = new LayoutTree(pane);
        tree.AddTab(pane.Id, other);
        var stack = (StackNode)tree.Root;
        var view = TabStripView.Build(Strip(stack), tree.Focused, Commands());
        pane.IsPinned = true;
        Assert.False(TabStripView.TryRefresh(view, Strip(stack), tree.Focused));
        pane.IsPinned = false;
        tree.AddTab(other.Id, Pane.Terminal("third"));
        Assert.False(TabStripView.TryRefresh(view, Strip(stack), tree.Focused));
    });

    private sealed class WindowScope(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
