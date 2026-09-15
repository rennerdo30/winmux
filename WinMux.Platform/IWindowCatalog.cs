namespace WinMux.Platform;

/// <summary>A top-level window that is already open and could be adopted into a pane.</summary>
/// <param name="Window">The handle to hand to a pane host.</param>
/// <param name="Title">What the window calls itself. Empty titles are not offered.</param>
/// <param name="ProcessName">The owning executable's file name, to tell three browsers apart.</param>
/// <param name="ProcessId">The owning process.</param>
/// <param name="ProgramPath">
/// The owning executable's full path when it can be read, so an adopted pane can be restored by
/// launching the same program next time. Empty when the process refuses to say — an elevated one
/// will — and the pane then restores as empty rather than as something wrong.
/// </param>
public readonly record struct AdoptableWindow(
    WindowHandle Window,
    string Title,
    string ProcessName,
    int ProcessId,
    string ProgramPath);

/// <summary>
/// The windows already on screen that WinMux could take into a pane.
///
/// CLAUDE.md section 9 lists adopting a running application as an open question; this is the half
/// of it that has to exist either way, because the shell cannot enumerate windows without Win32
/// (ADR 0013).
///
/// The selection rules from ADR 0003 apply here as hard as they do when launching: only visible,
/// top-level, non-owned windows with a real title are offered, because Notepad alone owns twelve
/// top-level windows and eleven of them are not the one anybody means.
/// </summary>
public interface IWindowCatalog
{
    /// <summary>
    /// Every adoptable window, most recently active first.
    /// </summary>
    /// <param name="exclude">
    /// Windows WinMux already owns or has claimed. Offering to adopt our own shell, or a window
    /// another pane is already hosting, would be a way to lose it.
    /// </param>
    /// <param name="error">Why the list may be incomplete, or null when it is not.</param>
    IReadOnlyList<AdoptableWindow> List(IReadOnlyCollection<WindowHandle> exclude, out string? error);
}
