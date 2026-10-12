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
        new(activate ?? (_ => { }), rename ?? (_ => { }), _ => Task.CompletedTask, _ => { },
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Horizontal_tabs_are_compact_with_accessible_pin_and_close_targets(bool caption) =>
        Headless.RunSync(() =>
        {
            var pinned = Pane.Terminal("Pinned workspace");
            pinned.IsPinned = true;
            var regular = Pane.Terminal("Regular workspace");
            var stack = new StackNode([new LeafNode(pinned), new LeafNode(regular)]);
            var view = TabStripView.Build(Strip(stack), regular.Id, Commands(), isCaption: caption);
            using var window = new WindowScope(Show(view));
            foreach (var tab in Tabs(view))
            {
                Assert.Equal(TabStripView.CompactTabHeight, tab.Bounds.Height);
                Assert.InRange(tab.Bounds.Width, TabStripView.CompactTabMinWidth,
                    TabStripView.CompactTabMaxWidth);
                var title = tab.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text?.Contains("workspace") == true);
                Assert.Equal(TabStripView.CompactTabTitleSize, title.FontSize);
                var action = tab.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Classes.Contains(Theme.PinButton) ||
                                      button.Classes.Contains(Theme.CloseButton));
                Assert.Equal(24, action.Width);
                Assert.Equal(24, action.Height);
                if (action.IsVisible)
                {
                    Assert.Equal(24, action.Bounds.Width);
                    Assert.Equal(24, action.Bounds.Height);
                }
                Assert.True(action.Width * 1.5 >= 36);
            }
        });

    [Fact]
    public Task Cancelled_animated_close_restores_the_retained_tab() => Headless.RunAsync(async () =>
    {
        var first = Pane.Terminal("keep");
        var stack = new StackNode([new LeafNode(first), new LeafNode(Pane.Terminal("other"))]);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolveClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = Commands() with
        {
            CloseTab = _ => { closeStarted.TrySetResult(); return resolveClose.Task; },
        };
        var view = TabStripView.Build(Strip(stack), first.Id, commands, closeMotionOverride: true);
        using var window = new WindowScope(Show(view));
        var tab = Tabs(view)[0];
        var oldWidth = tab.Width;
        var close = tab.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains(Theme.CloseButton));
        try
        {
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(tab.IsHitTestVisible);
            resolveClose.SetResult(); // the confirmation was cancelled; the tree is unchanged
            await Task.Delay(40);
            Dispatcher.UIThread.RunJobs();
            Assert.True(tab.IsHitTestVisible);
            Assert.Equal(oldWidth, tab.Width);
            Assert.Equal(1, tab.Opacity);
            Assert.True(TabStripView.TryRefresh(view, Strip(stack), first.Id));
        }
        finally { resolveClose.TrySetResult(); }
    });

    [Fact]
    public Task Overflow_tabs_keep_navigation_visible_and_scrollable() => Headless.RunSync(() =>
    {
        var panes = Enumerable.Range(0, 12).Select(i => Pane.Terminal($"Workspace {i}")).ToArray();
        var stack = new StackNode(panes.Select(pane => new LeafNode(pane)));
        var view = TabStripView.Build(Strip(stack), panes[0].Id, Commands());
        using var window = new WindowScope(Show(view));
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single();
        var left = view.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(ToolTip.GetTip(button), "Scroll tabs left"));
        var right = view.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(ToolTip.GetTip(button), "Scroll tabs right"));
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        Assert.True(right.IsVisible);
        Assert.False(left.IsVisible);
        right.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(scroller.Offset.X > 0);
        Assert.True(left.IsVisible);
        Assert.Equal(12, Tabs(view).Length);
    });

    [Fact]
    public Task Tabs_shrink_together_before_the_strip_scrolls() => Headless.RunSync(() =>
    {
        var panes = Enumerable.Range(0, 6).Select(i => Pane.Terminal($"Workspace {i:D2}")).ToArray();
        var stack = new StackNode(panes.Select(pane => new LeafNode(pane)));
        var view = TabStripView.Build(new TabStrip(stack, new Rect(0, 0, 800, 40),
            TabStripPlacement.Top), panes[0].Id, Commands());
        var window = new Window { Width = 800, Height = 80, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var widths = Tabs(view).Select(tab => tab.Bounds.Width).ToArray();
            Assert.All(widths, width => Assert.InRange(width, 96, 130));
            Assert.True(widths.Max() - widths.Min() < 1, "Tabs should share the available width.");
            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single();
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 1,
                $"extent={scroller.Extent.Width}, viewport={scroller.Viewport.Width}, widths={string.Join(',', widths)}");
        }
        finally { window.Close(); }
    });

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
