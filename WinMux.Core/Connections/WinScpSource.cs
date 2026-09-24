using System.Globalization;
using System.Text;

namespace WinMux.Core.Connections;

/// <summary>
/// WinSCP's saved sessions, from <c>WinSCP.ini</c> or from the registry.
///
/// <para>
/// WinSCP puts a session's folder in its <em>name</em>: a section called
/// <c>Sessions\Production/web-01</c> is the session <c>web-01</c> in the folder <c>Production</c>.
/// So this is the first source with a hierarchy that has to be built rather than read, and folders
/// here carry no settings of their own — WinSCP has no notion of inheriting anything, so the
/// folders group and nothing more. A source must not invent inheritance a format does not have.
/// </para>
///
/// <para>
/// Passwords are obfuscated rather than encrypted: a run-length of nibbles XORed against a
/// constant and the session's own name. There is no key and no secret, so this is decoding rather
/// than decryption — and worth saying plainly, because "WinSCP stores my passwords safely" is a
/// thing people believe. Anything with a master password set is not stored this way and is left
/// alone.
/// </para>
/// </summary>
public sealed class WinScpSource : IConnectionSource
{
    private const string SessionPrefix = @"Sessions\";

    private readonly string _path;
    private readonly Dictionary<ConnectionNode, string> _sections = [];
    private IniDocument? _document;

    public WinScpSource(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>
    /// The portable file, beside WinSCP.exe. An installed WinSCP uses the registry instead, which
    /// is the same sections under <c>HKCU\Software\Martin Prikryl\WinSCP 2</c>.
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WinSCP.ini");

    public string DisplayName => "WinSCP";

    public string Location => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>WinSCP writes its ini when it closes, and would overwrite anything saved under it.</summary>
    public bool CanWriteWhileOtherToolRuns => false;

    public ConnectionFolder Read()
    {
        if (!Exists) throw new ConnectionSourceException($"There is no WinSCP configuration at {_path}.");

        _document = IniDocument.Load(_path);
        _sections.Clear();

        var sessions = _document.Sections
            .Where(name => name.StartsWith(SessionPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (sessions.Length == 0 && !_document.Sections.Any(name =>
                name.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Configuration\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConnectionSourceException(
                $"{_path} has no Sessions or Configuration section, so it is not a WinSCP configuration.");
        }

        var root = new ConnectionFolder("WinSCP");
        foreach (var section in sessions)
        {
            // The part after "Sessions\" is a path: folders separated by "/", then the name. Both
            // are URL-escaped, because a session name may contain either separator.
            var parts = section[SessionPrefix.Length..]
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Unescape)
                .ToArray();
            if (parts.Length == 0) continue;

            var parent = root;
            for (var i = 0; i < parts.Length - 1; i++) parent = FolderIn(parent, parts[i]);

            var entry = new ConnectionEntry(parts[^1], SettingsOf(section));
            _sections[entry] = section;
            parent.Add(entry);
        }

        return root;
    }

    public void Write(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var document = _document
            ?? throw new ConnectionSourceException("Read the WinSCP sessions before writing them back.");

        foreach (var entry in root.Entries())
        {
            if (!_sections.TryGetValue(entry, out var section)) continue;

            var wanted = SessionPrefix + string.Join('/', entry.UpToRoot().Reverse().Skip(1).Select(node => Escape(node.Name)));
            if (!string.Equals(wanted, section, StringComparison.Ordinal) &&
                document.RenameSection(section, wanted))
            {
                _sections[entry] = section = wanted;
            }

            if (entry.Settings.Host.TryGet(out var host)) document.Write(section, "HostName", host);
            if (entry.Settings.User.TryGet(out var user)) document.Write(section, "UserName", user);
            if (entry.Settings.RemoteDirectory.TryGet(out var directory))
            {
                document.Write(section, "RemoteDirectory", directory);
            }

            if (entry.Settings.Port.TryGet(out var port))
            {
                document.Write(section, "PortNumber", port.ToString(CultureInfo.InvariantCulture));
            }
        }

        Backup();
        document.Save(_path);
    }

    public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_document is null) return [];

        var found = new List<FoundCredential>();
        foreach (var entry in root.Entries())
        {
            if (!_sections.TryGetValue(entry, out var section)) continue;

            var stored = _document.Read(section, "Password");
            if (string.IsNullOrEmpty(stored)) continue;

            var host = _document.Read(section, "HostName") ?? string.Empty;
            var user = _document.Read(section, "UserName") ?? string.Empty;
            if (Decode(stored, user + host) is { Length: > 0 } secret)
            {
                found.Add(new FoundCredential(entry, secret));
            }
        }

        return found;
    }

    private static ConnectionFolder FolderIn(ConnectionFolder parent, string name)
    {
        var existing = parent.Children.OfType<ConnectionFolder>()
            .FirstOrDefault(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        // No settings: WinSCP folders group sessions and state nothing, so this source must not
        // pretend otherwise.
        var folder = new ConnectionFolder(name);
        parent.Add(folder);
        return folder;
    }

    private ConnectionSettings SettingsOf(string section)
    {
        var values = _document!.ReadAll(section);
        var user = Value(values, "UserName");
        var directory = Value(values, "RemoteDirectory");
        var identity = Value(values, "PublicKeyFile");

        return new ConnectionSettings
        {
            Protocol = Inherited<ConnectionProtocol>.Of(ProtocolOf(Value(values, "FSProtocol"))),
            Host = Inherited<string>.Of(Value(values, "HostName") ?? string.Empty),
            Port = PortOf(Value(values, "PortNumber")),
            User = user is null ? Inherited<string>.Inherit : Inherited<string>.Of(user),
            RemoteDirectory = directory is null ? Inherited<string>.Inherit : Inherited<string>.Of(directory),
            Identity = identity is null ? Inherited<string>.Inherit : Inherited<string>.Of(Unescape(identity)),
        };
    }

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// WinSCP's protocol numbers. 0 is SCP and 5 is SFTP — both are SSH file transfer and WinMux
    /// opens either with its SFTP pane; 2 is FTP, and 3 is WebDAV, which WinMux does not speak.
    /// </summary>
    private static ConnectionProtocol ProtocolOf(string? value) => (value ?? "0").Trim() switch
    {
        "2" => ConnectionProtocol.Ftp,
        "3" => ConnectionProtocol.Unknown,
        _ => ConnectionProtocol.Sftp,
    };

    private static Inherited<int> PortOf(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;

    /// <summary>
    /// WinSCP's stored password: nibbles, XORed against a constant, with the user and host prefixed
    /// so the same password reads differently per session. Obfuscation rather than encryption —
    /// there is no key, so anyone with the file has the password.
    /// </summary>
    internal static string? Decode(string stored, string salt)
    {
        const int Magic = 0xA3;
        const int Flagged = 0xFF;

        var bytes = new Queue<int>();
        foreach (var character in stored)
        {
            if (!Uri.IsHexDigit(character)) return null;
            bytes.Enqueue(Convert.ToInt32(character.ToString(), 16));
        }

        int Next()
        {
            if (bytes.Count < 2) return -1;
            var value = (bytes.Dequeue() << 4) + bytes.Dequeue();
            return ~(value ^ Magic) & 0xFF;
        }

        var flag = Next();
        if (flag < 0) return null;

        // The flag byte doubles as the length in the format WinSCP used before it prefixed the
        // session's own user and host.
        var length = flag == Flagged ? Next() : flag;
        if (length < 0) return null;

        // Then a shift: that many bytes of padding, so the same password does not encode to the
        // same text twice.
        var shift = Next();
        if (shift < 0) return null;
        for (var i = 0; i < shift; i++)
        {
            if (Next() < 0) return null;
        }

        var text = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var value = Next();
            if (value < 0) return null;
            text.Append((char)value);
        }

        var decoded = text.ToString();
        if (flag != Flagged) return decoded;

        // The newer format prefixes the user and host, which is what ties a stored password to one
        // session. A decode that does not find them has misread the bytes rather than decoded them.
        return decoded.StartsWith(salt, StringComparison.Ordinal) ? decoded[salt.Length..] : null;
    }

    /// <summary>WinSCP escapes a name's separators as <c>%xx</c>, the same as PuTTY does.</summary>
    internal static string Unescape(string text) => PuttySource.Unescape(text);

    internal static string Escape(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is '/' or '\\' or '%' or '[' or ']')
            {
                text.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(character);
            }
        }

        return text.ToString();
    }

    private void Backup()
    {
        var backup = _path + ".winmux-backup";
        if (File.Exists(backup) || !File.Exists(_path)) return;

        try
        {
            File.Copy(_path, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionSourceException(
                $"{_path} was not written, because a backup could not be made first: {exception.Message}",
                exception);
        }
    }
}
