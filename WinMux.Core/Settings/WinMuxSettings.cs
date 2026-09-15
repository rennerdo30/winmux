using WinMux.Core.Layout;

namespace WinMux.Core.Settings;

/// <summary>Which colour scheme the shell should use.</summary>
public enum ThemePreference
{
    /// <summary>Follow the Windows light/dark setting, and change with it.</summary>
    System,
    Dark,
    Light,
}

/// <summary>
/// The shell's preferences, separate from any session.
///
/// Deliberately small. Every entry here changes something a user can see, and nothing is stored
/// that the session file already owns — a setting that does nothing is worse than a missing one,
/// because it looks like a promise.
///
/// Lives beside the session code because CLAUDE.md section 3 puts config in Core: it is portable
/// data with no platform in it, and it is read and written the same way a session is.
/// </summary>
public sealed record WinMuxSettings
{
    /// <summary>Bumped only when an old file would otherwise be misread.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>Colour scheme. <see cref="ThemePreference.System"/> tracks Windows live.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>
    /// Which terminal a plain "new terminal" opens when there is nothing to inherit from. Stored as
    /// a profile name rather than a program path so it survives a PATH change and stays readable.
    /// </summary>
    public string DefaultTerminal { get; init; } = "cmd";

    /// <summary>Where a newly created tab group puts its tabs.</summary>
    public TabStripPlacement DefaultTabPlacement { get; init; } = TabStripPlacement.Top;

    /// <summary>
    /// Ask before an action closes panes that are still running. Off means Open replaces a session
    /// without a prompt; it never means a pane closes without being detached safely.
    /// </summary>
    public bool ConfirmBeforeClosingPanes { get; init; } = true;

    /// <summary>The defaults, for a first run or a settings file that could not be read.</summary>
    public static WinMuxSettings Defaults { get; } = new();
}
