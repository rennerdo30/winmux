namespace WinMux.Shell.FileBrowser.Remote;

/// <summary>
/// POSIX path arithmetic for a remote filesystem.
///
/// Separate from <c>System.IO.Path</c> and never delegating to it, because on Windows that type
/// answers for Windows: <c>Path.Combine("/srv", "logs")</c> gives <c>/srv\logs</c>, and
/// <c>Path.GetFullPath</c> would anchor a remote path onto the local current directory. Both are
/// wrong in ways that produce a plausible-looking string, which is the kind of wrong that reaches
/// a server and creates a directory with a backslash in its name.
///
/// So: <c>/</c> always, case-sensitive always (the remote is almost certainly Unix), and the root
/// is <c>/</c> rather than a drive.
/// </summary>
internal static class RemotePath
{
    public const string Root = "/";

    /// <summary>Normalise: absolute, single separators, no <c>.</c> or <c>..</c>, no trailing slash.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Root;

        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw)
            {
                case ".":
                    continue;
                case "..":
                    // Walking above the root stays at the root, as `cd ..` in `/` does.
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(raw);
                    continue;
            }
        }

        return segments.Count == 0 ? Root : Root + string.Join('/', segments);
    }

    /// <summary>Join a directory and a child name. The name is a single component, not a path.</summary>
    public static string Combine(string directory, string name)
    {
        var parent = Normalize(directory);
        var child = name.Trim('/');
        if (child.Length == 0) return parent;
        return parent == Root ? Root + child : parent + "/" + child;
    }

    /// <summary>The containing directory, or null at the root — which has no parent, like a share.</summary>
    public static string? Parent(string path)
    {
        var normalized = Normalize(path);
        if (normalized == Root) return null;

        var cut = normalized.LastIndexOf('/');
        return cut <= 0 ? Root : normalized[..cut];
    }

    /// <summary>The last component: what to show in a list and what a rename changes.</summary>
    public static string Name(string path)
    {
        var normalized = Normalize(path);
        if (normalized == Root) return Root;
        return normalized[(normalized.LastIndexOf('/') + 1)..];
    }

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or sits underneath it.</summary>
    public static bool IsSelfOrDescendant(string root, string candidate)
    {
        var top = Normalize(root);
        var below = Normalize(candidate);

        if (below == top) return true;
        // The trailing separator matters: "/srv/data2" must not count as inside "/srv/data".
        return below.StartsWith(top == Root ? Root : top + "/", StringComparison.Ordinal);
    }
}
