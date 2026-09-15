using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.Panes;

namespace WinMux.Shell.Tests;

/// <summary>
/// Following the OS when it moves focus into a pane WinMux does not own.
///
/// CLAUDE.md section 6 says never to assume WinMux's focused pane and the OS's active window agree,
/// and until now they only agreed by luck: the shell learned about focus only when it caused it, so
/// clicking into an Explorer pane left the focused pane pointing at a terminal somewhere else. The
/// next keystroke, or the next "close pane", then acted on that terminal.
/// </summary>
public sealed class FocusReconciliationTests
{
    private sealed class FakePane(params WindowHandle[] windows) : IHostedWindowPane
    {
        public bool OwnsWindow(WindowHandle window) => Array.IndexOf(windows, window) >= 0;
    }

    private static WindowHandle Handle(int value) => WindowHandle.FromPlatformValue(value);

    private static ForegroundWindow Active(WindowHandle window, WindowHandle? root = null) =>
        new(window, root ?? window, ProcessId: 1234);

    [Fact]
    public void Focusing_a_panes_window_moves_the_focused_pane()
    {
        var explorer = PaneId.New();
        var terminal = PaneId.New();
        var panes = new[]
        {
            (terminal, (IHostedWindowPane)new FakePane(Handle(10))),
            (explorer, new FakePane(Handle(20))),
        };

        var result = FocusReconciliation.PaneFor(Active(Handle(20)), panes, current: terminal);

        Assert.Equal(explorer, result);
    }

    [Fact]
    public void An_embedded_application_is_recognised_by_its_host_window()
    {
        // Embed mode: the application is a child of the pane host, so the root window Windows
        // reports is the host's. A pane that only knew the child would never match.
        var pane = PaneId.New();
        var host = Handle(30);
        var child = Handle(31);
        var panes = new[] { (pane, (IHostedWindowPane)new FakePane(host)) };

        var result = FocusReconciliation.PaneFor(Active(child, root: host), panes, current: PaneId.New());

        Assert.Equal(pane, result);
    }

    [Fact]
    public void An_attached_application_is_recognised_by_its_own_window()
    {
        // Attach mode: the application window is top-level, so window and root are the same.
        var pane = PaneId.New();
        var window = Handle(40);
        var panes = new[] { (pane, (IHostedWindowPane)new FakePane(window)) };

        Assert.Equal(pane, FocusReconciliation.PaneFor(Active(window), panes, current: PaneId.New()));
    }

    [Fact]
    public void Focusing_the_pane_that_is_already_focused_changes_nothing()
    {
        // Otherwise every click inside a foreign pane costs a relayout for no change.
        var pane = PaneId.New();
        var panes = new[] { (pane, (IHostedWindowPane)new FakePane(Handle(50))) };

        Assert.Null(FocusReconciliation.PaneFor(Active(Handle(50)), panes, current: pane));
    }

    [Fact]
    public void A_window_belonging_to_no_pane_leaves_focus_alone()
    {
        // The user alt-tabbed to their browser. WinMux's focused pane is where its keys go when it
        // is focused again, so moving it because the user left would be exactly wrong.
        var terminal = PaneId.New();
        var panes = new[] { (terminal, (IHostedWindowPane)new FakePane(Handle(60))) };

        Assert.Null(FocusReconciliation.PaneFor(Active(Handle(999)), panes, current: terminal));
    }

    [Fact]
    public void With_no_hosted_panes_nothing_happens()
    {
        var current = PaneId.New();

        Assert.Null(FocusReconciliation.PaneFor(Active(Handle(70)), [], current));
    }

    [Fact]
    public void A_dead_handle_matches_nothing()
    {
        var pane = PaneId.New();
        var panes = new[] { (pane, (IHostedWindowPane)new FakePane(WindowHandle.None)) };

        Assert.Null(FocusReconciliation.PaneFor(Active(WindowHandle.None), panes, current: PaneId.New()));
    }
}
