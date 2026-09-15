using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.Panes;

namespace WinMux.Shell;

/// <summary>
/// Deciding whether the OS moving focus means WinMux should move its focused pane.
///
/// CLAUDE.md section 6: "Maintain WinMux's own focused-pane state and reconcile it with the OS;
/// never assume they agree." The shell used to learn about focus only when it caused it, so
/// clicking into an Explorer pane left the focused pane pointing at a terminal elsewhere — and the
/// next key, or the next "close pane", acted on that terminal.
///
/// Separated from the window because the rules are the whole of it and none of them is drawing.
/// </summary>
internal static class FocusReconciliation
{
    /// <summary>
    /// The pane that should take focus, or null to leave it alone.
    /// </summary>
    /// <param name="foreground">What Windows says just became active.</param>
    /// <param name="panes">The panes standing in for windows, with their ids.</param>
    /// <param name="current">The pane the shell currently believes is focused.</param>
    public static PaneId? PaneFor(
        ForegroundWindow foreground,
        IEnumerable<(PaneId Id, IHostedWindowPane Pane)> panes,
        PaneId current)
    {
        ArgumentNullException.ThrowIfNull(panes);

        // A pane whose window has not launched yet holds no handle, and neither does a foreground
        // report for a window that has just died. Letting those two nothings match would focus an
        // arbitrary half-started pane.
        if (foreground.Window.IsNone && foreground.RootWindow.IsNone) return null;

        foreach (var (id, pane) in panes)
        {
            // Both handles: in embed mode the root window Windows reports is the pane host, and in
            // attach mode it is the application's own top-level window.
            if (!pane.OwnsWindow(foreground.RootWindow) && !pane.OwnsWindow(foreground.Window)) continue;

            // Already there. Saying so costs a relayout for nothing, on every click into a pane.
            return id == current ? null : id;
        }

        // A window belonging to no pane is the user alt-tabbing to their browser. WinMux's focused
        // pane is where its keys go when it is focused *again*, so moving it because the user left
        // would be exactly wrong.
        return null;
    }
}
