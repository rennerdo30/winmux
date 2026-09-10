using System.Globalization;
using System.Text;
using WinMux.Core.Layout;

namespace WinMux.Core.Session;

/// <summary>
/// Renders a session as TOML.
///
/// Written by hand rather than through a serializer's model, because the whole reason TOML was
/// chosen (ADR 0006) is that a human can read and edit the result — which means controlling
/// alignment, comments, key order and when a table goes inline. Correctness of the escaping is not
/// taken on trust: a test round-trips every generated file through Tomlyn, an independent parser.
///
/// The tree is FLATTENED into `[[windows.nodes]]` with generated ids. Nesting TOML tables to match
/// the tree produces headers like [[windows.root.children.children.children]], which is less
/// readable than the JSON it was meant to improve on.
/// </summary>
public static class TomlSessionWriter
{
    public static string Write(SessionSnapshot snapshot)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# WinMux session file. Safe to hand-edit — the fields you are most likely to");
        sb.AppendLine("# want are `program`, `args` and `cwd` on the [[windows.panes]] entries below.");
        sb.AppendLine("# Restoring recreates processes from these descriptors; it does NOT restore");
        sb.AppendLine("# process state (scrollback, in-flight jobs, TUI state are out of scope).");
        sb.AppendLine();
        sb.Append("version = ").Append(snapshot.Version).AppendLine();
        sb.Append("saved_at = ").Append(Timestamp(snapshot.SavedAt)).AppendLine();

        foreach (var window in snapshot.Windows) WriteWindow(sb, window);
        return sb.ToString();
    }

    private static void WriteWindow(StringBuilder sb, WindowSnapshot window)
    {
        var flat = Flattener.Flatten(window.Root);

        sb.AppendLine();
        sb.AppendLine("[[windows]]");
        sb.Append("title   = ").Append(Str(window.Title)).AppendLine();
        sb.Append("root    = ").Append(Str(flat.RootId)).AppendLine();
        sb.Append("focused = ").Append(Str(window.FocusedPane.ToString("D"))).AppendLine();
        sb.Append("bounds  = { x = ").Append(window.Bounds.X)
          .Append(", y = ").Append(window.Bounds.Y)
          .Append(", width = ").Append(window.Bounds.Width)
          .Append(", height = ").Append(window.Bounds.Height)
          .AppendLine(" }");

        sb.AppendLine();
        sb.AppendLine("# ---- layout tree, flattened. `children` and `root` refer to node ids. ----");
        foreach (var node in flat.Nodes) WriteNode(sb, node);

        sb.AppendLine();
        sb.AppendLine("# ---- panes. This is the part worth editing. ----");
        foreach (var pane in flat.Panes) WritePane(sb, pane);
    }

    private static void WriteNode(StringBuilder sb, FlatNode node)
    {
        sb.AppendLine();
        sb.AppendLine("[[windows.nodes]]");
        Kv(sb, NodeKeyWidth, "id", Str(node.Id));
        Kv(sb, NodeKeyWidth, "kind", Str(node.Kind));

        switch (node.Kind)
        {
            case NodeKinds.Leaf:
                Kv(sb, NodeKeyWidth, "pane", Str(node.PaneId!.Value.ToString("D")));
                break;

            case NodeKinds.Split:
                Kv(sb, NodeKeyWidth, "direction", Str(TomlNames.Text(node.Direction!.Value)));
                Kv(sb, NodeKeyWidth, "children", StrArray(node.Children));
                Kv(sb, NodeKeyWidth, "ratios", NumArray(node.Ratios!));
                break;

            case NodeKinds.Stack:
                Kv(sb, NodeKeyWidth, "children", StrArray(node.Children));
                Kv(sb, NodeKeyWidth, "active", node.ActiveIndex!.Value.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    private static void WritePane(StringBuilder sb, PaneSnapshot pane)
    {
        var r = pane.Restore;
        sb.AppendLine();
        sb.AppendLine("[[windows.panes]]");
        Kv(sb, PaneKeyWidth, "id", Str(pane.Id.ToString("D")));
        Kv(sb, PaneKeyWidth, "kind", Str(TomlNames.Text(pane.Kind)));
        Kv(sb, PaneKeyWidth, "title", Str(pane.Title));

        // `is not null` rather than IsNullOrEmpty: an empty program and an absent one are different
        // states, and collapsing them here would make the round trip lossy for no benefit.
        if (r.Program is not null)
            Kv(sb, PaneKeyWidth, "program", Str(r.Program));
        if (r.Args.Count > 0)
            Kv(sb, PaneKeyWidth, "args", StrArray(r.Args));

        if (r.Cwd.IsKnown)
        {
            Kv(sb, PaneKeyWidth, "cwd", Str(r.Cwd.Path));
            // Provenance travels with the path (ADR 0004): a stale shell report and a live process
            // query do not deserve equal trust when the session is restored.
            Kv(sb, PaneKeyWidth, "cwd_source", Str(TomlNames.Text(r.Cwd.Source)));
            Kv(sb, PaneKeyWidth, "cwd_captured_at", Timestamp(r.Cwd.CapturedAt));
        }

        if (pane.Kind == Model.PaneKind.ForeignApp || r.Strategy != Model.HostStrategy.Embed)
            Kv(sb, PaneKeyWidth, "strategy", Str(TomlNames.Text(r.Strategy)));

        // Inline tables first, then any that were too long to inline. A scalar key written after a
        // [windows.panes.x] header would attach to that sub-table instead of the pane, so the order
        // here is load-bearing, not cosmetic.
        var deferred = new List<(string Name, IReadOnlyDictionary<string, string> Map)>();
        Emit("env", r.EnvOverrides);
        Emit("extras", r.Extras);
        foreach (var (name, map) in deferred) WriteSubTable(sb, name, map);

        void Emit(string name, IReadOnlyDictionary<string, string> map)
        {
            if (map.Count == 0) return;
            var inline = InlineMap(map);
            if (PaneKeyWidth + 3 + inline.Length <= MaxInlineWidth) Kv(sb, PaneKeyWidth, name, inline);
            else deferred.Add((name, map));
        }
    }

    /// <summary>A map too wide to inline becomes its own table, one key per line.</summary>
    private static void WriteSubTable(StringBuilder sb, string name, IReadOnlyDictionary<string, string> map)
    {
        sb.AppendLine();
        sb.Append("[windows.panes.").Append(name).AppendLine("]");
        var width = map.Keys.Max(k => Key(k).Length);
        foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Kv(sb, width, Key(k), Str(v));
    }

    // ---------------- value formatting ----------------

    /// <summary>Widest key in each section, so the `=` signs line up down the file.</summary>
    private const int NodeKeyWidth = 9;    // "direction"
    private const int PaneKeyWidth = 15;   // "cwd_captured_at"

    /// <summary>Beyond this, an inline table becomes a sub-table instead of one very long line.</summary>
    private const int MaxInlineWidth = 100;

    private static void Kv(StringBuilder sb, int width, string key, string value) =>
        sb.Append(key.PadRight(width)).Append(" = ").AppendLine(value);


    /// <summary>
    /// A TOML string, preferring the literal form so Windows paths keep their backslashes.
    /// Falls back to a basic string with escapes when the literal form cannot represent the value.
    /// </summary>
    internal static string Str(string? value)
    {
        value ??= string.Empty;

        var literalSafe = !value.Contains('\'') && !value.Any(c => c is '\n' or '\r' || char.IsControl(c));
        if (literalSafe) return "'" + value + "'";

        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static string StrArray(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Str)) + "]";

    private static string NumArray(IEnumerable<double> values) =>
        "[" + string.Join(", ", values.Select(v => v.ToString("0.######", CultureInfo.InvariantCulture))) + "]";

    private static string InlineMap(IReadOnlyDictionary<string, string> map) =>
        "{ " + string.Join(", ", map.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                    .Select(kv => Key(kv.Key) + " = " + Str(kv.Value))) + " }";

    /// <summary>A bare key where TOML allows it, a quoted key otherwise.</summary>
    private static string Key(string key) =>
        key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? key
            : Str(key).StartsWith('\'') ? "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : Str(key);

    /// <summary>An unquoted TOML offset date-time.</summary>
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
