using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

/// <summary>
/// A tab whose pane is waiting says so on the tab itself.
///
/// The notification tells you something happened; this tells you <em>where</em>, which is the part
/// that matters with six Claude Code sessions open and one of them asking.
/// </summary>
public sealed class WaitingTabTests
{
    private static TabStripCommands NoCommands() => new(
        _ => { }, _ => { }, _ => { }, _ => { }, (_, _, _) => { }, _ => { }, (_, _) => { }, _ => { });

    /// <summary>The three panes a strip is built from: a plain tab, and a tab holding a split.</summary>
    private sealed record Panes(Pane Plain, Pane Visible, Pane Buried)
    {
        public static Panes Create() =>
            new(Pane.Terminal("plain"), Pane.Terminal("visible"), Pane.Terminal("buried"));
    }

    /// <summary>A strip of two tabs, the second of which holds a split of two panes.</summary>
    private static Control Build(Panes panes, Func<PaneId, string?>? waiting)
    {
        var split = new SplitNode(
            SplitDirection.Columns, [new LeafNode(panes.Visible), new LeafNode(panes.Buried)]);
        var stack = new StackNode([new LeafNode(panes.Plain), split], activeIndex: 0);

        var strip = TabStripView.Build(
            new WinMux.Core.Layout.TabStrip(stack, new WinMux.Core.Layout.Rect(0, 0, 480, 32), TabStripPlacement.Top),
            panes.Plain.Id,
            NoCommands(),
            waiting);

        var window = new Window { Width = 480, Height = 64, Content = strip };
        window.Show();
        window.Measure(new Avalonia.Size(480, 64));
        window.Arrange(new Avalonia.Rect(0, 0, 480, 64));
        Dispatcher.UIThread.RunJobs();
        return strip;
    }

    /// <summary>The dot: a small round accent border, which nothing else in a tab is.</summary>
    private static IReadOnlyList<Border> Dots(Control strip) =>
    [
        .. strip.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Width is > 0 and <= 10 && b.Height is > 0 and <= 10)
    ];

    [Fact]
    public Task No_dot_when_nothing_is_waiting() => Headless.RunSync(() =>
        Assert.Empty(Dots(Build(Panes.Create(), waiting: null))));

    [Fact]
    public Task A_waiting_pane_puts_a_dot_on_its_tab() => Headless.RunSync(() =>
    {
        var panes = Panes.Create();

        var strip = Build(panes, id => id == panes.Plain.Id ? "Claude needs your permission" : null);

        var dots = Dots(strip);
        Assert.Single(dots);
        Assert.Equal("Claude needs your permission", ToolTip.GetTip(dots[0]));
    });

    [Fact]
    public Task A_pane_buried_in_a_split_marks_the_tab_that_holds_it() => Headless.RunSync(() =>
    {
        // The tab shows the first pane's title, and the one that rang is the other one. Marking
        // only the pane whose title is displayed would leave the ringing pane unfindable.
        var panes = Panes.Create();

        var strip = Build(panes, id => id == panes.Buried.Id ? "over here" : null);

        Assert.Single(Dots(strip));
    });
}
