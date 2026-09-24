using WinMux.Connections;
using WinMux.Core.Settings;

namespace WinMux.Shell.Connections;

/// <summary>One tool's connections, as read — or the reason they could not be.</summary>
/// <param name="Source">Which tool.</param>
/// <param name="Root">Its tree, or null when reading failed.</param>
/// <param name="Problem">Why, in words for the user, or null when it worked.</param>
public sealed record ConnectionSourceResult(IConnectionSource Source, ConnectionFolder? Root, string? Problem)
{
    public bool Succeeded => Root is not null;
}

/// <summary>
/// The saved connections of every tool on this machine.
///
/// <para>
/// A tool that is not installed is not an error and not a row: the list shows what is there. A tool
/// that <em>is</em> there and cannot be read is both — because "you have no PuTTY sessions" and
/// "your PuTTY sessions could not be read" must never look the same, which is the rule the sources
/// themselves follow (ADR 0025) and would be undone by a catalogue that swallowed it.
/// </para>
/// </summary>
public sealed class ConnectionCatalog
{
    private readonly IReadOnlyList<IConnectionSource> _sources;

    public ConnectionCatalog(IReadOnlyList<IConnectionSource> sources) =>
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));

    /// <summary>The sources WinMux knows how to find without being told where to look.</summary>
    public static ConnectionCatalog Standard(IRegistryStore registry, Unprotect? unprotect = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // RDCMan is absent on purpose: an .rdg file lives wherever its owner put it, so it is opened
        // by name rather than discovered. Everything else has one usual place.
        return new ConnectionCatalog(
        [
            new PuttySource(registry),
            new FileZillaSource(),
            new WinScpSource(),
            new MobaXtermSource(),
            new MRemoteNgSource(),
        ]);
    }

    /// <summary>Every source that is present here, read.</summary>
    public IReadOnlyList<ConnectionSourceResult> Read()
    {
        var results = new List<ConnectionSourceResult>();
        foreach (var source in _sources)
        {
            if (!source.Exists) continue;

            try
            {
                results.Add(new ConnectionSourceResult(source, source.Read(), null));
            }
            catch (ConnectionSourceException exception)
            {
                results.Add(new ConnectionSourceResult(source, null, exception.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// Turn a connection into something that can be opened in a pane.
    ///
    /// <para>
    /// The settings are <em>resolved</em> first, so a host that inherits its username from a folder
    /// arrives with that username — which is the entire reason the tree carries inheritance rather
    /// than the import flattening it (ADR 0025).
    /// </para>
    /// </summary>
    /// <returns>The profile, or null when the connection has no host or no protocol WinMux opens.</returns>
    public static LaunchProfile? ToProfile(ConnectionEntry entry, string idPrefix = "")
    {
        ArgumentNullException.ThrowIfNull(entry);

        var host = ConnectionResolver.Host(entry).Value;
        if (string.IsNullOrWhiteSpace(host)) return null;

        var kind = ConnectionResolver.Protocol(entry).Value switch
        {
            ConnectionProtocol.Ssh => ProfileKind.Ssh,
            ConnectionProtocol.Sftp => ProfileKind.Sftp,
            ConnectionProtocol.Ftp => ProfileKind.Ftp,
            ConnectionProtocol.Rdp => ProfileKind.Rdp,
            _ => (ProfileKind?)null,
        };

        if (kind is not { } profileKind) return null;

        var user = ConnectionResolver.User(entry).Value ?? string.Empty;
        var domain = ConnectionResolver.Domain(entry).Value;

        // A Windows domain belongs in front of the user name, which is how every RDP client takes
        // it and how the credential was stored in the first place.
        if (!string.IsNullOrWhiteSpace(domain) && !user.Contains('\\', StringComparison.Ordinal))
        {
            user = string.IsNullOrEmpty(user) ? user : $"{domain}\\{user}";
        }

        return new LaunchProfile
        {
            Id = LaunchProfile.MakeId(idPrefix + entry.Name),
            Name = entry.Name,
            Kind = profileKind,
            Program = string.Empty,
            Host = host,
            Port = ConnectionResolver.Port(entry).Value,
            User = user,
            Identity = ConnectionResolver.Identity(entry).Value ?? string.Empty,
            Source = entry.Path,
        };
    }
}
