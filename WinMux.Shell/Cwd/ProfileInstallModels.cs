namespace WinMux.Shell.Cwd;

/// <summary>The shells for which WinMux can report profile-integration status.</summary>
public enum CwdProfileShell
{
    WindowsPowerShell,
    PowerShell,
    CommandPrompt,
    WslBash,
}

/// <summary>The outcome of inspecting or installing one shell integration.</summary>
public enum ProfileInstallState
{
    NotInstalled,
    Installed,
    AlreadyInstalled,
    ManualActionRequired,
    Failed,
}

/// <summary>A plain-language result for one shell.</summary>
public sealed record ShellProfileStatus(
    CwdProfileShell Shell,
    ProfileInstallState State,
    string Message,
    string? Target = null,
    string? Backup = null);

/// <summary>The per-shell result of an inspection or installation attempt.</summary>
public sealed record ProfileInstallationReport(IReadOnlyList<ShellProfileStatus> Shells)
{
    public bool HasFailures => Shells.Any(shell => shell.State == ProfileInstallState.Failed);
}

/// <summary>Filesystem locations used by <see cref="ProfileInstaller"/>.</summary>
public sealed record ProfileInstallerPaths(
    string WindowsPowerShellProfile,
    string PowerShellProfile,
    string IntegrationDirectory)
{
    public static ProfileInstallerPaths ForCurrentUser()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new ProfileInstallerPaths(
            Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(localData, "WinMux", "profiles"));
    }
}
