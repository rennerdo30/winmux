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
    public const string NextTab = "next-tab";
    public const string PreviousTab = "previous-tab";
    public const string SaveSession = "save-session";

    public const string ResizeLeft = "resize-left";
    public const string ResizeRight = "resize-right";
    public const string ResizeUp = "resize-up";
    public const string ResizeDown = "resize-down";
    public const string ShowPalette = "show-palette";
    public const string SendPrefix = "send-prefix";
    public const string NewTerminalCmd = "new-terminal-cmd";
    public const string NewTerminalWindowsPowerShell = "new-terminal-windows-powershell";
    public const string NewTerminalPowerShell = "new-terminal-powershell";
    public const string NewTerminalWsl = "new-terminal-wsl";

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
        NextTab,
        PreviousTab,
        SaveSession,
        ResizeLeft,
        ResizeRight,
        ResizeUp,
        ResizeDown,
        ShowPalette,
        SendPrefix,
        NewTerminalCmd,
        NewTerminalWindowsPowerShell,
        NewTerminalPowerShell,
        NewTerminalWsl,
    ];
}
