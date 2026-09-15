using System.Globalization;
using Tomlyn;
using Tomlyn.Model;
using WinMux.Core.Layout;

using WinMux.Core.Update;

namespace WinMux.Core.Settings;

/// <summary>
/// Reads and writes <see cref="WinMuxSettings"/> as TOML.
///
/// Deliberately gentler than <c>SessionFile</c>, and for a reason worth stating: a session is the
/// user's work and a broken one must never be silently replaced, so loading refuses and quarantines.
/// Settings are preferences. Refusing to start because a preference file has a typo would be
/// absurd, so a bad file falls back to defaults and *says which key was wrong* — the fallback is
/// reported, not hidden.
/// </summary>
public static class SettingsFile
{
    public const string FileName = "settings.toml";

    /// <summary>The outcome of a load: always usable settings, plus anything worth telling the user.</summary>
    /// <param name="Settings">The settings read, or the defaults.</param>
    /// <param name="Warning">Why the file was not used in full, or null when it was.</param>
    public readonly record struct LoadResult(WinMuxSettings Settings, string? Warning);

    /// <summary><c>%APPDATA%\WinMux\settings.toml</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinMux",
        FileName);

    public static LoadResult Load(string path)
    {
        if (!File.Exists(path)) return new LoadResult(WinMuxSettings.Defaults, null);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(WinMuxSettings.Defaults, $"could not read {path}: {ex.Message}");
        }

        return Parse(text, path);
    }

    public static LoadResult Parse(string text, string where = "settings")
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions())
                   ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            return new LoadResult(WinMuxSettings.Defaults, $"{where} is not valid TOML, using defaults. {ex.Message}");
        }

        var problems = new List<string>();
        var settings = new WinMuxSettings
        {
            Version = WinMuxSettings.CurrentVersion,
            Theme = Enum(root, "theme", WinMuxSettings.Defaults.Theme, problems),
            DefaultTerminal = String(root, "default_terminal", WinMuxSettings.Defaults.DefaultTerminal),
            DefaultTabPlacement = Enum(root, "default_tab_placement", WinMuxSettings.Defaults.DefaultTabPlacement, problems),
            ConfirmBeforeClosingPanes =
                Bool(root, "confirm_before_closing_panes", WinMuxSettings.Defaults.ConfirmBeforeClosingPanes, problems),
            CheckForUpdates = Bool(root, "check_for_updates", WinMuxSettings.Defaults.CheckForUpdates, problems),
            UpdateChannel = Enum(root, "update_channel", WinMuxSettings.Defaults.UpdateChannel, problems),
            TerminalFontFamily = String(root, "terminal_font_family", WinMuxSettings.Defaults.TerminalFontFamily),
            TerminalFontSize =
                Number(root, "terminal_font_size", WinMuxSettings.Defaults.TerminalFontSize, 6, 72, problems),
        };

        return new LoadResult(
            settings,
            problems.Count == 0 ? null : $"{where}: " + string.Join("; ", problems));
    }

    public static void Save(string path, WinMuxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Written atomically for the same reason a session is: a half-written preferences file on a
        // power cut would greet the user with an error next launch.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(settings));
        File.Move(temporary, path, overwrite: true);
    }

    public static string Serialize(WinMuxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"""
            # WinMux settings. Safe to hand-edit; an unreadable value falls back to the default
            # and WinMux says so rather than starting differently without telling you.

            version                      = {settings.Version.ToString(CultureInfo.InvariantCulture)}

            # "system" follows the Windows light/dark setting and changes with it. "dark" or "light" pin it.
            theme                        = '{Text(settings.Theme)}'

            # Which terminal a plain "new terminal" opens: cmd, windows-powershell, powershell, wsl.
            default_terminal             = '{settings.DefaultTerminal}'

            # Where a new tab group puts its tabs: top, bottom, left, right.
            default_tab_placement        = '{Text(settings.DefaultTabPlacement)}'

            # Ask before an action closes panes that are still running.
            confirm_before_closing_panes = {(settings.ConfirmBeforeClosingPanes ? "true" : "false")}

            # Look for a newer release on startup. Nothing is downloaded or installed without asking.
            check_for_updates            = {(settings.CheckForUpdates ? "true" : "false")}

            # Which releases to be offered: stable, prerelease.
            update_channel               = '{Text(settings.UpdateChannel)}'

            # The font terminals draw with. A fallback list; the first one present is used, and the
            # cell size is measured from whichever that turns out to be.
            terminal_font_family         = '{settings.TerminalFontFamily}'
            terminal_font_size           = {settings.TerminalFontSize.ToString(CultureInfo.InvariantCulture)}

            """;
    }

    private static string Text(ThemePreference value) => value switch
    {
        ThemePreference.Dark => "dark",
        ThemePreference.Light => "light",
        _ => "system",
    };

    private static string Text(UpdateChannel value) => value switch
    {
        UpdateChannel.Prerelease => "prerelease",
        _ => "stable",
    };

    private static string Text(TabStripPlacement value) => value switch
    {
        TabStripPlacement.Bottom => "bottom",
        TabStripPlacement.Left => "left",
        TabStripPlacement.Right => "right",
        _ => "top",
    };

    private static TEnum Enum<TEnum>(TomlTable table, string key, TEnum fallback, List<string> problems)
        where TEnum : struct, Enum
    {
        if (!table.TryGetValue(key, out var raw)) return fallback;
        if (raw is not string text)
        {
            problems.Add($"`{key}` should be a string");
            return fallback;
        }

        // Case- and separator-insensitive so "windows-powershell" style values read naturally.
        foreach (var candidate in System.Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), text.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        problems.Add(
            $"`{key}` is \"{text}\", expected one of " +
            string.Join(", ", System.Enum.GetNames<TEnum>().Select(name => "\"" + name.ToLowerInvariant() + "\"")));
        return fallback;
    }

    private static string String(TomlTable table, string key, string fallback) =>
        table.TryGetValue(key, out var raw) && raw is string text && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    /// <summary>
    /// A number, within bounds.
    ///
    /// TOML distinguishes integers from floats, and a hand-edited file will contain both — 14 and
    /// 14.0 mean the same thing to a person. Out-of-range values are refused rather than clamped:
    /// a two-pixel font is not what anyone meant, and silently using 6 would hide the typo.
    /// </summary>
    private static double Number(
        TomlTable table, string key, double fallback, double minimum, double maximum, List<string> problems)
    {
        if (!table.TryGetValue(key, out var raw)) return fallback;

        var value = raw switch
        {
            double number => number,
            long integer => integer,
            int integer => integer,
            _ => double.NaN,
        };

        if (double.IsNaN(value))
        {
            problems.Add($"`{key}` should be a number");
            return fallback;
        }

        if (value < minimum || value > maximum)
        {
            problems.Add($"`{key}` should be between {minimum} and {maximum}");
            return fallback;
        }

        return value;
    }

    private static bool Bool(TomlTable table, string key, bool fallback, List<string> problems)
    {
        if (!table.TryGetValue(key, out var raw)) return fallback;
        if (raw is bool value) return value;
        problems.Add($"`{key}` should be true or false");
        return fallback;
    }
}
