namespace WinMux.Terminal;

/// <summary>How a program in a terminal asked for the user's attention.</summary>
public enum TerminalNotificationKind
{
    /// <summary><c>OSC 9 ; message</c> — iTerm2's notification, and Claude Code's when told to use it.</summary>
    Osc9,

    /// <summary><c>OSC 777 ; notify ; title ; body</c> — urxvt's, adopted by Ghostty and others.</summary>
    Osc777,

    /// <summary><c>OSC 99</c> — kitty's desktop-notification protocol.</summary>
    Osc99,

    /// <summary>The BEL character on its own: "look at me", with nothing more said.</summary>
    Bell,
}

/// <summary>
/// A program asking for attention — Claude Code waiting for a permission, a build that finished, a
/// shell ringing its bell. Text is already stripped of control characters and bounded, because it
/// arrives from whatever is running in the pane and is about to be shown outside it.
/// </summary>
/// <param name="Kind">Which protocol it arrived by.</param>
/// <param name="Title">The program's own heading, when the protocol carries one.</param>
/// <param name="Body">The message; empty for a bell.</param>
public sealed record TerminalNotification(TerminalNotificationKind Kind, string? Title, string Body);
