using WinMux.Core.Model;

namespace WinMux.Core.Settings;

/// <summary>What kind of pane a profile opens.</summary>
public enum ProfileKind
{
    /// <summary>A shell in a terminal pane: ConPTY, VT, the WinMux-drawn grid.</summary>
    Terminal,

    /// <summary>A windowed application, hosted in a PaneHost (ADR 0001/0008).</summary>
    Application,
}

/// <summary>
/// A named thing the user can put in a pane.
///
/// One concept covers terminals and applications because, to the layout, they are the same: a
/// program, its arguments, a working directory, and — for a windowed application — how to find and
/// host its window. CLAUDE.md section 5a: this is the extension point users actually touch, so it
/// is user data rather than a compiled-in list.
/// </summary>
public sealed record LaunchProfile
{
    /// <summary>Stable identifier, used by settings and the CLI. Never shown to the user.</summary>
    public required string Id { get; init; }

    /// <summary>What the user sees in every menu that can open a pane.</summary>
    public required string Name { get; init; }

    public ProfileKind Kind { get; init; } = ProfileKind.Terminal;

    /// <summary>Absolute path where possible; a bare name is resolved against PATH at launch.</summary>
    public required string Program { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Empty means "inherit from the focused pane", which is almost always what is wanted.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>
    /// How to host the window, for <see cref="ProfileKind.Application"/>. Auto lets the quirks
    /// database decide (ADR 0003), which is right for anything the database already knows.
    /// </summary>
    public HostStrategy Strategy { get; init; } = HostStrategy.Auto;

    /// <summary>
    /// Window class to match on, for an application whose process is not the one that owns the
    /// window — `explorer.exe` being the standing example (ADR 0011).
    /// </summary>
    public string WindowClass { get; init; } = string.Empty;

    /// <summary>Optional title substring, for an application that shows several windows.</summary>
    public string TitleContains { get; init; } = string.Empty;

    /// <summary>Where this came from, so the UI can say so. Not used for matching.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The pane kind this profile opens.</summary>
    public PaneKind PaneKind => Kind == ProfileKind.Terminal ? Model.PaneKind.Terminal : Model.PaneKind.ForeignApp;

    /// <summary>
    /// Value equality including <see cref="Args"/>.
    ///
    /// A record compares a list member by reference, so two profiles differing only in their
    /// arguments compared equal — and two identical ones read from disk compared different. The
    /// settings UI asks "did this change?" to decide whether to write the file, so the generated
    /// behaviour would have silently dropped edits and written files nobody changed.
    /// </summary>
    public bool Equals(LaunchProfile? other) =>
        other is not null &&
        Id == other.Id &&
        Name == other.Name &&
        Kind == other.Kind &&
        Program == other.Program &&
        WorkingDirectory == other.WorkingDirectory &&
        Strategy == other.Strategy &&
        WindowClass == other.WindowClass &&
        TitleContains == other.TitleContains &&
        Source == other.Source &&
        Args.SequenceEqual(other.Args, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(Kind);
        hash.Add(Program);
        hash.Add(WorkingDirectory);
        hash.Add(Strategy);
        hash.Add(WindowClass);
        hash.Add(TitleContains);
        hash.Add(Source);
        foreach (var argument in Args) hash.Add(argument, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// The profiles seeded on first run: the shells Windows ships with, plus File Explorer as the
    /// worked example of an application profile. All of them are ordinary, editable, deletable
    /// entries — nothing here is special-cased anywhere else in the product.
    /// </summary>
    public static IReadOnlyList<LaunchProfile> Defaults { get; } =
    [
        new()
        {
            Id = "cmd",
            Name = "Command Prompt",
            Kind = ProfileKind.Terminal,
            Program = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            Source = "built in",
        },
        new()
        {
            Id = "windows-powershell",
            Name = "Windows PowerShell",
            Kind = ProfileKind.Terminal,
            Program = "powershell.exe",
            Args = ["-NoLogo"],
            Source = "built in",
        },
        new()
        {
            Id = "powershell",
            Name = "PowerShell 7",
            Kind = ProfileKind.Terminal,
            Program = "pwsh.exe",
            Args = ["-NoLogo"],
            Source = "built in",
        },
        new()
        {
            Id = "wsl",
            Name = "WSL",
            Kind = ProfileKind.Terminal,
            Program = "wsl.exe",
            Source = "built in",
        },
        new()
        {
            Id = "explorer",
            Name = "File Explorer",
            Kind = ProfileKind.Application,
            Program = "explorer.exe",
            // explorer.exe exits immediately and the window belongs to the running shell process,
            // so the window is found by class rather than by the launched pid (ADR 0003).
            WindowClass = "CabinetWClass",
            Strategy = HostStrategy.Embed,
            Source = "built in",
        },
    ];

    /// <summary>
    /// Turn a name into an id that is safe in a TOML key and stable across renames of the display
    /// name. Collisions are the caller's to resolve — see <c>ProfileList.WithUniqueId</c>.
    /// </summary>
    public static string MakeId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var id = new string(name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray())
            .Trim('-');

        while (id.Contains("--", StringComparison.Ordinal)) id = id.Replace("--", "-", StringComparison.Ordinal);
        return id.Length == 0 ? "profile" : id;
    }
}
