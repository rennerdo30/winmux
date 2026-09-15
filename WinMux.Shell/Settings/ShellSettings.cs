using WinMux.Core.Settings;

namespace WinMux.Shell.Settings;

/// <summary>
/// The live settings, and the one place that knows where they are stored.
///
/// Static for the same reason <see cref="PlatformServices"/> is: there is exactly one user with one
/// set of preferences, and threading an instance through every control would be ceremony without a
/// second implementation to justify it.
/// </summary>
internal static class ShellSettings
{
    private static WinMuxSettings _current = WinMuxSettings.Defaults;

    /// <summary>Raised after <see cref="Update"/>, so open windows can follow a change at once.</summary>
    public static event Action? Changed;

    public static WinMuxSettings Current => _current;

    public static string Path { get; private set; } = SettingsFile.DefaultPath;

    /// <summary>Why the file on disk was not used in full, if it was not. Shown once at startup.</summary>
    public static string? Warning { get; private set; }

    public static void Load(string? path = null)
    {
        Path = path ?? SettingsFile.DefaultPath;
        var result = SettingsFile.Load(Path);
        _current = result.Settings;
        Warning = result.Warning;
    }

    /// <summary>
    /// Apply and persist. Returns the error when the file could not be written — the new settings
    /// still take effect for this run, because refusing to honour a choice the user just made is
    /// worse than failing to remember it.
    /// </summary>
    public static string? Update(WinMuxSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _current = next;
        Changed?.Invoke();

        try
        {
            SettingsFile.Save(Path, next);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"settings applied but not saved: {ex.Message}";
        }
    }
}
