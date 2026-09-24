using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;

namespace WinMux.Connections;

/// <summary>
/// FileZilla's Site Manager, from <c>%APPDATA%\FileZilla\sitemanager.xml</c>.
///
/// <para>
/// The first source, chosen because it exercises every part of the contract without any of the
/// obstacles: nested folders, a password store, plain XML in a file rather than the registry, and a
/// schema full of settings WinMux has no opinion about. ADR 0025 has the order of the rest.
/// </para>
///
/// <para>
/// The document is kept and edited rather than regenerated. FileZilla writes a dozen settings per
/// site — transfer mode, timezone offset, encoding, key file, per-site bookmarks — and a writer that
/// emitted only the fields below would silently delete all of it the first time somebody renamed a
/// server in WinMux.
/// </para>
/// </summary>
public sealed class FileZillaSource : IConnectionSource
{
    /// <summary>
    /// Which element each node came from, so a write edits the original rather than building a new
    /// one. Weak, so a tree the user has closed does not hold its document in memory.
    /// </summary>
    private static readonly ConditionalWeakTable<ConnectionNode, XElement> Links = new();

    private readonly string _path;
    private XDocument? _document;

    public FileZillaSource(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>Where FileZilla keeps it, which is the same place on every Windows install.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FileZilla",
        "sitemanager.xml");

    public string DisplayName => "FileZilla";

    public string Location => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>
    /// FileZilla reads the file when the Site Manager is opened and writes the whole of it when
    /// that dialog is closed, so a change made underneath a running FileZilla is lost the moment
    /// the user opens their site list.
    /// </summary>
    public bool CanWriteWhileOtherToolRuns => false;

    public ConnectionFolder Read()
    {
        _document = Load();

        var servers = _document.Root?.Element("Servers")
            ?? throw new ConnectionSourceException(
                $"{_path} has no <Servers> element, so it is not a FileZilla site manager file.");

        var root = new ConnectionFolder("FileZilla");
        ReadInto(root, servers);
        return root;
    }

    public void Write(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var document = _document
            ?? throw new ConnectionSourceException("Read the FileZilla sites before writing them back.");

        // Edited on the very elements that were read, so every sibling setting survives untouched.
        foreach (var node in root.Folders().Cast<ConnectionNode>().Concat(root.Entries()))
        {
            if (node.Parent is null) continue;
            if (Element(node) is { } element) Apply(node, element);
        }

        Backup();

        try
        {
            using var stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None);
            document.Save(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionSourceException($"{_path} could not be written: {exception.Message}", exception);
        }
    }

    public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var found = new List<FoundCredential>();
        foreach (var entry in root.Entries())
        {
            if (Element(entry) is not { } element) continue;
            if (Decode(element.Element("Pass")) is { Length: > 0 } secret)
            {
                found.Add(new FoundCredential(entry, secret));
            }
        }

        return found;
    }

    private XDocument Load()
    {
        if (!File.Exists(_path))
        {
            throw new ConnectionSourceException($"There is no FileZilla site manager at {_path}.");
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or System.Xml.XmlException)
        {
            throw new ConnectionSourceException($"{_path} could not be read: {exception.Message}", exception);
        }
    }

    private static void ReadInto(ConnectionFolder parent, XElement element)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Folder":
                    // A folder's name is its own text, mixed in among its child elements.
                    var folder = new ConnectionFolder(OwnText(child));
                    Links.AddOrUpdate(folder, child);
                    parent.Add(folder);
                    ReadInto(folder, child);
                    break;

                case "Server":
                    var entry = new ConnectionEntry(
                        child.Element("Name")?.Value.Trim() is { Length: > 0 } named ? named : OwnText(child),
                        SettingsOf(child));
                    Links.AddOrUpdate(entry, child);
                    parent.Add(entry);
                    break;
            }
        }
    }

    private static ConnectionSettings SettingsOf(XElement server) => new()
    {
        Protocol = Inherited<ConnectionProtocol>.Of(ProtocolOf(server.Element("Protocol")?.Value)),
        Host = Inherited<string>.Of(server.Element("Host")?.Value.Trim() ?? string.Empty),
        Port = PortOf(server.Element("Port")?.Value),
        User = Inherited<string>.Of(server.Element("User")?.Value.Trim() ?? string.Empty),
        RemoteDirectory = DirectoryOf(server.Element("RemoteDir")?.Value),
        Identity = IdentityOf(server.Element("Keyfile")?.Value),
    };

    /// <summary>
    /// FileZilla's protocol numbers: 1 is SFTP, and everything else is FTP in one of its several
    /// TLS spellings, which WinMux has no separate pane kind for.
    /// </summary>
    private static ConnectionProtocol ProtocolOf(string? value) =>
        (value ?? "0").Trim() == "1" ? ConnectionProtocol.Sftp : ConnectionProtocol.Ftp;

    private static Inherited<int> PortOf(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;

    /// <summary>
    /// FileZilla writes a remote path as a length-prefixed list — <c>1 0 5 files 3 log</c> is
    /// <c>/files/log</c>. Anything that does not look like one is passed through rather than
    /// guessed at.
    /// </summary>
    private static Inherited<string> DirectoryOf(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return Inherited<string>.Inherit;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return Inherited<string>.Of(text);
        }

        var segments = new List<string>();
        for (var i = 2; i + 1 < parts.Length; i += 2) segments.Add(parts[i + 1]);

        return Inherited<string>.Of(segments.Count == 0 ? "/" : "/" + string.Join("/", segments));
    }

    private static Inherited<string> IdentityOf(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Inherited<string>.Inherit : Inherited<string>.Of(value.Trim());

    /// <summary>A password FileZilla stored: base64 when it says so, in the clear otherwise.</summary>
    private static string? Decode(XElement? pass)
    {
        if (pass is null) return null;

        var text = pass.Value;
        if (!string.Equals(pass.Attribute("encoding")?.Value, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The element's own text, which is where FileZilla puts a name.</summary>
    private static string OwnText(XElement element) =>
        string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();

    private static void Apply(ConnectionNode node, XElement element)
    {
        if (node is ConnectionFolder)
        {
            SetOwnText(element, node.Name);
            return;
        }

        if (element.Element("Name") is { } name) name.Value = node.Name;
        else SetOwnText(element, node.Name);

        if (node.Settings.Host.TryGet(out var host)) Set(element, "Host", host);
        if (node.Settings.User.TryGet(out var user)) Set(element, "User", user);
        if (node.Settings.Port.TryGet(out var port))
        {
            Set(element, "Port", port.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Set(XElement parent, string name, string value)
    {
        if (parent.Element(name) is { } existing) existing.Value = value;
        else parent.Add(new XElement(name, value));
    }

    /// <summary>Replace an element's own text without disturbing the children around it.</summary>
    private static void SetOwnText(XElement element, string value)
    {
        foreach (var text in element.Nodes().OfType<XText>().ToArray()) text.Remove();
        element.AddFirst(new XText(value));
    }

    private static XElement? Element(ConnectionNode node) =>
        Links.TryGetValue(node, out var element) ? element : null;

    /// <summary>
    /// Never the only copy. The first write leaves the original beside it, because this is somebody
    /// else's configuration and WinMux is the newcomer editing it.
    /// </summary>
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
