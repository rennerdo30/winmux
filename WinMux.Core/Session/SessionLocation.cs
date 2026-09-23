namespace WinMux.Core.Session;

/// <summary>
/// Where the session file lives when nobody named one.
///
/// It used to be <c>session.toml</c> relative to the <i>working directory</i> — so double-clicking
/// <c>WinMux.exe</c> put the session inside the installation, and the updater, which replaces the
/// installation, took the session with it. A session started from a shortcut or a terminal landed
/// somewhere else again. Priority 1 is that the session survives; it now lives beside the settings,
/// in <c>%APPDATA%\WinMux</c>, whatever the working directory.
///
/// A session left at the old place is copied to the new one the first time, and never deleted —
/// the copy is the migration, the original is the user's to throw away.
/// </summary>
public static class SessionLocation
{
    /// <summary><c>%APPDATA%\WinMux\session.toml</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinMux",
        SessionFile.DefaultFileName);

    /// <summary>The session to use, and where it was brought from if it was just moved.</summary>
    public sealed record Resolution(string Path, string? MigratedFrom);

    /// <param name="explicitPath">A path the user gave. Always wins, exactly as given.</param>
    /// <param name="legacyDirectories">
    /// Where an older WinMux may have left <c>session.toml</c>: the working directory and the
    /// installation directory. The first one holding a file is the one migrated.
    /// </param>
    /// <param name="defaultPath">The new home; <see cref="DefaultPath"/> unless a test says otherwise.</param>
    public static Resolution Resolve(string? explicitPath, IEnumerable<string> legacyDirectories, string? defaultPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return new Resolution(System.IO.Path.GetFullPath(explicitPath), null);
        }

        var target = defaultPath ?? DefaultPath;
        if (File.Exists(target)) return new Resolution(target, null);

        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var directory in legacyDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, SessionFile.DefaultFileName));
            if (!seen.Add(candidate) || !File.Exists(candidate)) continue;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(candidate, target, overwrite: false);
            return new Resolution(target, candidate);
        }

        return new Resolution(target, null);
    }
}
