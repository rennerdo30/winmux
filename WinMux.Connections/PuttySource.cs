using System.Globalization;

namespace WinMux.Connections;

/// <summary>
/// PuTTY's saved sessions, from <c>HKCU\Software\SimonTatham\PuTTY\Sessions</c>.
///
/// <para>
/// A flat list — PuTTY has no folders, so everything lands directly under the root and inheritance
/// has nothing to do. That is not a gap in this source: a format that cannot express a hierarchy
/// must not invent one, and a user who wants their PuTTY hosts in folders can convert them into
/// WinMux's own connections and arrange them there (ADR 0025).
/// </para>
///
/// <para>
/// PuTTY stores <em>no</em> passwords, which makes it the one source with nothing to decrypt and
/// nothing to offer the credential store. It does keep a private key path, which is the part of a
/// PuTTY session people actually rely on.
/// </para>
///
/// <para>
/// Session names are escaped in the key name: a space is <c>%20</c>, and PuTTY's own escaping is
/// percent-encoding over the bytes of the name. A session called <c>web 01</c> is the key
/// <c>web%2001</c>, and a source that showed the raw key name would show that to the user.
/// </para>
/// </summary>
public sealed class PuttySource : IConnectionSource
{
    private const string SessionsKey = @"Software\SimonTatham\PuTTY\Sessions";

    private readonly IRegistryStore _registry;
    private readonly Dictionary<ConnectionNode, string> _keys = [];

    public PuttySource(IRegistryStore registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string DisplayName => "PuTTY";

    public string Location => @"HKCU\" + SessionsKey;

    public bool Exists => _registry.KeyExists(SessionsKey);

    /// <summary>
    /// PuTTY reads a session when it opens one and writes it when the user saves, so it holds
    /// nothing open and there is nothing to race with.
    /// </summary>
    public bool CanWriteWhileOtherToolRuns => true;

    public ConnectionFolder Read()
    {
        if (!Exists)
        {
            throw new ConnectionSourceException($"There are no PuTTY sessions under {Location}.");
        }

        _keys.Clear();
        var root = new ConnectionFolder("PuTTY");

        foreach (var key in _registry.SubKeyNames(SessionsKey).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var name = Unescape(key);

            // PuTTY's own defaults, which are a template rather than a host you can open.
            if (name.Equals("Default Settings", StringComparison.OrdinalIgnoreCase)) continue;

            var entry = new ConnectionEntry(name, SettingsOf($@"{SessionsKey}\{key}"));
            _keys[entry] = key;
            root.Add(entry);
        }

        return root;
    }

    public void Write(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var entry in root.Entries())
        {
            if (!_keys.TryGetValue(entry, out var key)) continue;

            var escaped = Escape(entry.Name);
            if (!string.Equals(escaped, key, StringComparison.Ordinal) &&
                _registry.RenameSubKey(SessionsKey, key, escaped))
            {
                _keys[entry] = key = escaped;
            }

            var path = $@"{SessionsKey}\{key}";
            if (entry.Settings.Host.TryGet(out var host)) _registry.WriteValue(path, "HostName", host);
            if (entry.Settings.User.TryGet(out var user)) _registry.WriteValue(path, "UserName", user);
            if (entry.Settings.Identity.TryGet(out var identity)) _registry.WriteValue(path, "PublicKeyFile", identity);
            if (entry.Settings.Port.TryGet(out var port))
            {
                _registry.WriteValue(path, "PortNumber", port.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Nothing. PuTTY has never stored a password, which is the right answer and not a gap.</summary>
    public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root) => [];

    private ConnectionSettings SettingsOf(string path)
    {
        var protocol = (_registry.ReadValue(path, "Protocol") ?? "ssh").Trim().ToLowerInvariant();
        var host = _registry.ReadValue(path, "HostName")?.Trim() ?? string.Empty;
        var user = _registry.ReadValue(path, "UserName")?.Trim();
        var identity = _registry.ReadValue(path, "PublicKeyFile")?.Trim();
        var proxy = _registry.ReadValue(path, "ProxyHost")?.Trim();

        return new ConnectionSettings
        {
            Protocol = Inherited<ConnectionProtocol>.Of(protocol switch
            {
                "ssh" => ConnectionProtocol.Ssh,
                "telnet" => ConnectionProtocol.Telnet,
                "serial" => ConnectionProtocol.Serial,
                _ => ConnectionProtocol.Unknown,
            }),
            Host = Inherited<string>.Of(host),
            Port = PortOf(_registry.ReadValue(path, "PortNumber")),
            User = string.IsNullOrEmpty(user) ? Inherited<string>.Inherit : Inherited<string>.Of(user),
            Identity = string.IsNullOrEmpty(identity) ? Inherited<string>.Inherit : Inherited<string>.Of(identity),
            Gateway = string.IsNullOrEmpty(proxy) ? Inherited<string>.Inherit : Inherited<string>.Of(proxy),
        };
    }

    /// <summary>PuTTY writes numbers as DWORDs; a store may hand them back in either notation.</summary>
    private static Inherited<int> PortOf(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return Inherited<int>.Inherit;

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex > 0 ? Inherited<int>.Of(hex) : Inherited<int>.Inherit;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;
    }

    /// <summary>
    /// PuTTY's key names are percent-encoded: <c>web%2001</c> is <c>web 01</c>. Showing the raw key
    /// would put the escaping in front of the user.
    /// </summary>
    internal static string Unescape(string key)
    {
        if (!key.Contains('%', StringComparison.Ordinal)) return key;

        var text = new System.Text.StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] == '%' && i + 2 < key.Length &&
                byte.TryParse(key.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                text.Append((char)value);
                i += 2;
                continue;
            }

            text.Append(key[i]);
        }

        return text.ToString();
    }

    /// <summary>The way back, so a session renamed in WinMux is a session PuTTY can still open.</summary>
    internal static string Escape(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.') text.Append(character);
            else text.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}
