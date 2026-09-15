using WinMux.Core.Settings;

namespace WinMux.Shell.Settings;

/// <summary>
/// The live profile list: everything the user can open in a pane (CLAUDE.md section 5a).
///
/// Static alongside <see cref="ShellSettings"/>, and for the same reason — one user, one list, and
/// every surface that can open a pane reads it, so adding a profile has to appear everywhere at
/// once rather than in whichever menu was rebuilt last.
/// </summary>
internal static class ShellProfiles
{
    private static List<LaunchProfile> _profiles = [.. LaunchProfile.Defaults];

    /// <summary>Raised after any change, so open menus can rebuild.</summary>
    public static event Action? Changed;

    public static IReadOnlyList<LaunchProfile> All => _profiles;

    public static string Path { get; private set; } = ProfilesFile.DefaultPath;

    /// <summary>What was skipped when loading, if anything. Surfaced once at startup.</summary>
    public static string? Warning { get; private set; }

    public static void Load(string? path = null)
    {
        Path = path ?? ProfilesFile.DefaultPath;
        var result = ProfilesFile.Load(Path);
        _profiles = [.. result.Profiles];
        Warning = result.Warning;
    }

    public static LaunchProfile? ById(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// The profile a plain "new terminal" opens: the configured default if it still exists and is
    /// still a terminal, otherwise the first terminal there is. Falls back rather than failing,
    /// because a deleted profile must not make the new-terminal button stop working.
    /// </summary>
    public static LaunchProfile? DefaultTerminal()
    {
        var configured = ById(ShellSettings.Current.DefaultTerminal);
        if (configured is { Kind: ProfileKind.Terminal }) return configured;
        return _profiles.FirstOrDefault(p => p.Kind == ProfileKind.Terminal) ?? _profiles.FirstOrDefault();
    }

    /// <summary>Replace the whole list and persist. Returns an error only if the write failed.</summary>
    public static string? Replace(IEnumerable<LaunchProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = [.. profiles];
        Changed?.Invoke();

        try
        {
            ProfilesFile.Save(Path, _profiles);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The list still takes effect this run: refusing to honour a change the user just made
            // is worse than failing to remember it.
            return $"profiles applied but not saved: {ex.Message}";
        }
    }

    /// <summary>Add a profile, giving it an id that does not collide with an existing one.</summary>
    public static string? Add(LaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = profile.Id;
        var suffix = 2;
        while (_profiles.Any(p => p.Id == id)) id = $"{profile.Id}-{suffix++}";
        return Replace([.. _profiles, profile with { Id = id }]);
    }
}
