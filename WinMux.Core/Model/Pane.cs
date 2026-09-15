namespace WinMux.Core.Model;

/// <summary>
/// Stable identifier for the provider that owns a pane.
///
/// Identifiers are case-normalized ASCII strings so providers can be registered outside
/// WinMux.Core without extending a closed enum. They begin with a letter, contain letters or
/// digits, and may use single <c>.</c>, <c>-</c>, or <c>_</c> separators between segments.
/// </summary>
public readonly struct PaneKind : IEquatable<PaneKind>, IComparable<PaneKind>, IComparable
{
    private const int MaximumLength = 128;
    private readonly string? _value;

    public static PaneKind Terminal { get; } = new("terminal");
    public static PaneKind FileBrowser { get; } = new("file-browser");
    public static PaneKind ForeignApp { get; } = new("foreign-app");

    /// <summary>
    /// A pane with nothing in it yet, showing a launcher. A first-class state rather than a gap:
    /// "make a pane, then decide" is a supported workflow (CLAUDE.md section 5a), and it is also
    /// what an adopted pane falls back to when the window it held cannot be restored.
    /// </summary>
    public static PaneKind Empty { get; } = new("empty");

    /// <summary>The normalized provider identifier.</summary>
    /// <exception cref="InvalidOperationException">The value is an uninitialized default.</exception>
    public string Value => _value
        ?? throw new InvalidOperationException("The default PaneKind value is not a valid provider identifier.");

    /// <summary>Whether this value was constructed from a valid provider identifier.</summary>
    public bool IsValid => _value is not null;

    /// <summary>Creates a built-in or third-party provider identifier.</summary>
    public PaneKind(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaximumLength || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw InvalidIdentifier(value);

        var normalized = value.ToLowerInvariant();
        if (!IsStableIdentifier(normalized))
            throw InvalidIdentifier(value);

        _value = normalized;
    }

    /// <summary>Creates a built-in or third-party provider identifier.</summary>
    public static PaneKind Create(string value) => new(value);

    /// <summary>Attempts to create a provider identifier without throwing.</summary>
    public static bool TryCreate(string? value, out PaneKind kind)
    {
        if (value is not null)
        {
            try
            {
                kind = new PaneKind(value);
                return true;
            }
            catch (ArgumentException)
            {
                // The validation contract is represented by the false return below.
            }
        }

        kind = default;
        return false;
    }

    public bool Equals(PaneKind other) =>
        string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PaneKind other && Equals(other);

    public override int GetHashCode() => _value is null
        ? 0
        : StringComparer.Ordinal.GetHashCode(_value);

    public int CompareTo(PaneKind other) =>
        StringComparer.Ordinal.Compare(_value, other._value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        PaneKind other => CompareTo(other),
        _ => throw new ArgumentException($"Object must be a {nameof(PaneKind)}.", nameof(obj)),
    };

    public override string ToString() => _value ?? string.Empty;

    public static bool operator ==(PaneKind left, PaneKind right) => left.Equals(right);
    public static bool operator !=(PaneKind left, PaneKind right) => !left.Equals(right);
    public static bool operator <(PaneKind left, PaneKind right) => left.CompareTo(right) < 0;
    public static bool operator <=(PaneKind left, PaneKind right) => left.CompareTo(right) <= 0;
    public static bool operator >(PaneKind left, PaneKind right) => left.CompareTo(right) > 0;
    public static bool operator >=(PaneKind left, PaneKind right) => left.CompareTo(right) >= 0;

    private static bool IsStableIdentifier(string value)
    {
        if (!IsAsciiLetter(value[0])) return false;

        var previousWasSeparator = false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (IsAsciiLetter(c) || char.IsAsciiDigit(c))
            {
                previousWasSeparator = false;
                continue;
            }

            if (c is not ('.' or '-' or '_') || previousWasSeparator || i == value.Length - 1)
                return false;
            previousWasSeparator = true;
        }

        return true;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z';

    private static ArgumentException InvalidIdentifier(string value) => new(
        $"Pane kind \"{value}\" is not a stable provider identifier. Use 1-{MaximumLength} " +
        "ASCII characters: start with a letter, then letters or digits with single '.', '-', " +
        "or '_' separators.",
        nameof(value));
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
        ArgumentNullException.ThrowIfNull(restore);
        if (!kind.IsValid) throw new ArgumentException("A pane kind must be a valid provider identifier.", nameof(kind));
        if (restore.Kind != kind)
            throw new ArgumentException(
                $"Pane kind '{kind}' does not match restore descriptor kind '{restore.Kind}'.",
                nameof(restore));
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

    /// <summary>A pane with nothing in it yet.</summary>
    public static Pane Empty(string title = "empty pane") =>
        new(PaneId.New(), PaneKind.Empty, title, new RestoreDescriptor { Kind = PaneKind.Empty, Title = title });

    public static Pane FileBrowser(string directory, string title = "files", string? selectedPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return new Pane(PaneId.New(), PaneKind.FileBrowser, title, new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = title,
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["current_directory"] = directory,
                ["selected_path"] = selectedPath ?? string.Empty,
            },
        });
    }

    public override string ToString() => $"{Kind}:{Id} \"{Title}\"";
}
