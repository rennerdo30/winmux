namespace WinMux.Platform;

/// <summary>An application the user has installed, as the system describes it.</summary>
/// <param name="Name">What the system calls it — the shortcut's name, not the executable's.</param>
/// <param name="Program">The executable to launch. Always resolved; never a shortcut.</param>
/// <param name="Arguments">Arguments the shortcut carried, if any.</param>
/// <param name="WorkingDirectory">The shortcut's working directory, or empty.</param>
/// <param name="Source">Where it was found, so the UI can group and the user can trust it.</param>
public readonly record struct InstalledApp(
    string Name,
    string Program,
    string Arguments,
    string WorkingDirectory,
    string Source);

/// <summary>
/// The applications this machine has, so a user can put one in a pane without knowing where its
/// executable lives (CLAUDE.md section 5a).
///
/// Behind the platform boundary because finding them is entirely platform work: on Windows it means
/// walking the Start Menu and following every shortcut to its target, which no portable API exposes.
/// The shell asks for a list of names and programs and stays free of P/Invoke (ADR 0013).
/// </summary>
public interface IAppCatalog
{
    /// <summary>
    /// Everything installed, de-duplicated and sorted by name.
    ///
    /// Expected to take hundreds of milliseconds — it reads a few hundred files — so callers run it
    /// off the UI thread. It never throws: a directory that cannot be read contributes nothing and
    /// is reported, because a single unreadable shortcut must not cost the whole catalogue.
    /// </summary>
    IReadOnlyList<InstalledApp> List(out string? error);

    /// <summary>
    /// Resolve one shortcut or executable to something launchable, for the "browse for a program"
    /// path where the user may well pick a <c>.lnk</c>.
    /// </summary>
    InstalledApp? Resolve(string path, out string? error);
}
