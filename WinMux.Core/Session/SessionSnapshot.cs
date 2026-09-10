using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Core.Session;

/// <summary>
/// The persisted shape of a session, independent of file format.
///
/// The JSON-vs-TOML decision is still open (CLAUDE.md section 9) and must not leak in here: this
/// layer is what a serializer consumes, and keeping it format-neutral means the choice can be made
/// — and an ADR written — without touching the layout engine.
///
/// A single node record with optional fields, rather than a polymorphic hierarchy, because the
/// session file has to stay hand-editable. Discriminated unions serialize badly into both formats
/// and read worse.
/// </summary>
public sealed record SessionSnapshot
{
    /// <summary>Versioned from the very first write, with a migration path (CLAUDE.md section 4).</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public DateTimeOffset SavedAt { get; init; }
    public IReadOnlyList<WindowSnapshot> Windows { get; init; } = [];
}

public sealed record WindowSnapshot
{
    public string Title { get; init; } = string.Empty;
    public Rect Bounds { get; init; }
    public Guid FocusedPane { get; init; }
    public required NodeSnapshot Root { get; init; }
}

public static class NodeKinds
{
    public const string Leaf = "leaf";
    public const string Split = "split";
    public const string Stack = "stack";
}

public sealed record NodeSnapshot
{
    public required string Kind { get; init; }

    // leaf
    public PaneSnapshot? Pane { get; init; }

    // split
    public SplitDirection? Direction { get; init; }
    public IReadOnlyList<double>? Ratios { get; init; }

    // split and stack
    public IReadOnlyList<NodeSnapshot>? Children { get; init; }

    // stack
    public int? ActiveIndex { get; init; }
}

public sealed record PaneSnapshot
{
    public required Guid Id { get; init; }
    public required PaneKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public required RestoreDescriptor Restore { get; init; }
}

/// <summary>Thrown when a session file is structurally impossible. Never swallowed silently.</summary>
public sealed class SessionFormatException : Exception
{
    public SessionFormatException(string message) : base(message) { }
}
