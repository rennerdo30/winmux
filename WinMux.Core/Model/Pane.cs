namespace WinMux.Core.Model;

public enum PaneKind
{
    Terminal,
    FileBrowser,
    ForeignApp,
}

/// <summary>How a foreign app is hosted. Measured per app in spike 2; see ADR 0003.</summary>
public enum HostStrategy
{
    /// <summary>
    /// Select a measured per-application strategy from the quirks database, falling back to
    /// embed for applications that have not been measured.
    /// </summary>
    Auto,

    /// <summary>Reparent the app's window into the pane host. True containment.</summary>
    Embed,

    /// <summary>
    /// Leave the window top-level and drive its position to follow the pane rect.
    /// A *compatibility* fallback, not a stability one — ADR 0001 measured a wedged app stalling
    /// the host equally in both modes unless window calls are kept off the UI thread.
    /// </summary>
    Attach,
}

public readonly record struct PaneId(Guid Value)
{
    public static PaneId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N")[..8];
}

/// <summary>
/// The persisted per-pane payload (CLAUDE.md section 4).
///
/// Restore recreates a process from this descriptor. It does NOT restore process state — no
/// scrollback beyond a capped buffer, no in-flight jobs, no TUI state. That is a stated non-goal
/// and the UI must say so plainly rather than let users infer otherwise.
/// </summary>
public sealed record RestoreDescriptor
{
    public required PaneKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>Resolved absolute path. Resolving at save time survives a changed PATH.</summary>
    public string? Program { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> EnvOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The single most important field (CLAUDE.md section 4), with its provenance.</summary>
    public WorkingDirectory Cwd { get; init; } = WorkingDirectory.None;

    /// <summary>Automatic, embed, or attach hosting for foreign-app panes.</summary>
    public HostStrategy Strategy { get; init; } = HostStrategy.Embed;

    /// <summary>
    /// Kind-specific extras: file browser current directory and selection, foreign-app match rules.
    /// Deliberately open so a new pane kind needs no change to the tree or the session model.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A pane in the layout. The tree knows only this much — everything about *running* a pane lives
/// behind the pane-provider interface, so adding a fourth kind does not touch the tree code.
/// </summary>
public sealed class Pane
{
    public PaneId Id { get; }
    public PaneKind Kind { get; }
    public string Title { get; set; }
    public RestoreDescriptor Restore { get; set; }

    public Pane(PaneId id, PaneKind kind, string title, RestoreDescriptor restore)
    {
        Id = id;
        Kind = kind;
        Title = title ?? string.Empty;
        Restore = restore;
    }

    public static Pane Terminal(string title = "terminal", string? program = null, WorkingDirectory? cwd = null) =>
        new(PaneId.New(), PaneKind.Terminal, title, new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = title,
            Program = program,
            Cwd = cwd ?? WorkingDirectory.None,
        });

    public override string ToString() => $"{Kind}:{Id} \"{Title}\"";
}
