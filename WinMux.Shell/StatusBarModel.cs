using WinMux.Core.Model;

namespace WinMux.Shell;

/// <summary>How loudly a status message should read.</summary>
public enum StatusMessageKind
{
    Info,
    Error,
}

/// <summary>The three segments of the status bar, already worded.</summary>
/// <param name="Location">The focused pane and where it is, which is the product's headline claim.</param>
/// <param name="Session">Which session file is in use, and whether it is saved.</param>
/// <param name="Prefix">The prefix key, or the keys available while it is armed.</param>
public readonly record struct StatusBarText(string Location, string Session, string Prefix);

/// <summary>
/// What the status bar says.
///
/// It used to be one line of debug output — "4 panes   focus: ABC   Ctrl+B then a key" — with
/// messages appended after a pipe in the same grey. Two problems. "4 panes" is a fact nobody wants;
/// "focus: ABC" existed only because nothing on screen showed which pane had focus, which the
/// accent ring now does. And the two facts that would show this product works at all — the captured
/// working directory with the strategy that captured it (CLAUDE.md section 4, the single most
/// important field), and whether the session is actually saved — were nowhere in the interface.
/// </summary>
public static class StatusBarModel
{
    /// <summary>How a captured directory is described, worst source to best.</summary>
    public static string Describe(WorkingDirectory cwd)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        if (!cwd.IsKnown) return "directory not captured";

        var how = cwd.Source switch
        {
            CwdSource.ShellReported => "shell-reported",
            CwdSource.ProcessDeepest => "from the running program",
            CwdSource.ProcessRoot => "from the process",
            CwdSource.LaunchDirectory => "where it was launched",
            _ => "unknown",
        };

        return $"{cwd.Path} · {how}";
    }

    /// <summary>
    /// The left segment: the focused pane, and where it is.
    ///
    /// A pane with no captured directory says so rather than showing nothing, because "not
    /// captured" is the state a user can act on — it means installing the shell snippet.
    /// </summary>
    public static string Location(string? paneTitle, WorkingDirectory? cwd) =>
        string.IsNullOrWhiteSpace(paneTitle)
            ? "no pane"
            : $"{paneTitle}  ·  {Describe(cwd ?? WorkingDirectory.None)}";

    /// <summary>
    /// The middle segment: the session file and its save state.
    ///
    /// Persistence is priority 1 and was invisible; a user had no way to know whether the thing the
    /// product exists to do had happened.
    /// </summary>
    public static string Session(string? sessionPath, DateTimeOffset? savedAt, bool saving, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(sessionPath)
            ? "unsaved session"
            : System.IO.Path.GetFileName(sessionPath);

        if (saving) return $"{name} · saving…";
        if (savedAt is not { } at) return $"{name} · not saved yet";

        var age = now - at;
        var when = age < TimeSpan.FromSeconds(10)
            ? "just now"
            : age < TimeSpan.FromMinutes(1)
                ? $"{(int)age.TotalSeconds}s ago"
                : age < TimeSpan.FromHours(1)
                    ? $"{(int)age.TotalMinutes}m ago"
                    : at.ToLocalTime().ToString("HH:mm");

        return $"{name} · saved {when}";
    }

    /// <summary>
    /// The right segment: the prefix, or what it can do now that it is armed.
    ///
    /// Armed is a mode, and a mode with no visible state is how people end up typing a stray "x"
    /// into a shell and closing a pane instead.
    /// </summary>
    public static string Prefix(string prefixGesture, bool armed) =>
        armed
            ? "%  \"  split      ←↑↓→  focus      x  close      c  tab      :  commands"
            : prefixGesture;

    public static StatusBarText Build(
        string? paneTitle,
        WorkingDirectory? cwd,
        string? sessionPath,
        DateTimeOffset? savedAt,
        bool saving,
        string prefixGesture,
        bool armed,
        DateTimeOffset now) =>
        new(
            Location(paneTitle, cwd),
            Session(sessionPath, savedAt, saving, now),
            Prefix(prefixGesture, armed));
}
