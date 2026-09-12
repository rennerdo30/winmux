using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Core.Session;

/// <summary>
/// The spelling of every enum in the session file.
///
/// Written out explicitly rather than derived from the enum names: the file is a published format
/// that users hand-edit, so renaming a C# member must not silently change it. An unrecognised
/// value names the offender rather than falling back to a default.
/// </summary>
internal static class TomlNames
{
    private static readonly (PaneKind Value, string Text)[] PaneKinds =
    [
        (PaneKind.Terminal, "terminal"),
        (PaneKind.FileBrowser, "file-browser"),
        (PaneKind.ForeignApp, "foreign-app"),
    ];

    private static readonly (SplitDirection Value, string Text)[] Directions =
    [
        (SplitDirection.Columns, "columns"),
        (SplitDirection.Rows, "rows"),
    ];

    private static readonly (CwdSource Value, string Text)[] CwdSources =
    [
        (CwdSource.Unknown, "unknown"),
        (CwdSource.LaunchDirectory, "launch-directory"),
        (CwdSource.ProcessRoot, "process-root"),
        (CwdSource.ProcessDeepest, "process-deepest"),
        (CwdSource.ShellReported, "shell-reported"),
    ];

    private static readonly (HostStrategy Value, string Text)[] Strategies =
    [
        (HostStrategy.Auto, "auto"),
        (HostStrategy.Embed, "embed"),
        (HostStrategy.Attach, "attach"),
    ];

    public static string Text(PaneKind v) => Find(PaneKinds, v);
    public static string Text(SplitDirection v) => Find(Directions, v);
    public static string Text(CwdSource v) => Find(CwdSources, v);
    public static string Text(HostStrategy v) => Find(Strategies, v);

    public static PaneKind ParsePaneKind(string s, string where) => Parse(PaneKinds, s, "pane kind", where);
    public static SplitDirection ParseDirection(string s, string where) => Parse(Directions, s, "split direction", where);
    public static CwdSource ParseCwdSource(string s, string where) => Parse(CwdSources, s, "cwd source", where);
    public static HostStrategy ParseStrategy(string s, string where) => Parse(Strategies, s, "host strategy", where);

    private static string Find<T>((T Value, string Text)[] map, T value) where T : struct, Enum
    {
        foreach (var (v, t) in map) if (EqualityComparer<T>.Default.Equals(v, value)) return t;
        throw new SessionFormatException($"No session-file spelling defined for {typeof(T).Name}.{value}.");
    }

    private static T Parse<T>((T Value, string Text)[] map, string s, string what, string where) where T : struct, Enum
    {
        foreach (var (v, t) in map) if (string.Equals(t, s, StringComparison.OrdinalIgnoreCase)) return v;
        var known = string.Join(", ", map.Select(m => "\"" + m.Text + "\""));
        throw new SessionFormatException($"Unknown {what} \"{s}\" at {where}. Expected one of {known}.");
    }
}
