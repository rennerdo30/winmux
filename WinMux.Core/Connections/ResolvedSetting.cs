namespace WinMux.Core.Connections;

/// <summary>
/// A setting's effective value and the node that supplied it.
///
/// <para>
/// The second half is not a nicety. A user looking at a host that connects as the wrong user needs
/// to know <em>which folder</em> says so, because that is the thing they have to change; a settings
/// page that shows the value and not its origin sends them hunting through a tree. mRemoteNG and
/// RDCMan both show it, and it is most of why their inheritance is usable.
/// </para>
/// </summary>
/// <param name="Value">The effective value.</param>
/// <param name="From">The node that stated it, or null when nothing in the tree did.</param>
public readonly record struct ResolvedSetting<T>(T? Value, ConnectionNode? From)
{
    /// <summary>Whether any node stated this setting.</summary>
    public bool IsSet => From is not null;

    /// <summary>Whether the node asked stated it itself, rather than taking it from a folder.</summary>
    public bool IsOwn(ConnectionNode node) => ReferenceEquals(From, node);

    /// <summary>"CORP (from Production)", or just the value when the node states it itself.</summary>
    public string Describe(ConnectionNode asked) =>
        From is null ? "(not set)"
        : IsOwn(asked) ? $"{Value}"
        : $"{Value} (from {From.Name})";
}

/// <summary>Resolving a node's settings against the folders above it.</summary>
public static class ConnectionResolver
{
    public static ResolvedSetting<ConnectionProtocol> Protocol(ConnectionNode node) =>
        Resolve(node, settings => settings.Protocol);

    public static ResolvedSetting<string> Host(ConnectionNode node) =>
        Resolve(node, settings => settings.Host);

    public static ResolvedSetting<int> Port(ConnectionNode node) =>
        Resolve(node, settings => settings.Port);

    public static ResolvedSetting<string> User(ConnectionNode node) =>
        Resolve(node, settings => settings.User);

    public static ResolvedSetting<string> Domain(ConnectionNode node) =>
        Resolve(node, settings => settings.Domain);

    public static ResolvedSetting<string> CredentialKey(ConnectionNode node) =>
        Resolve(node, settings => settings.CredentialKey);

    public static ResolvedSetting<string> Identity(ConnectionNode node) =>
        Resolve(node, settings => settings.Identity);

    public static ResolvedSetting<string> Gateway(ConnectionNode node) =>
        Resolve(node, settings => settings.Gateway);

    public static ResolvedSetting<string> RemoteDirectory(ConnectionNode node) =>
        Resolve(node, settings => settings.RemoteDirectory);

    public static ResolvedSetting<string> Command(ConnectionNode node) =>
        Resolve(node, settings => settings.Command);

    /// <summary>
    /// The nearest node that states <paramref name="select"/>, walking up from
    /// <paramref name="node"/>. Nearest wins, which is what every tool in ADR 0025 does and what
    /// makes "override this one host" possible at all.
    /// </summary>
    public static ResolvedSetting<T> Resolve<T>(
        ConnectionNode node,
        Func<ConnectionSettings, Inherited<T>> select)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(select);

        foreach (var current in node.UpToRoot())
        {
            if (select(current.Settings).TryGet(out var value)) return new ResolvedSetting<T>(value, current);
        }

        return new ResolvedSetting<T>(default, null);
    }
}
