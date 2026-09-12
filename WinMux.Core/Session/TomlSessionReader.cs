using System.Globalization;
using Tomlyn;
using Tomlyn.Model;
using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Core.Session;

/// <summary>
/// Parses a session file. Uses Tomlyn for the TOML grammar and does its own structural validation,
/// naming the offending key rather than returning a default — a session file is user data, and a
/// malformed one is a bug worth reporting, never a reason to silently start empty.
/// </summary>
public static class TomlSessionReader
{
    public static SessionSnapshot Read(string text)
    {
        TomlTable root;
        try
        {
            // Tomlyn 2.10 has no Toml.Parse().ToModel(); deserializing into its own TomlTable
            // model is the supported route to an untyped document.
            root = TomlSerializer.Deserialize<TomlTable>(text, new TomlSerializerOptions())
                   ?? throw new SessionFormatException("Session file is empty.");
        }
        catch (TomlException ex)
        {
            // Tomlyn's message already carries "(line,column) : error : ...", so adding our own
            // position produced two different-looking line numbers for one problem.
            throw new SessionFormatException($"Session file is not valid TOML. {ex.Message}");
        }

        var sourceVersion = (int)Integer(root, "version", "the file root");
        if (sourceVersion < 0)
            throw new SessionFormatException("Session file has no usable `version`. It was not written by WinMux.");
        if (sourceVersion > SessionSnapshot.CurrentVersion)
            throw new SessionFormatException(
                $"Session file is version {sourceVersion}, but this build understands up to " +
                $"{SessionSnapshot.CurrentVersion}. Upgrade WinMux rather than letting it discard your layout.");

        var savedAt = OptionalTimestamp(root, "saved_at") ?? default;

        // Split rather than chained with ||: a pattern variable bound inside a short-circuiting
        // condition is not definitely assigned afterwards, even when every other branch throws.
        if (!root.TryGetValue("windows", out var windowsRaw))
            throw new SessionFormatException("Session file contains no [[windows]].");
        if (windowsRaw is not TomlTableArray windows)
            throw new SessionFormatException("`windows` should be a table array, written as [[windows]].");
        if (windows.Count == 0)
            throw new SessionFormatException("Session file contains no [[windows]].");

        return new SessionSnapshot
        {
            // Reading is also migration: callers and the next save always see the current model.
            Version = SessionSnapshot.CurrentVersion,
            SavedAt = savedAt,
            Windows = windows.Select((w, i) => ReadWindow(w, $"windows[{i}]", sourceVersion)).ToArray(),
        };
    }

    private static WindowSnapshot ReadWindow(TomlTable window, string where, int sourceVersion)
    {
        var panes = ReadPanes(window, where, sourceVersion);
        var nodes = ReadNodes(window, where);
        var rootId = String(window, "root", where);

        if (!nodes.ContainsKey(rootId))
            throw new SessionFormatException($"{where}.root is \"{rootId}\", which has no [[windows.nodes]] entry.");

        var tree = Flattener.Rebuild(rootId, nodes, panes);

        // Reachability matters: an orphaned node means a hand-edit went wrong, and silently
        // dropping panes is exactly the "pane that vanishes without explanation" the contract bans.
        var reached = CountNodes(tree);
        if (reached != nodes.Count)
            throw new SessionFormatException(
                $"{where} defines {nodes.Count} nodes but only {reached} are reachable from root \"{rootId}\". " +
                "Some panes would be silently dropped.");

        var focusedPane = Guid(window, "focused", where);
        if (!ContainsPane(tree, focusedPane))
            throw new SessionFormatException(
                $"{where}.focused is \"{focusedPane:D}\", which does not identify a pane reachable " +
                $"from root \"{rootId}\".");

        return new WindowSnapshot
        {
            Title = Optional(window, "title") is string t ? t : string.Empty,
            Bounds = ReadBounds(window, where),
            FocusedPane = focusedPane,
            Root = tree,
        };
    }

    private static int CountNodes(NodeSnapshot node) =>
        1 + (node.Children?.Sum(CountNodes) ?? 0);

    private static bool ContainsPane(NodeSnapshot node, System.Guid paneId) =>
        node.Kind == NodeKinds.Leaf
            ? node.Pane?.Id == paneId
            : node.Children?.Any(child => ContainsPane(child, paneId)) == true;

    private static Rect ReadBounds(TomlTable window, string where)
    {
        if (Optional(window, "bounds") is not TomlTable b) return Rect.Empty;
        return new Rect(
            (int)Integer(b, "x", where + ".bounds"),
            (int)Integer(b, "y", where + ".bounds"),
            (int)Integer(b, "width", where + ".bounds"),
            (int)Integer(b, "height", where + ".bounds"));
    }

    private static Dictionary<string, FlatNode> ReadNodes(TomlTable window, string where)
    {
        if (!window.TryGetValue("nodes", out var raw) || raw is not TomlTableArray array || array.Count == 0)
            throw new SessionFormatException($"{where} has no [[windows.nodes]].");

        var result = new Dictionary<string, FlatNode>(StringComparer.Ordinal);
        for (int i = 0; i < array.Count; i++)
        {
            var t = array[i];
            var at = $"{where}.nodes[{i}]";
            var id = String(t, "id", at);
            var kind = String(t, "kind", at);

            FlatNode node = kind switch
            {
                NodeKinds.Leaf => new FlatNode(id, kind, Guid(t, "pane", at), null, [], null, null),

                NodeKinds.Split => new FlatNode(
                    id, kind, null,
                    TomlNames.ParseDirection(String(t, "direction", at), at),
                    StringList(t, "children", at),
                    DoubleList(t, "ratios", at),
                    null),

                NodeKinds.Stack => new FlatNode(
                    id, kind, null, null,
                    StringList(t, "children", at),
                    null,
                    (int)Integer(t, "active", at)),

                _ => throw new SessionFormatException(
                    $"Unknown node kind \"{kind}\" at {at}. Expected \"leaf\", \"split\" or \"stack\"."),
            };

            if (!result.TryAdd(id, node))
                throw new SessionFormatException($"Duplicate node id \"{id}\" at {at}.");
        }
        return result;
    }

    private static Dictionary<Guid, PaneSnapshot> ReadPanes(TomlTable window, string where, int sourceVersion)
    {
        var result = new Dictionary<Guid, PaneSnapshot>();
        if (!window.TryGetValue("panes", out var raw) || raw is not TomlTableArray array) return result;

        for (int i = 0; i < array.Count; i++)
        {
            var t = array[i];
            var at = $"{where}.panes[{i}]";
            var id = Guid(t, "id", at);
            var kind = TomlNames.ParsePaneKind(String(t, "kind", at), at);
            var title = Optional(t, "title") as string ?? string.Empty;

            var cwd = WorkingDirectory.None;
            if (Optional(t, "cwd") is string path && !string.IsNullOrWhiteSpace(path))
            {
                cwd = sourceVersion == 0
                    ? new WorkingDirectory(path, CwdSource.LaunchDirectory, DateTimeOffset.UnixEpoch)
                    : ReadCurrentWorkingDirectory(t, at, path);
            }
            else if (sourceVersion > 0 &&
                     (Optional(t, "cwd_source") is not null || Optional(t, "cwd_captured_at") is not null))
                throw new SessionFormatException(
                    $"{at} contains cwd provenance but no usable `cwd` path.");

            var pane = new PaneSnapshot
            {
                Id = id,
                Kind = kind,
                Title = title,
                Restore = new RestoreDescriptor
                {
                    Kind = kind,
                    Title = title,
                    Program = Optional(t, "program") as string,
                    Args = StringListOrEmpty(t, "args", at),
                    EnvOverrides = Map(t, "env", at, StringComparer.OrdinalIgnoreCase),
                    Cwd = cwd,
                    Strategy = Optional(t, "strategy") is string st ? TomlNames.ParseStrategy(st, at) : HostStrategy.Embed,
                    Extras = Map(t, "extras", at, StringComparer.Ordinal),
                },
            };

            if (!result.TryAdd(id, pane))
                throw new SessionFormatException($"Duplicate pane id \"{id:D}\" at {at}.");
        }
        return result;
    }

    private static WorkingDirectory ReadCurrentWorkingDirectory(TomlTable pane, string where, string path)
    {
        var source = Optional(pane, "cwd_source") is string sourceText
            ? TomlNames.ParseCwdSource(sourceText, where)
            : throw new SessionFormatException(
                $"{where} has `cwd` but is missing the string key `cwd_source`.");
        var capturedAt = OptionalTimestamp(pane, "cwd_captured_at")
            ?? throw new SessionFormatException(
                $"{where} has `cwd` but is missing the date-time key `cwd_captured_at`.");
        return new WorkingDirectory(path, source, capturedAt);
    }

    // ---------------- typed accessors ----------------

    private static object? Optional(TomlTable t, string key) => t.TryGetValue(key, out var v) ? v : null;

    private static string String(TomlTable t, string key, string where) =>
        Optional(t, key) as string
        ?? throw new SessionFormatException($"{where} is missing the string key `{key}`.");

    private static long Integer(TomlTable t, string key, string where) => Optional(t, key) switch
    {
        long l => l,
        int i => i,
        double d when Math.Abs(d % 1) < double.Epsilon => (long)d,
        null => throw new SessionFormatException($"{where} is missing the integer key `{key}`."),
        var other => throw new SessionFormatException($"{where}.{key} should be an integer, found {other.GetType().Name}."),
    };

    private static Guid Guid(TomlTable t, string key, string where)
    {
        var s = String(t, key, where);
        return System.Guid.TryParse(s, out var g)
            ? g
            : throw new SessionFormatException($"{where}.{key} is \"{s}\", which is not a valid id.");
    }

    private static IReadOnlyList<string> StringList(TomlTable t, string key, string where)
    {
        if (Optional(t, key) is not TomlArray array)
            throw new SessionFormatException($"{where} is missing the array key `{key}`.");
        return array.Select(v => v as string
            ?? throw new SessionFormatException($"{where}.{key} contains a non-string entry.")).ToArray();
    }

    private static IReadOnlyList<string> StringListOrEmpty(TomlTable t, string key, string where) =>
        Optional(t, key) is TomlArray ? StringList(t, key, where) : [];

    private static IReadOnlyList<double> DoubleList(TomlTable t, string key, string where)
    {
        if (Optional(t, key) is not TomlArray array)
            throw new SessionFormatException($"{where} is missing the array key `{key}`.");
        return array.Select(v => v switch
        {
            double d => d,
            long l => (double)l,
            _ => throw new SessionFormatException($"{where}.{key} contains a non-numeric entry."),
        }).ToArray();
    }

    private static IReadOnlyDictionary<string, string> Map(
        TomlTable t, string key, string where, StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        if (Optional(t, key) is not TomlTable table) return result;
        foreach (var (k, v) in table)
        {
            if (v is not string s)
                throw new SessionFormatException($"{where}.{key}.{k} should be a string, found {v?.GetType().Name ?? "nothing"}.");
            result[k] = s;
        }
        return result;
    }

    private static DateTimeOffset? OptionalTimestamp(TomlTable t, string key) => Optional(t, key) switch
    {
        null => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
        // Tomlyn may hand back its own date-time wrapper depending on version; parsing its text
        // form is version-proof and this value is not hot.
        var other when DateTimeOffset.TryParse(other.ToString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
        var other => throw new SessionFormatException($"`{key}` should be a date-time, found {other.GetType().Name}."),
    };
}
