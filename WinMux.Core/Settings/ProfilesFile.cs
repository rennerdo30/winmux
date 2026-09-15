using System.Globalization;
using Tomlyn;
using Tomlyn.Model;
using WinMux.Core.Model;

namespace WinMux.Core.Settings;

/// <summary>
/// Reads and writes the profile list as TOML, beside the settings.
///
/// Its own file rather than a section of <c>settings.toml</c>: profiles are a list a user curates
/// and is expected to hand-edit and share, while settings are a handful of switches. Mixing them
/// would make the interesting file hard to read.
///
/// Like settings and unlike a session, a profile that cannot be read is skipped and reported rather
/// than refused — losing one entry must not cost the other twenty, and it must not stop WinMux
/// starting.
/// </summary>
public static class ProfilesFile
{
    public const string FileName = "profiles.toml";

    /// <param name="Profiles">Everything that loaded, or the defaults for a first run.</param>
    /// <param name="Warning">What was skipped and why, or null when nothing was.</param>
    public readonly record struct LoadResult(IReadOnlyList<LaunchProfile> Profiles, string? Warning);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinMux",
        FileName);

    public static LoadResult Load(string path)
    {
        if (!File.Exists(path)) return new LoadResult(LaunchProfile.Defaults, null);

        try
        {
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadResult(LaunchProfile.Defaults, $"could not read {path}: {ex.Message}");
        }
    }

    public static LoadResult Parse(string text, string where = "profiles")
    {
        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions()) ?? new TomlTable();
        }
        catch (TomlException ex)
        {
            return new LoadResult(LaunchProfile.Defaults, $"{where} is not valid TOML, using the defaults. {ex.Message}");
        }

        if (root["profiles"] is not TomlTableArray array)
        {
            // An empty list is a legitimate choice — someone may want no profiles at all — but a
            // file with no [[profiles]] at all is far more likely to be a mistake than an intent.
            return new LoadResult(LaunchProfile.Defaults, $"{where} contains no [[profiles]], using the defaults.");
        }

        var problems = new List<string>();
        var profiles = new List<LaunchProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < array.Count; index++)
        {
            var table = array[index];
            var at = $"{where}.profiles[{index}]";

            var name = Text(table, "name");
            var kind = ParseKind(Text(table, "kind"));
            var program = Text(table, "program");
            var host = Text(table, "host");

            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add($"{at} has no name and was skipped");
                continue;
            }

            // A connection names a host and derives its program; everything else names a program.
            // Saying which is missing beats one message that covers both badly.
            if (RemoteConnection.IsConnection(kind))
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    problems.Add($"{at} is a {Text(table, "kind")} profile with no host and was skipped");
                    continue;
                }
            }
            else if (string.IsNullOrWhiteSpace(program))
            {
                problems.Add($"{at} has no program and was skipped");
                continue;
            }

            var id = Text(table, "id");
            if (string.IsNullOrWhiteSpace(id)) id = LaunchProfile.MakeId(name);
            if (!seen.Add(id))
            {
                problems.Add($"{at} repeats the id \"{id}\" and was skipped");
                continue;
            }

            profiles.Add(new LaunchProfile
            {
                Id = id,
                Name = name,
                Kind = kind,
                Program = program,
                Args = Strings(table, "args"),
                WorkingDirectory = Text(table, "cwd"),
                Strategy = Text(table, "strategy").ToLowerInvariant() switch
                {
                    "embed" => HostStrategy.Embed,
                    "attach" => HostStrategy.Attach,
                    _ => HostStrategy.Auto,
                },
                WindowClass = Text(table, "window_class"),
                TitleContains = Text(table, "window_title_contains"),
                Source = Text(table, "source"),
                Host = host,
                Port = Port(table, at, problems),
                User = Text(table, "user"),
                Identity = Text(table, "identity"),
            });
        }

        if (profiles.Count == 0)
        {
            problems.Add("nothing usable was left, so the defaults are in force");
            return new LoadResult(LaunchProfile.Defaults, $"{where}: " + string.Join("; ", problems));
        }

        return new LoadResult(profiles, problems.Count == 0 ? null : $"{where}: " + string.Join("; ", problems));
    }

    public static void Save(string path, IReadOnlyList<LaunchProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Serialize(profiles));
        File.Move(temporary, path, overwrite: true);
    }

    public static string Serialize(IReadOnlyList<LaunchProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var text = new System.Text.StringBuilder();
        text.AppendLine("# WinMux profiles: the things you can open in a pane.");
        text.AppendLine("#");
        text.AppendLine("# kind     = 'terminal' runs a shell in a WinMux terminal pane.");
        text.AppendLine("#            'application' launches a windowed program and hosts its window.");
        text.AppendLine("#            'ssh' opens a saved host in a terminal pane.");
        text.AppendLine("#            'rdp' opens a saved host in the Windows Remote Desktop client.");
        text.AppendLine("# A connection ('ssh'/'rdp') needs `host` instead of `program`; the command is");
        text.AppendLine("# built from host, port, user and identity. No password is stored here or");
        text.AppendLine("# anywhere else in WinMux — both clients use Windows Credential Manager.");
        text.AppendLine("# strategy = 'auto' (let the quirks database decide), 'embed' or 'attach'.");
        text.AppendLine("# Editing this file by hand is supported; WinMux rewrites it when you change");
        text.AppendLine("# a profile in Settings, and comments you add are not preserved.");
        text.AppendLine();

        foreach (var profile in profiles)
        {
            text.AppendLine("[[profiles]]");
            text.AppendLine($"id      = {Literal(profile.Id)}");
            text.AppendLine($"name    = {Literal(profile.Name)}");
            text.AppendLine($"kind    = {Literal(KindText(profile.Kind))}");

            if (RemoteConnection.IsConnection(profile.Kind))
            {
                text.AppendLine($"host    = {Literal(profile.Host)}");
                if (profile.Port > 0)
                    text.AppendLine($"port    = {profile.Port.ToString(CultureInfo.InvariantCulture)}");
                if (!string.IsNullOrWhiteSpace(profile.User))
                    text.AppendLine($"user    = {Literal(profile.User)}");
                if (!string.IsNullOrWhiteSpace(profile.Identity))
                    text.AppendLine($"identity = {Literal(profile.Identity)}");
            }
            else
            {
                text.AppendLine($"program = {Literal(profile.Program)}");
            }
            if (profile.Args.Count > 0)
                text.AppendLine($"args    = [{string.Join(", ", profile.Args.Select(Literal))}]");
            if (!string.IsNullOrWhiteSpace(profile.WorkingDirectory))
                text.AppendLine($"cwd     = {Literal(profile.WorkingDirectory)}");
            if (profile.Kind == ProfileKind.Application)
            {
                text.AppendLine($"strategy = {Literal(profile.Strategy.ToString().ToLowerInvariant())}");
                if (!string.IsNullOrWhiteSpace(profile.WindowClass))
                    text.AppendLine($"window_class = {Literal(profile.WindowClass)}");
                if (!string.IsNullOrWhiteSpace(profile.TitleContains))
                    text.AppendLine($"window_title_contains = {Literal(profile.TitleContains)}");
            }
            if (!string.IsNullOrWhiteSpace(profile.Source))
                text.AppendLine($"source  = {Literal(profile.Source)}");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>
    /// A TOML literal string, so a Windows path keeps its backslashes (ADR 0006). A literal string
    /// cannot contain a single quote, so the rare value that does falls back to a basic string.
    /// </summary>
    private static ProfileKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "application" => ProfileKind.Application,
        "ssh" => ProfileKind.Ssh,
        "rdp" => ProfileKind.Rdp,
        "sftp" => ProfileKind.Sftp,
        "ftp" => ProfileKind.Ftp,
        _ => ProfileKind.Terminal,
    };

    private static string KindText(ProfileKind kind) => kind switch
    {
        ProfileKind.Application => "application",
        ProfileKind.Ssh => "ssh",
        ProfileKind.Rdp => "rdp",
        ProfileKind.Sftp => "sftp",
        ProfileKind.Ftp => "ftp",
        _ => "terminal",
    };

    /// <summary>
    /// A port, or zero for the protocol default. Out of range is refused rather than clamped: a
    /// port of 70000 is a typo, and silently connecting somewhere else would be worse.
    /// </summary>
    private static int Port(TomlTable table, string at, List<string> problems)
    {
        if (!table.TryGetValue("port", out var raw)) return 0;

        var value = raw switch
        {
            long number => number,
            int number => number,
            _ => -1L,
        };

        if (value is < 0 or > 65535)
        {
            problems.Add($"{at} has a port that is not between 1 and 65535; the default is used");
            return 0;
        }

        return (int)value;
    }

    private static string Literal(string value) => value.Contains('\'', StringComparison.Ordinal)
        ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                      .Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
        : "'" + value + "'";

    private static string Text(TomlTable table, string key) =>
        table.TryGetValue(key, out var raw) && raw is string value ? value : string.Empty;

    private static IReadOnlyList<string> Strings(TomlTable table, string key) =>
        table.TryGetValue(key, out var raw) && raw is TomlArray array
            ? array.OfType<string>().ToArray()
            : [];
}
