namespace WinMux.Platform;

/// <summary>Which window the operating system has just made active.</summary>
/// <param name="Window">The window itself, which may be a child of something we host.</param>
/// <param name="RootWindow">
/// Its top-level ancestor. An application embedded in a pane host is a child of that host's window,
/// so the root is what the shell recognises; in attach mode the two are the same.
/// </param>
/// <param name="ProcessId">The owning process, for telling one hosted application from another.</param>
public readonly record struct ForegroundWindow(WindowHandle Window, WindowHandle RootWindow, int ProcessId);

/// <summary>
/// Being told when the user gives focus to something outside WinMux's own controls.
///
/// CLAUDE.md section 6: "Maintain WinMux's own focused-pane state and reconcile it with the OS;
/// never assume they agree." Without this the shell only ever learns about focus it caused itself,
/// so clicking into an Explorer pane left WinMux still believing the terminal three inches away was
/// focused — and the next key, or the next "close pane", acted on that terminal.
///
/// It reports, it does not ask. Nothing here calls into the foreign window, which is what makes it
/// safe to observe from the UI thread despite ADR 0001: reading a window's ancestor and its owning
/// process does not wait on the thread that owns it, while <c>SetWindowPos</c> and
/// <c>SendMessage</c> do.
/// </summary>
public interface IForegroundWindowMonitor
{
    /// <summary>Raised when the active window changes. Never raised for a window that has died.</summary>
    event Action<ForegroundWindow>? Changed;

    /// <summary>
    /// Begin watching. Dispose to stop.
    ///
    /// Implementations may require the calling thread to pump messages, so this is called from the
    /// shell's UI thread and the event is raised there too.
    /// </summary>
    IDisposable Start();
}
