namespace WinMux.Shell;

/// <summary>What a mouse button means in a terminal pane.</summary>
internal enum TerminalMouseAction
{
    /// <summary>Nothing; the button is not one this pane acts on.</summary>
    None,

    /// <summary>Copy the selection and drop it, as the console does.</summary>
    CopyAndClearSelection,

    /// <summary>Paste the clipboard into the program.</summary>
    Paste,
}

/// <summary>
/// The console's own right-click, which Windows calls QuickEdit.
///
/// <para>
/// Select with the mouse and right-click copies; right-click with nothing selected pastes. It is
/// how <c>cmd.exe</c> has behaved for thirty years and how Windows Terminal still behaves, so it is
/// what hands do without being told — and WinMux ignored the right button entirely, which made
/// copying out of a pane look broken rather than merely different.
/// </para>
///
/// <para>
/// Its own type because the rule is the whole of it: one line of wiring in the control, and a
/// decision that can be stated and checked without a pty, a clipboard or a window.
/// </para>
/// </summary>
internal static class TerminalQuickEdit
{
    /// <summary>
    /// What the right button does, given whether anything is selected.
    /// </summary>
    /// <remarks>
    /// Copy wins when there is a selection, and that ordering matters: pasting over a selection the
    /// user had just made would throw away the thing they were reaching for, and a terminal cannot
    /// undo a paste because the program has already read it.
    /// </remarks>
    public static TerminalMouseAction RightButton(bool hasSelection) =>
        hasSelection ? TerminalMouseAction.CopyAndClearSelection : TerminalMouseAction.Paste;

    /// <summary>
    /// What the middle button does: nothing.
    ///
    /// Pasting on middle-click is an X11 convention, not a Windows one, and a stray wheel press
    /// that silently types into a shell is not a trade worth making.
    /// </summary>
    public static TerminalMouseAction MiddleButton() => TerminalMouseAction.None;
}
