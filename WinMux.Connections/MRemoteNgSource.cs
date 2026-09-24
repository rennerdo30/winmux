using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace WinMux.Connections;

/// <summary>
/// mRemoteNG's connections, from <c>confCons.xml</c>.
///
/// <para>
/// The first source with inheritance of its own, and the reason WinMux grew a connection tree at
/// all. Every field on a node has a matching <c>Inherit*</c> attribute: <c>InheritUsername="True"</c>
/// means take it from the container above. That maps onto <see cref="Inherited{T}"/> exactly — a
/// stated value or an absent one — which is why importing this tool by flattening would have thrown
/// away the thing that makes it worth using (ADR 0025).
/// </para>
///
/// <para>
/// Passwords are AES-GCM with a key derived from a password by PBKDF2. The default is the string
/// <c>mR3m</c>, which mRemoteNG ships with and most people never change; a file with a password of
/// its own needs it supplied. The nonce is sixteen bytes, which .NET's own <c>AesGcm</c> refuses —
/// it accepts twelve and nothing else — so this uses BouncyCastle, which the package already ships
/// for SSH.NET.
/// </para>
/// </summary>
public sealed class MRemoteNgSource : IConnectionSource
{
    /// <summary>What mRemoteNG uses when the user has not set a password of their own.</summary>
    public const string DefaultPassword = "mR3m";

    private const int SaltLength = 16;
    private const int NonceLength = 16;
    private const int TagBits = 128;
    private const int KeyBits = 256;

    private readonly string _path;
    private readonly string _password;
    private readonly Dictionary<ConnectionNode, XElement> _elements = [];

    private XDocument? _document;
    private int _iterations = 1000;

    /// <param name="path">The file, or null for mRemoteNG's usual place.</param>
    /// <param name="password">
    /// The file's password. Null uses mRemoteNG's default, which is what an untouched installation
    /// has. A file encrypted with a chosen password needs it, and reading one without asking is not
    /// something WinMux does.
    /// </param>
    public MRemoteNgSource(string? path = null, string? password = null)
    {
        _path = path ?? DefaultPath();
        _password = password ?? DefaultPassword;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "mRemoteNG",
        "confCons.xml");

    public string DisplayName => "mRemoteNG";

    public string Location => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>mRemoteNG holds the file open and writes all of it when it closes.</summary>
    public bool CanWriteWhileOtherToolRuns => false;

    public ConnectionFolder Read()
    {
        if (!Exists) throw new ConnectionSourceException($"There is no mRemoteNG configuration at {_path}.");

        _document = Load();
        _elements.Clear();

        var connections = _document.Root
            ?? throw new ConnectionSourceException($"{_path} is empty.");

        if (!connections.Name.LocalName.Equals("Connections", StringComparison.Ordinal))
        {
            throw new ConnectionSourceException(
                $"{_path} has no <Connections> root, so it is not an mRemoteNG configuration.");
        }

        if (string.Equals(Attribute(connections, "FullFileEncryption"), "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectionSourceException(
                $"{_path} is fully encrypted. WinMux can read per-password files, not whole-file ones.");
        }

        _iterations = int.TryParse(Attribute(connections, "KdfIterations"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var iterations) && iterations > 0
            ? iterations
            : 1000;

        var root = new ConnectionFolder(Attribute(connections, "Name") is { Length: > 0 } name ? name : "mRemoteNG");
        _elements[root] = connections;
        ReadInto(root, connections);
        return root;
    }

    public void Write(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var document = _document
            ?? throw new ConnectionSourceException("Read the mRemoteNG connections before writing them back.");

        foreach (var node in root.Folders().Cast<ConnectionNode>().Concat(root.Entries()))
        {
            if (!_elements.TryGetValue(node, out var element)) continue;

            element.SetAttributeValue("Name", node.Name);
            Apply(node, element, "Hostname", node.Settings.Host);
            Apply(node, element, "Username", node.Settings.User);
            Apply(node, element, "Domain", node.Settings.Domain);
            ApplyPort(node, element);
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

        // Containers as well as connections: mRemoteNG's whole point is stating a credential once
        // on a folder and inheriting it, so a scan that skipped folders would miss the one that
        // fifty hosts are using.
        var found = new List<FoundCredential>();
        foreach (var node in root.Folders().Cast<ConnectionNode>().Concat(root.Entries()))
        {
            if (!_elements.TryGetValue(node, out var element)) continue;

            var stored = Attribute(element, "Password");
            if (stored.Length == 0) continue;

            if (Decrypt(stored, _password, _iterations) is { Length: > 0 } secret)
            {
                found.Add(new FoundCredential(node, secret));
            }
        }

        return found;
    }

    private XDocument Load()
    {
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

    private void ReadInto(ConnectionFolder parent, XElement element)
    {
        foreach (var child in element.Elements("Node"))
        {
            var name = Attribute(child, "Name");
            var settings = SettingsOf(child);

            if (Attribute(child, "Type").Equals("Container", StringComparison.OrdinalIgnoreCase))
            {
                var folder = new ConnectionFolder(name, settings);
                _elements[folder] = child;
                parent.Add(folder);
                ReadInto(folder, child);
                continue;
            }

            var entry = new ConnectionEntry(name, settings);
            _elements[entry] = child;
            parent.Add(entry);
        }
    }

    /// <summary>
    /// A field is read only when its <c>Inherit*</c> attribute says the node states it. That is the
    /// whole of mRemoteNG's inheritance, and it lines up with <see cref="Inherited{T}"/> one to one.
    /// </summary>
    private static ConnectionSettings SettingsOf(XElement node) => new()
    {
        Protocol = Inherits(node, "Protocol")
            ? Inherited<ConnectionProtocol>.Inherit
            : Inherited<ConnectionProtocol>.Of(ProtocolOf(Attribute(node, "Protocol"))),
        Host = Stated(node, "Hostname"),
        User = Stated(node, "Username"),
        Domain = Stated(node, "Domain"),
        Gateway = Stated(node, "RDGatewayHostname", "RDGatewayUsageMethod"),
        Port = PortOf(node),
        Command = Stated(node, "PreExtApp"),
    };

    private static Inherited<string> Stated(XElement node, string attribute, string? inheritName = null) =>
        Inherits(node, inheritName ?? attribute)
            ? Inherited<string>.Inherit
            : Inherited<string>.Of(Attribute(node, attribute));

    private static Inherited<int> PortOf(XElement node)
    {
        if (Inherits(node, "Port")) return Inherited<int>.Inherit;

        return int.TryParse(Attribute(node, "Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
               && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;
    }

    private static bool Inherits(XElement node, string attribute) =>
        string.Equals(Attribute(node, "Inherit" + attribute), "true", StringComparison.OrdinalIgnoreCase);

    private static string Attribute(XElement element, string name) =>
        element.Attribute(name)?.Value ?? string.Empty;

    private static ConnectionProtocol ProtocolOf(string value) => value.Trim().ToUpperInvariant() switch
    {
        "RDP" => ConnectionProtocol.Rdp,
        "SSH1" or "SSH2" => ConnectionProtocol.Ssh,
        "TELNET" => ConnectionProtocol.Telnet,
        "" => ConnectionProtocol.Unknown,
        _ => ConnectionProtocol.Unknown,
    };

    /// <summary>
    /// Writing a value also clears its <c>Inherit*</c> flag, because a node that states something
    /// while claiming to inherit it is a node mRemoteNG will read the other way.
    /// </summary>
    private static void Apply(ConnectionNode node, XElement element, string attribute, Inherited<string> setting)
    {
        if (!setting.TryGet(out var value)) return;

        element.SetAttributeValue(attribute, value);
        if (element.Attribute("Inherit" + attribute) is not null)
        {
            element.SetAttributeValue("Inherit" + attribute, "false");
        }
    }

    private static void ApplyPort(ConnectionNode node, XElement element)
    {
        if (!node.Settings.Port.TryGet(out var port)) return;

        element.SetAttributeValue("Port", port.ToString(CultureInfo.InvariantCulture));
        if (element.Attribute("InheritPort") is not null) element.SetAttributeValue("InheritPort", "false");
    }

    /// <summary>
    /// mRemoteNG's AES-GCM: base64 of salt, nonce, ciphertext and tag, with the key derived from
    /// the file's password by PBKDF2-SHA1 over the salt.
    /// </summary>
    /// <returns>The password, or null when the file's password is wrong or the value is not one.</returns>
    internal static string? Decrypt(string stored, string password, int iterations)
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

        if (raw.Length <= SaltLength + NonceLength + TagBits / 8) return null;

        var salt = raw[..SaltLength];
        var nonce = raw[SaltLength..(SaltLength + NonceLength)];
        var body = raw[(SaltLength + NonceLength)..];

        var generator = new Pkcs5S2ParametersGenerator(new Org.BouncyCastle.Crypto.Digests.Sha1Digest());
        generator.Init(Encoding.UTF8.GetBytes(password), salt, iterations);
        var key = (KeyParameter)generator.GenerateDerivedMacParameters(KeyBits);

        try
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(key, TagBits, nonce));

            var plain = new byte[cipher.GetOutputSize(body.Length)];
            var written = cipher.ProcessBytes(body, 0, body.Length, plain, 0);
            written += cipher.DoFinal(plain, written);

            return Encoding.UTF8.GetString(plain, 0, written);
        }
        catch (Exception exception) when (exception is Org.BouncyCastle.Crypto.InvalidCipherTextException
                                             or ArgumentException)
        {
            // The tag did not match: the file's password is not the one supplied. A wrong answer
            // here must never be returned as though it were a password.
            return null;
        }
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
