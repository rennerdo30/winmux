using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinMux.Core.Settings;

/// <summary>
/// Pointing Claude Code's notifications at a terminal that can show them.
///
/// <para>
/// Claude Code decides for itself which terminals get a desktop notification, and by its own
/// documentation it sends one "only in Ghostty, Kitty, and iTerm2"; anywhere else it sends nothing
/// unless told otherwise. The telling is one key in its settings file, <c>preferredNotifChannel</c>.
/// This reads that file, reports what it says, and — only when asked — sets the key to a channel
/// WinMux understands, leaving every other key exactly as it was.
/// </para>
///
/// <para>
/// It is a user's configuration file for another program, so the rules are strict: a file that does
/// not parse as plain JSON is left alone and the user is told what to set by hand, and the previous
/// file is kept as a backup before anything is written.
/// </para>
/// </summary>
public static class ClaudeCodeNotifications
{
    /// <summary>The setting Claude Code reads.</summary>
    public const string Key = "preferredNotifChannel";

    /// <summary>
    /// The value WinMux asks for: iTerm2's channel, which writes the message as <c>OSC 9</c>. Chosen
    /// over <c>terminal_bell</c> because a bell says only "look", where OSC 9 says why.
    /// </summary>
    public const string Channel = "iterm2";

    /// <summary>What the settings file says now.</summary>
    /// <param name="Path">The file, whether or not it exists.</param>
    /// <param name="Exists">False when Claude Code has no user settings file yet.</param>
    /// <param name="Current">The channel it is set to, or null when unset.</param>
    /// <param name="Problem">Why the file cannot be changed safely, or null when it can.</param>
    public sealed record State(string Path, bool Exists, string? Current, string? Problem)
    {
        /// <summary>Already sending notifications WinMux can show.</summary>
        public bool IsSetUp => Problem is null && Current is Channel or "iterm2_with_bell" or "kitty" or "ghostty";
    }

    /// <summary>
    /// Claude Code's user settings file: <c>settings.json</c> in <c>CLAUDE_CONFIG_DIR</c> when that
    /// is set, else in <c>.claude</c> under the user's home.
    /// </summary>
    public static string DefaultPath(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var directory = environment("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }

        return System.IO.Path.Combine(directory, "settings.json");
    }

    public static State Inspect(string path)
    {
        if (!File.Exists(path)) return new State(path, Exists: false, Current: null, Problem: null);

        try
        {
            var root = Read(path);
            var current = root[Key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            return new State(path, Exists: true, current, Problem: null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new State(path, Exists: true, Current: null,
                Problem: $"{path} could not be read as plain JSON ({ex.Message}). To set it by hand, add " +
                         $"\"{Key}\": \"{Channel}\" to it.");
        }
    }

    /// <summary>
    /// Set <see cref="Key"/> to <see cref="Channel"/>, keeping every other setting. The old file is
    /// copied to <c>settings.json.winmux-backup</c> first, and the new one is written atomically.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is not something this should touch.</exception>
    public static void Apply(string path)
    {
        var state = Inspect(path);
        if (state.Problem is not null) throw new InvalidOperationException(state.Problem);

        var root = state.Exists ? Read(path) : new JsonObject();
        root[Key] = Channel;

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (state.Exists) File.Copy(path, path + ".winmux-backup", overwrite: true);

        var temporary = path + ".winmux-tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Strict: comments and trailing commas are refused rather than skipped, because writing the
    /// file back would silently drop them.
    /// </summary>
    private static JsonObject Read(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
               {
                   CommentHandling = JsonCommentHandling.Disallow,
                   AllowTrailingCommas = false,
               }) as JsonObject
               ?? throw new InvalidOperationException("the top level is not a JSON object");
    }
}
