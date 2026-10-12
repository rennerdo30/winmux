using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Actions;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class CaptionTabMenuTests
{
    [Fact]
    public Task Overflow_menu_keeps_toolbar_actions_reachable() => Headless.RunSync(() =>
    {
        var pane = Pane.Terminal("first");
        var stack = new StackNode([new LeafNode(pane), new LeafNode(Pane.Terminal("second"))]);
        var dispatched = new List<string>();
        var commands = new TabStripCommands(
            Activate: _ => { }, Rename: _ => { }, CloseTab: _ => Task.CompletedTask, TogglePin: _ => { },
            MoveTabToGroup: (_, _, _) => { }, AddTab: _ => { }, MoveStrip: (_, _) => { },
            MoveTab: _ => { }, Dispatch: dispatched.Add);
        var view = TabStripView.Build(new TabStrip(stack, new Rect(0, 0, 800, 40),
            TabStripPlacement.Top), pane.Id, commands);
        var window = new Window { Width = 800, Height = 80, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var more = view.GetVisualDescendants().OfType<Button>()
            .First(button => button.IsVisible && Equals(ToolTip.GetTip(button), "More tab actions"));
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var menu = more.ContextMenu!;
        var items = menu.ItemsSource!.OfType<MenuItem>().ToArray();
        Assert.Contains(items, item => Equals(item.Header, "Split right"));
        Assert.Contains(items, item => Equals(item.Header, "Settings"));
        items.Single(item => Equals(item.Header, "Split right"))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Contains(ShellActionNames.SplitColumns, dispatched);
        window.Close();
    });
}
