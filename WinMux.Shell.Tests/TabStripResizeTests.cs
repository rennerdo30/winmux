using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

public sealed class TabStripResizeTests
{
    [Theory]
    [InlineData(TabStripPlacement.Left, 250)]
    [InlineData(TabStripPlacement.Right, 190)]
    public Task DraggingContentEdgeRetainsScrollerAndUpdatesOwningStack(
        TabStripPlacement placement, int expectedWidth) => Headless.RunSync(() =>
    {
        var panes = Enumerable.Range(0, 24).Select(i => Pane.Terminal("Terminal " + i)).ToArray();
        var stack = new StackNode(panes.Select(pane => new LeafNode(pane)), tabStrip: placement);
        var geometry = new WinMux.Core.Layout.TabStrip(stack,
            new WinMux.Core.Layout.Rect(0, 0, 220, 350), placement);
        Control? strip = null;
        var resizeCalls = 0;
        var refreshes = new List<bool>();
        var commands = new TabStripCommands(
            Activate: _ => { }, Rename: _ => { }, CloseTab: _ => { }, TogglePin: _ => { },
            MoveTabToGroup: (_, _, _) => { }, AddTab: _ => { }, MoveStrip: (_, _) => { },
            MoveTab: _ => { }, ResizeStrip: (owner, width) =>
            {
                Assert.Same(stack, owner);
                resizeCalls++;
                owner.TabStripWidth = width;
                strip!.Width = width;
                Canvas.SetLeft(strip, placement == TabStripPlacement.Right ? 800 - width : 0);
                refreshes.Add(TabStripView.TryRefresh(strip, geometry with
                { Rect = geometry.Rect with { Width = width } }, panes[0].Id));
            });
        strip = TabStripView.Build(geometry, panes[0].Id, commands);
        strip.Width = 220;
        strip.Height = 350;
        Canvas.SetLeft(strip, placement == TabStripPlacement.Right ? 580 : 0);
        var canvas = new Canvas { Children = { strip } };
        var window = new Window { Width = 800, Height = 400, Content = canvas };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var scroller = strip.GetVisualDescendants().OfType<ScrollViewer>().Single();
        scroller.Offset = new Vector(0, 90);
        Dispatcher.UIThread.RunJobs();
        var offset = scroller.Offset;
        Assert.True(offset.Y > 0, "Control case: enough terminal tabs must create scrollable content.");
        var handle = strip.GetVisualDescendants().OfType<Border>()
            .Single(border => Equals(ToolTip.GetTip(border), "Drag to resize this tab strip"));
        var start = handle.TranslatePoint(new Point(3, 150), window)!.Value;
        var retained = strip;
        window.MouseDown(start, MouseButton.Left);
        // Move beyond the original handle's hit target; pointer capture must keep resizing.
        window.MouseMove(start + new Vector(30, 0), RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(start + new Vector(30, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(resizeCalls > 0, "Pointer input must reach the resize callback.");
        Assert.Equal(expectedWidth, stack.TabStripWidth);
        Assert.Equal((double)expectedWidth, strip.Width);
        Assert.All(refreshes, Assert.True);
        Assert.Same(retained, Assert.Single(canvas.Children));
        Assert.Same(scroller, strip.GetVisualDescendants().OfType<ScrollViewer>().Single());
        Assert.Equal(offset, scroller.Offset);
        // Releasing must end capture; plain pointer motion must not alter the requested width.
        window.MouseMove(start + new Vector(60, 0));
        Assert.Equal(expectedWidth, stack.TabStripWidth);
        window.Close();
    });

    [Theory]
    [InlineData(TabStripPlacement.Left, 30, 250)]
    [InlineData(TabStripPlacement.Right, 30, 190)]
    [InlineData(TabStripPlacement.Left, -1000, 96)]
    [InlineData(TabStripPlacement.Right, -1000, 480)]
    public void ContentEdgeDragResizesInCorrectDirectionAndClamps(TabStripPlacement placement, double delta, int expected)
    {
        Assert.Equal(expected, TabStripView.WidthAfterDrag(220, delta, placement));
    }
}
