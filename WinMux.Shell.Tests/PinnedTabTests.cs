using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Chrome;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a pinned tab offers, which is the whole point of pinning: the control that closes it is
/// not there any more.
///
/// A close button that refused would be worse than none — the tab would still say it can be
/// closed, and the refusal would read as a bug rather than as the setting the user chose.
/// </summary>
public sealed class PinnedTabTests
{
    private sealed record Recorder(List<PaneId> Closed, List<PaneId> Pinned)
    {
        public TabStripCommands Commands => new(
            Activate: _ => { },
            Rename: _ => { },
            CloseTab: Closed.Add,
            TogglePin: Pinned.Add,
            MoveTabToGroup: (_, _, _) => { },
            AddTab: _ => { },
            MoveStrip: (_, _) => { },
            MoveTab: _ => { });
    }

    private static (Control Strip, Pane First, Recorder Log) BuildStrip(bool pinFirst)
    {
        var first = Pane.Terminal("build");
        var second = Pane.Terminal("scratch");
        var tree = new LayoutTree(first);
        tree.AddTab(first.Id, second);
        first.IsPinned = pinFirst;

        // Tabbing a lone pane makes the root the stack.
        var stack = (StackNode)tree.Root;
        var log = new Recorder([], []);
        var strip = TabStripView.Build(
            new WinMux.Core.Layout.TabStrip(stack, new WinMux.Core.Layout.Rect(0, 0, 400, 32), TabStripPlacement.Top), first.Id, log.Commands);

        // Shown in a window rather than measured by hand: the strip's own controls are templated,
        // and a template is only applied once the control is attached to a root. Without this the
        // buttons exist in code and in no visual tree, which is indistinguishable from a tab strip
        // that draws no buttons at all.
        var window = new Window { Width = 400, Height = 64, Content = strip };
        window.Show();
        window.Measure(new Size(400, 64));
        window.Arrange(new Avalonia.Rect(0, 0, 400, 64));
        Dispatcher.UIThread.RunJobs();
        return (strip, first, log);
    }

    private static Button CornerOf(Control strip, string tabTitle) => strip
        .GetVisualDescendants()
        .OfType<Button>()
        .Where(b => b.Classes.Contains(Theme.CloseButton) || b.Classes.Contains(Theme.PinButton))
        .ElementAt(tabTitle == "build" ? 0 : 1);

    [Fact]
    public Task An_ordinary_tab_closes_from_its_corner() => Headless.RunSync(() =>
    {
        var (strip, first, log) = BuildStrip(pinFirst: false);

        var corner = CornerOf(strip, "build");
        Assert.Contains(Theme.CloseButton, corner.Classes);

        corner.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal([first.Id], log.Closed);
        Assert.Empty(log.Pinned);
    });

    [Fact]
    public Task A_pinned_tab_has_no_close_button_and_unpins_instead() => Headless.RunSync(() =>
    {
        var (strip, first, log) = BuildStrip(pinFirst: true);

        var corner = CornerOf(strip, "build");
        Assert.Contains(Theme.PinButton, corner.Classes);
        Assert.DoesNotContain(Theme.CloseButton, corner.Classes);

        corner.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Empty(log.Closed);
        Assert.Equal([first.Id], log.Pinned);
    });

    [Fact]
    public Task Pinning_only_affects_the_tab_that_was_pinned() => Headless.RunSync(() =>
    {
        var (strip, _, log) = BuildStrip(pinFirst: true);

        var other = CornerOf(strip, "scratch");
        other.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Single(log.Closed);
        Assert.Empty(log.Pinned);
    });
}
