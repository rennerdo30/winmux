using WinMux.Core.Model;

namespace WinMux.Core.Settings;

/// <summary>What kind of pane a profile opens.</summary>
public enum ProfileKind
{
    /// <summary>A shell in a terminal pane: ConPTY, VT, the WinMux-drawn grid.</summary>
    Terminal,

    /// <summary>A windowed application, hosted in a PaneHost (ADR 0001/0008).</summary>
    Application,

    /// <summary>A saved SSH host. Opens a terminal pane running the OpenSSH client.</summary>
    Ssh,

    /// <summary>A saved Remote Desktop host. Opens the Windows client in an application pane.</summary>
    Rdp,

    /// <summary>
    /// A saved SFTP host. Opens a file-browser pane onto the remote filesystem.
    ///
    /// SCP has no separate kind: it is a copy command with no directory listing, so there is nothing
    /// for a browser to show, and every server that speaks it speaks SFTP.
    /// </summary>
    Sftp,

    /// <summary>A saved FTP host. Opens a file-browser pane, over FTPS where the server allows it.</summary>
    Ftp,
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

    /// <summary>
    /// The remote host, for <see cref="ProfileKind.Ssh"/> and <see cref="ProfileKind.Rdp"/>.
    ///
    /// A connection is an ordinary profile rather than a separate list, so that adding one adds it
    /// to every surface that can open a pane at once (CLAUDE.md section 5a). These four fields are
    /// what a connection stores instead of a program; <see cref="RemoteConnection"/> turns them
    /// into one.
    /// </summary>
    public string Host { get; init; } = string.Empty;

    /// <summary>Zero means the protocol's default — 22 for SSH, 3389 for RDP.</summary>
    public int Port { get; init; }

    /// <summary>The remote user. Empty lets the client decide, which is usually right for RDP.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>An SSH private key file. Empty uses the agent and the default identities.</summary>
    public string Identity { get; init; } = string.Empty;

    /// <summary>
    /// The pane kind this profile opens.
    ///
    /// SSH is a terminal because that is what it is; RDP is a hosted window; SFTP and FTP are file
    /// browsers. Every connection kind chooses an existing provider rather than introducing a pane
    /// kind of its own, which is the rule in CLAUDE.md section 5a.
    /// </summary>
    public PaneKind PaneKind => Kind switch
    {
        ProfileKind.Terminal or ProfileKind.Ssh => Model.PaneKind.Terminal,
        ProfileKind.Sftp or ProfileKind.Ftp => Model.PaneKind.FileBrowser,
        _ => Model.PaneKind.ForeignApp,
    };

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
        // The connection fields belong here for the same reason Args does. They were left out when
        // connections were added, so changing a saved host's address compared equal to the old one
        // and the settings UI discarded the edit as "nothing changed".
        Host == other.Host &&
        Port == other.Port &&
        User == other.User &&
        Identity == other.Identity &&
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
        hash.Add(Host);
        hash.Add(Port);
        hash.Add(User);
        hash.Add(Identity);
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
