namespace CwdSpike;

internal sealed class ShellSpec
{
    public required string Name;
    public required string CommandLine;
    /// <summary>Typed into the shell to turn on cwd reporting — the opt-in profile snippet we would ship.</summary>
    public required string OscSnippet;
    /// <summary>path -> the command that changes directory.</summary>
    public required Func<string, string> Cd;
    /// <summary>A foreground child that runs for several seconds, so the shell is busy.</summary>
    public required string BusyChild;
    /// <summary>Start a nested shell and move deeper inside it.</summary>
    public required Func<string, string> NestedCd;
    public string Exit = "exit";
    public bool IsWsl;
    /// <summary>Where test directories live for this shell (Windows temp, or Linux /tmp under WSL).</summary>
    public required string Root;
    public int PromptQuietMs = 400;
}

internal static class Shells
{
    public const string SimpleDir = "simple";
    public const string AwkwardDir = "with space and ünïcode";
    public const string DeeperDir = "simple/deeper";

    public static string WinRoot => Path.Combine(Path.GetTempPath(), "winmux-spike4");
    public const string WslRoot = "/tmp/winmux-spike4";

    // ---- the snippets. These are the shippable artefact; profiles/ holds the same text. ----

    public const string PwshSnippet =
        "function prompt { $p = $ExecutionContext.SessionState.Path.CurrentLocation.ProviderPath; " +
        "\"$([char]27)]9;9;$p$([char]7)PS $p> \" }";

    // cmd's PROMPT understands $E (escape) and $P (current path).
    public const string CmdSnippet = "prompt $e]9;9;$P$e\\$P$G";

    // bash reports OSC 7 as a file:// URL, the xterm convention.
    public const string BashSnippet =
        "PROMPT_COMMAND='printf \"\\033]7;file://%s%s\\033\\\\\" \"$(hostname)\" \"$PWD\"'";

    public static List<ShellSpec> Build(bool includeWsl)
    {
        var list = new List<ShellSpec>
        {
            new()
            {
                Name = "pwsh",
                CommandLine = "pwsh.exe -NoLogo -NoProfile",
                OscSnippet = PwshSnippet,
                Cd = p => "Set-Location -LiteralPath '" + p + "'",
                BusyChild = "ping -n 8 127.0.0.1 | Out-Null",
                NestedCd = p => "cmd.exe /k \"cd /d \"\"" + p + "\"\"\"",
                Root = WinRoot,
            },
            new()
            {
                Name = "powershell",
                CommandLine = "powershell.exe -NoLogo -NoProfile",
                OscSnippet = PwshSnippet,
                Cd = p => "Set-Location -LiteralPath '" + p + "'",
                BusyChild = "ping -n 8 127.0.0.1 | Out-Null",
                NestedCd = p => "cmd.exe /k \"cd /d \"\"" + p + "\"\"\"",
                Root = WinRoot,
            },
            new()
            {
                Name = "cmd",
                CommandLine = "cmd.exe",
                OscSnippet = CmdSnippet,
                Cd = p => "cd /d \"" + p + "\"",
                BusyChild = "ping -n 8 127.0.0.1 > nul",
                NestedCd = p => "cmd.exe /k cd /d \"" + p + "\"",
                Root = WinRoot,
            },
        };

        if (includeWsl)
            list.Add(new ShellSpec
            {
                Name = "wsl (Debian bash)",
                CommandLine = "wsl.exe -d Debian",
                OscSnippet = BashSnippet,
                Cd = p => "cd '" + p + "'",
                BusyChild = "sleep 6",
                NestedCd = p => "bash -c 'cd \"" + p + "\"; exec bash'",
                Exit = "exit",
                IsWsl = true,
                Root = WslRoot,
                PromptQuietMs = 600,
            });

        return list;
    }

    /// <summary>Create the directories every scenario navigates into.</summary>
    public static void PrepareWindowsDirs()
    {
        Directory.CreateDirectory(Path.Combine(WinRoot, SimpleDir, "deeper"));
        Directory.CreateDirectory(Path.Combine(WinRoot, AwkwardDir));
    }

    public static string WslPrepareCommand =>
        "mkdir -p '" + WslRoot + "/" + SimpleDir + "/deeper' '" + WslRoot + "/" + AwkwardDir + "'";
}
