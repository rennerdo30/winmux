using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace WinMux.Connections;

/// <summary>
/// Undoing whatever the operating system did to protect a stored secret.
///
/// <para>
/// Remote Desktop Connection Manager encrypts its passwords with DPAPI, tied to the Windows account
/// that saved them. That is an OS service rather than an algorithm, so it arrives from outside Core
/// — and the tie is worth understanding rather than working around: a <c>.rdg</c> file copied from
/// a colleague keeps its servers and loses its passwords, which is what DPAPI is for.
/// </para>
/// </summary>
/// <param name="protectedBytes">What the file held.</param>
/// <returns>The secret, or null when this machine and account cannot read it.</returns>
public delegate byte[]? Unprotect(byte[] protectedBytes);

/// <summary>
/// Remote Desktop Connection Manager's <c>.rdg</c> files.
///
/// <para>
/// The other hierarchical format, and its inheritance is coarser than mRemoteNG's: whole blocks are
/// inherited at once. A server says <c>&lt;logonCredentials inherit="FromParent" /&gt;</c> and takes
/// the username, the domain and the password together, or states all of them itself. WinMux models
/// the fields separately, so a block marked inherited states nothing and a block marked
/// <c>None</c> states everything in it — which is exactly what RDCMan means.
/// </para>
///
/// <para>
/// Unlike the others this source is read-only. RDCMan is retired, it has no schema documentation
/// beyond the files it produces, and its <c>.rdg</c> files carry elements whose meaning is only
/// inferred; writing to a format on those terms is how somebody's server list gets damaged. Reading
/// costs nothing and converting into WinMux's own connections is one click (ADR 0025).
/// </para>
/// </summary>
public sealed class RdcManSource : IConnectionSource
{
    private readonly string _path;
    private readonly Unprotect? _unprotect;
    private readonly Dictionary<ConnectionNode, XElement> _elements = [];

    /// <param name="path">A <c>.rdg</c> file.</param>
    /// <param name="unprotect">
    /// How to undo DPAPI. Null means the passwords are not read at all, which is the honest answer
    /// on a machine that has no DPAPI rather than a silent empty list.
    /// </param>
    public RdcManSource(string path, Unprotect? unprotect = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _unprotect = unprotect;
    }

    public string DisplayName => "Remote Desktop Connection Manager";

    public string Location => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>Moot: this source does not write. See the type's remarks.</summary>
    public bool CanWriteWhileOtherToolRuns => false;

    public ConnectionFolder Read()
    {
        if (!Exists) throw new ConnectionSourceException($"There is no RDCMan file at {_path}.");

        XDocument document;
        try
        {
            using var stream = File.OpenRead(_path);
            document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or System.Xml.XmlException)
        {
            throw new ConnectionSourceException($"{_path} could not be read: {exception.Message}", exception);
        }

        var file = document.Root?.Element("file")
            ?? throw new ConnectionSourceException(
                $"{_path} has no <file> element, so it is not an RDCMan document.");

        _elements.Clear();

        var root = new ConnectionFolder(NameOf(file) is { Length: > 0 } name ? name : "RDCMan", SettingsOf(file));
        _elements[root] = file;
        ReadInto(root, file);
        return root;
    }

    /// <summary>
    /// Refused, always, and in words. RDCMan is retired and undocumented; a source that guessed at
    /// its schema well enough to read is not one that should be trusted to rewrite somebody's
    /// server list.
    /// </summary>
    public void Write(ConnectionFolder root) =>
        throw new ConnectionSourceException(
            "WinMux reads Remote Desktop Connection Manager files but does not write them. " +
            "Import the connections to edit them.");

    public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (_unprotect is null) return [];

        // Folders as well as servers: RDCMan's usual shape is one credential on a group, inherited
        // by everything in it, and a scan that looked only at servers would find nothing at all in
        // a well-organised file.
        var found = new List<FoundCredential>();
        foreach (var node in root.Folders().Cast<ConnectionNode>().Concat(root.Entries()))
        {
            if (!_elements.TryGetValue(node, out var element)) continue;

            var stored = element.Element("logonCredentials")?.Element("password")?.Value;
            if (string.IsNullOrWhiteSpace(stored)) continue;

            if (Decrypt(stored, _unprotect) is { Length: > 0 } secret)
            {
                found.Add(new FoundCredential(node, secret));
            }
        }

        return found;
    }

    private void ReadInto(ConnectionFolder parent, XElement element)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "group":
                    var folder = new ConnectionFolder(NameOf(child), SettingsOf(child));
                    _elements[folder] = child;
                    parent.Add(folder);
                    ReadInto(folder, child);
                    break;

                case "server":
                    var entry = new ConnectionEntry(NameOf(child), SettingsOf(child));
                    _elements[entry] = child;
                    parent.Add(entry);
                    break;
            }
        }
    }

    /// <summary>
    /// A server's display name if it has one, and its address otherwise — RDCMan shows the address
    /// in the tree when nothing else was typed.
    /// </summary>
    private static string NameOf(XElement element)
    {
        var properties = element.Element("properties");
        var display = properties?.Element("displayName")?.Value.Trim();
        if (!string.IsNullOrEmpty(display)) return display;

        return properties?.Element("name")?.Value.Trim() ?? string.Empty;
    }

    /// <summary>
    /// RDCMan inherits whole blocks: a <c>logonCredentials</c> marked <c>FromParent</c> takes the
    /// username, the domain and the password together. So a block that inherits states none of its
    /// fields, and a block that does not states all of them.
    /// </summary>
    private static ConnectionSettings SettingsOf(XElement element)
    {
        var logon = Block(element, "logonCredentials");
        var gateway = Block(element, "gatewaySettings");
        var connection = Block(element, "connectionSettings");
        var host = element.Element("properties")?.Element("name")?.Value.Trim();

        return new ConnectionSettings
        {
            // Everything in an .rdg file is Remote Desktop; that is what the tool was for.
            Protocol = element.Name.LocalName == "server"
                ? Inherited<ConnectionProtocol>.Of(ConnectionProtocol.Rdp)
                : Inherited<ConnectionProtocol>.Inherit,
            Host = string.IsNullOrEmpty(host) ? Inherited<string>.Inherit : Inherited<string>.Of(host),
            User = Text(logon, "userName"),
            Domain = Text(logon, "domain"),
            Gateway = Text(gateway, "hostName"),
            Port = PortOf(connection),
        };
    }

    /// <summary>The block's element when it states its own values, or null when it inherits them.</summary>
    private static XElement? Block(XElement element, string name)
    {
        var block = element.Element(name);
        if (block is null) return null;

        var inherit = block.Attribute("inherit")?.Value ?? "None";
        return inherit.Equals("None", StringComparison.OrdinalIgnoreCase) ? block : null;
    }

    private static Inherited<string> Text(XElement? block, string name)
    {
        var value = block?.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? Inherited<string>.Inherit : Inherited<string>.Of(value);
    }

    private static Inherited<int> PortOf(XElement? connection) =>
        int.TryParse(connection?.Element("port")?.Value.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var port) && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;

    /// <summary>
    /// A DPAPI blob in base64. Returns null when this account cannot read it, which is the normal
    /// outcome for a file that came from somebody else's machine — not an error, and not something
    /// to interrupt anyone about.
    /// </summary>
    internal static string? Decrypt(string stored, Unprotect unprotect)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(stored);
        }
        catch (FormatException)
        {
            return null;
        }

        var plain = unprotect(raw);
        if (plain is null || plain.Length == 0) return null;

        // RDCMan stores the password as UTF-16, which is what Windows hands DPAPI.
        return Encoding.Unicode.GetString(plain).TrimEnd('\0');
    }
}
