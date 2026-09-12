namespace WinMux.Shell;

internal sealed record TerminalProfile(string Name, string Program, IReadOnlyList<string> Arguments);

internal static class TerminalProfiles
{
    public static TerminalProfile Cmd { get; } = new("cmd", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", []);
    public static TerminalProfile WindowsPowerShell { get; } = new("Windows PowerShell", "powershell.exe", ["-NoLogo"]);
    public static TerminalProfile PowerShell { get; } = new("PowerShell", "pwsh.exe", ["-NoLogo"]);
    public static TerminalProfile Wsl { get; } = new("WSL", "wsl.exe", []);
}
