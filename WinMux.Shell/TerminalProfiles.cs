namespace WinMux.Shell;

internal sealed record TerminalProfile(string Name, string Program, IReadOnlyList<string> Arguments);

internal static class TerminalProfiles
{
    public static TerminalProfile Cmd { get; } = new("cmd", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", []);
    public static TerminalProfile WindowsPowerShell { get; } = new("Windows PowerShell", "powershell.exe", ["-NoLogo"]);
    public static TerminalProfile PowerShell { get; } = new("PowerShell", "pwsh.exe", ["-NoLogo"]);
    public static TerminalProfile Wsl { get; } = new("WSL", "wsl.exe", []);

    /// <summary>
    /// Resolve a profile by its settings-file name. An unknown name falls back to cmd rather than
    /// failing to open a terminal — the settings loader has already reported anything it could not
    /// read, and refusing to open a pane over a preference would be a poor trade.
    /// </summary>
    public static TerminalProfile ByName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "windows-powershell" => WindowsPowerShell,
        "powershell" or "pwsh" => PowerShell,
        "wsl" => Wsl,
        _ => Cmd,
    };
}
