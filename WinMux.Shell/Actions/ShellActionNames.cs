namespace WinMux.Shell.Actions;

/// <summary>
/// Stable names shared by the keymap, command palette, and CLI action surfaces.
/// </summary>
public static class ShellActionNames
{
    public const string SplitColumns = "split-columns";
    public const string SplitRows = "split-rows";

    public const string FocusLeft = "focus-left";
    public const string FocusRight = "focus-right";
    public const string FocusUp = "focus-up";
    public const string FocusDown = "focus-down";

    public const string ClosePane = "close-pane";
    public const string NewTab = "new-tab";
    public const string NewEmptyPane = "new-empty-pane";
    public const string RenamePane = "rename-pane";

    /// <summary>A tab group whose tabs run down the side rather than across the top.</summary>
    public const string NewTabVertical = "new-tab-vertical";
    public const string NextTab = "next-tab";
    public const string PreviousTab = "previous-tab";
    public const string ShowSettings = "show-settings";
    public const string OpenSession = "open-session";
    public const string SaveSession = "save-session";
    public const string SaveSessionAs = "save-session-as";

    public const string ResizeLeft = "resize-left";
    public const string ResizeRight = "resize-right";
    public const string ResizeUp = "resize-up";
    public const string ResizeDown = "resize-down";
    public const string ShowPalette = "show-palette";
    public const string FindInPane = "find-in-pane";
    public const string SendPrefix = "send-prefix";
    public const string NewTerminalCmd = "new-terminal-cmd";
    public const string NewTerminalWindowsPowerShell = "new-terminal-windows-powershell";
    public const string NewTerminalPowerShell = "new-terminal-powershell";
    public const string NewTerminalWsl = "new-terminal-wsl";
    public const string NewFileBrowser = "new-file-browser";
    public const string OpenTerminalHere = "open-terminal-here";
    public const string ConfigureCwdReporting = "configure-cwd-reporting";
    public const string ToggleForeignHostStrategy = "toggle-foreign-host-strategy";

    public const string MoveTabsTop = "move-tabs-top";
    public const string MoveTabsBottom = "move-tabs-bottom";
    public const string MoveTabsLeft = "move-tabs-left";
    public const string MoveTabsRight = "move-tabs-right";

    public static IReadOnlyList<string> All { get; } =
    [
        SplitColumns,
        SplitRows,
        FocusLeft,
        FocusRight,
        FocusUp,
        FocusDown,
        ClosePane,
        NewTab,
        NewEmptyPane,
        RenamePane,
        NewTabVertical,
        NextTab,
        PreviousTab,
        ShowSettings,
        OpenSession,
        SaveSession,
        SaveSessionAs,
        ResizeLeft,
        ResizeRight,
        ResizeUp,
        ResizeDown,
        ShowPalette,
        FindInPane,
        SendPrefix,
        NewTerminalCmd,
        NewTerminalWindowsPowerShell,
        NewTerminalPowerShell,
        NewTerminalWsl,
        NewFileBrowser,
        OpenTerminalHere,
        ConfigureCwdReporting,
        ToggleForeignHostStrategy,
        MoveTabsTop,
        MoveTabsBottom,
        MoveTabsLeft,
        MoveTabsRight,
    ];
}
