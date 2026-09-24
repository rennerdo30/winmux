namespace WinMux.Core.Connections;

/// <summary>How a connection is made. The vocabulary the foreign tools share.</summary>
public enum ConnectionProtocol
{
    Unknown = 0,
    Ssh,
    Sftp,
    Ftp,
    Rdp,
    Telnet,
    Serial,
}

/// <summary>
/// The settings of one connection, each stated here or inherited from a folder above.
///
/// <para>
/// Deliberately the union of what the tools in ADR 0025 agree on, rather than any one of their
/// schemas. Anything a single tool knows and the others do not stays in that tool's own document
/// and is written back untouched — a parser that keeps only the fields it understands would destroy
/// the rest of somebody's configuration on the first save.
/// </para>
/// </summary>
public sealed record ConnectionSettings
{
    public Inherited<ConnectionProtocol> Protocol { get; init; }
    public Inherited<string> Host { get; init; }
    public Inherited<int> Port { get; init; }
    public Inherited<string> User { get; init; }
    public Inherited<string> Domain { get; init; }

    /// <summary>The name of a credential in the OS store, never a password. See ADR 0025.</summary>
    public Inherited<string> CredentialKey { get; init; }

    /// <summary>A private key file, for the protocols that use one.</summary>
    public Inherited<string> Identity { get; init; }

    /// <summary>An SSH jump host or an RDP gateway, by name.</summary>
    public Inherited<string> Gateway { get; init; }

    /// <summary>The directory to open in, for the file-transfer protocols.</summary>
    public Inherited<string> RemoteDirectory { get; init; }

    /// <summary>A command to run once connected.</summary>
    public Inherited<string> Command { get; init; }

    /// <summary>Nothing stated at all — what a folder that only groups things looks like.</summary>
    public static ConnectionSettings Nothing { get; } = new();
}
