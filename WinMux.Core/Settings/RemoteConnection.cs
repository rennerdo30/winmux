namespace WinMux.Core.Settings;

/// <summary>What a connection profile actually runs.</summary>
/// <param name="Program">The client executable, resolved against PATH at launch.</param>
/// <param name="Args">Its arguments, already split — never a command line to be re-parsed.</param>
public readonly record struct ConnectionCommand(string Program, IReadOnlyList<string> Args);

/// <summary>
/// Turning a saved connection into a command line.
///
/// A connection is a <see cref="LaunchProfile"/> like any other, because CLAUDE.md section 5a is
/// explicit that one list feeds every surface: adding a connection has to add it to the New menu,
/// the palette, an empty pane's launcher and the CLI at once, and a parallel "connections" list
/// would have to be plumbed into all four separately and would drift.
///
/// So what a connection stores is a host, a port and a user, and this is the one place that knows
/// how those become `ssh` or `mstsc` arguments. Pure, and in Core, because argument construction is
/// where the quoting mistakes live and it is worth testing without launching anything.
/// </summary>
public static class RemoteConnection
{
    /// <summary>OpenSSH, shipped with Windows since 1809, so there is nothing to install.</summary>
    public const string SshProgram = "ssh.exe";

    /// <summary>The Remote Desktop client, shipped with Windows.</summary>
    public const string RdpProgram = "mstsc.exe";

    public const int DefaultSshPort = 22;
    public const int DefaultRdpPort = 3389;

    /// <summary>SFTP is carried over SSH, so it is port 22 and not a port of its own.</summary>
    public const int DefaultSftpPort = 22;

    public const int DefaultFtpPort = 21;

    /// <summary>The port a kind uses when the profile does not say.</summary>
    public static int DefaultPortFor(ProfileKind kind) => kind switch
    {
        ProfileKind.Ssh => DefaultSshPort,
        ProfileKind.Rdp => DefaultRdpPort,
        ProfileKind.Sftp => DefaultSftpPort,
        ProfileKind.Ftp => DefaultFtpPort,
        _ => 0,
    };

    /// <summary>Whether this kind describes a remote host rather than a local program.</summary>
    public static bool IsConnection(ProfileKind kind) =>
        kind is ProfileKind.Ssh or ProfileKind.Rdp or ProfileKind.Sftp or ProfileKind.Ftp;

    /// <summary>
    /// Whether this kind is answered by running a program.
    ///
    /// SSH and RDP delegate to a Windows client and so have a command line. SFTP and FTP do not —
    /// WinMux speaks those itself and opens a file-browser pane — so they are connections without a
    /// program, and <see cref="Resolve"/> has nothing to build for them.
    /// </summary>
    public static bool LaunchesProgram(ProfileKind kind) => kind is ProfileKind.Ssh or ProfileKind.Rdp;

    /// <summary>
    /// Build the command for a connection profile.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is not a connection, or names no host.</exception>
    public static ConnectionCommand Resolve(LaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (!LaunchesProgram(profile.Kind))
        {
            throw new ArgumentException(
                IsConnection(profile.Kind)
                    ? $"A {profile.Kind} connection opens a file-browser pane, not a program."
                    : $"{profile.Kind} is not a connection.",
                nameof(profile));
        }

        var host = profile.Host.Trim();
        if (host.Length == 0)
            throw new ArgumentException("A connection needs a host.", nameof(profile));

        var port = profile.Port > 0 ? profile.Port : DefaultPortFor(profile.Kind);

        return profile.Kind == ProfileKind.Ssh
            ? new ConnectionCommand(SshProgram, SshArgs(profile, host, port))
            : new ConnectionCommand(RdpProgram, RdpArgs(host, port));
    }

    private static List<string> SshArgs(LaunchProfile profile, string host, int port)
    {
        var args = new List<string>(8);

        // Only when it differs: an explicit -p 22 in every saved connection is noise, and it makes
        // the profile disagree with the ~/.ssh/config the user may already rely on.
        if (port != DefaultSshPort)
        {
            args.Add("-p");
            args.Add(port.ToString());
        }

        var identity = profile.Identity.Trim();
        if (identity.Length > 0)
        {
            args.Add("-i");
            args.Add(identity);
        }

        // Arguments are passed as a list, not a command line, so a host or key path containing a
        // space needs no quoting here — quoting it would put the quotes in the argument.
        var user = profile.User.Trim();
        args.Add(user.Length > 0 ? $"{user}@{host}" : host);

        // Anything the user added by hand comes last, so it can override what is built above.
        args.AddRange(profile.Args);
        return args;
    }

    private static List<string> RdpArgs(string host, int port)
    {
        // mstsc takes the port in the address rather than as a switch.
        var address = port != DefaultRdpPort ? $"{host}:{port}" : host;

        // Deliberately no username: mstsc has no switch for one — only a .rdp file — and
        // credentials belong in Windows Credential Manager, which mstsc already uses and which is
        // where the password would have to live anyway. A half-built credential store in a TOML
        // file would be worse than sending people to the one Windows provides.
        return [$"/v:{address}"];
    }

    /// <summary>
    /// What to show under the connection's name in a list: `user@host:port`, minus the parts that
    /// are the default.
    /// </summary>
    public static string Describe(LaunchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!IsConnection(profile.Kind)) return profile.Program;

        var host = profile.Host.Trim();
        if (host.Length == 0) return "(no host)";

        var user = profile.User.Trim();
        var prefix = user.Length > 0 ? user + "@" : "";
        var port = profile.Port > 0 ? profile.Port : DefaultPortFor(profile.Kind);
        var suffix = port != DefaultPortFor(profile.Kind) ? ":" + port : "";

        return prefix + host + suffix;
    }
}
