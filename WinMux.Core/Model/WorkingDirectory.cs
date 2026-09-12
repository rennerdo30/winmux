namespace WinMux.Core.Model;

/// <summary>
/// Which of the layered strategies in CLAUDE.md section 4 produced a working directory.
///
/// Ordered worst to best deliberately: a larger value is more trustworthy, so
/// <see cref="WorkingDirectory.Better"/> is a plain comparison.
/// </summary>
public enum CwdSource
{
    /// <summary>Nothing is known. Restore will fall back to the user's default directory.</summary>
    Unknown = 0,

    /// <summary>Where the pane's program was originally launched. Correct only until the user moves.</summary>
    LaunchDirectory = 1,

    /// <summary>
    /// PEB of the pane's own process. Reliable for cmd; measured permanently STALE for PowerShell,
    /// whose Set-Location does not update the process working directory, and meaningless for WSL
    /// (it returns a Windows path for a Linux shell). See ADR 0004.
    /// </summary>
    ProcessRoot = 2,

    /// <summary>PEB of the deepest descendant. Covers a nested shell, where a shell report goes stale.</summary>
    ProcessDeepest = 3,

    /// <summary>The shell reported it via OSC 9;9 or OSC 7. The only method that works for PowerShell and WSL.</summary>
    ShellReported = 4,
}

/// <summary>
/// A captured working directory together with its provenance.
///
/// ADR 0004 decision 5: a value from a stale shell report and one from a live process query do not
/// deserve equal trust on restore, so the source and the capture time travel with the path rather
/// than being discarded at the boundary.
/// </summary>
public sealed record WorkingDirectory(string Path, CwdSource Source, DateTimeOffset CapturedAt)
{
    public static readonly WorkingDirectory None = new(string.Empty, CwdSource.Unknown, default);

    public bool IsKnown => Source != CwdSource.Unknown && !string.IsNullOrWhiteSpace(Path);

    /// <summary>
    /// Pick the better of two captures. A fresh deepest-child PEB reading supersedes an older OSC
    /// report because it is the measured nested-shell case; otherwise OSC remains authoritative
    /// over a root PEB (which is permanently stale in PowerShell). Capture time breaks ties.
    /// </summary>
    public static WorkingDirectory Better(WorkingDirectory a, WorkingDirectory b)
    {
        if (!a.IsKnown) return b;
        if (!b.IsKnown) return a;
        if (IsShellVersusDeepest(a, b)) return a.CapturedAt >= b.CapturedAt ? a : b;
        if (a.Source != b.Source) return a.Source > b.Source ? a : b;
        return a.CapturedAt >= b.CapturedAt ? a : b;
    }

    private static bool IsShellVersusDeepest(WorkingDirectory a, WorkingDirectory b) =>
        (a.Source == CwdSource.ShellReported && b.Source == CwdSource.ProcessDeepest) ||
        (a.Source == CwdSource.ProcessDeepest && b.Source == CwdSource.ShellReported);

    public override string ToString() =>
        IsKnown ? $"{Path} ({Source} @ {CapturedAt:u})" : "(unknown)";
}
