using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using WinMux.Core.Connections;

namespace WinMux.Tests;

/// <summary>
/// mRemoteNG: the first format with inheritance of its own, and the reason WinMux has a connection
/// tree rather than a list.
/// </summary>
public class MRemoteNgSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winmux-mremoteng-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// mRemoteNG's real shape: a container stating the credentials, hosts under it inheriting most
    /// of them and overriding one, and a pile of attributes WinMux does not model.
    /// </summary>
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Connections Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="" ConfVersion="2.6">
          <Node Name="Production" Type="Container" Expanded="True" Descr="" Icon="mRemoteNG" Panel="General" Username="svc-deploy" Domain="CORP" Hostname="" Protocol="RDP" Port="3389" RDGatewayHostname="gw.corp.example" Colors="Colors16Bit" Resolution="FitToWindow" InheritUsername="false" InheritDomain="false" InheritHostname="false" InheritProtocol="false" InheritPort="false" InheritRDGatewayUsageMethod="false" InheritColors="false" InheritResolution="false" />
          <Node Name="web-01" Type="Connection" Descr="" Icon="mRemoteNG" Panel="General" Username="" Domain="" Hostname="10.0.0.11" Protocol="RDP" Port="0" Colors="Colors16Bit" Resolution="FitToWindow" InheritUsername="true" InheritDomain="true" InheritHostname="false" InheritProtocol="true" InheritPort="true" InheritRDGatewayUsageMethod="true" InheritColors="true" InheritResolution="true" />
        </Connections>
        """;

    /// <summary>The same file with the hosts actually inside the container, which is the usual shape.</summary>
    private const string Nested = """
        <?xml version="1.0" encoding="utf-8"?>
        <Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" ConfVersion="2.6">
          <Node Name="Production" Type="Container" Username="svc-deploy" Domain="CORP" Protocol="RDP" Port="3389" RDGatewayHostname="gw.corp.example" InheritUsername="false" InheritDomain="false" InheritProtocol="false" InheritPort="false" InheritRDGatewayUsageMethod="false">
            <Node Name="web-01" Type="Connection" Hostname="10.0.0.11" Username="" Domain="" Port="0" InheritUsername="true" InheritDomain="true" InheritProtocol="true" InheritPort="true" InheritRDGatewayUsageMethod="true" InheritHostname="false" />
            <Node Name="db-01" Type="Connection" Hostname="10.0.0.12" Username="postgres" Domain="" Port="5432" InheritUsername="false" InheritDomain="true" InheritProtocol="true" InheritPort="false" InheritRDGatewayUsageMethod="true" InheritHostname="false" />
          </Node>
        </Connections>
        """;

    private string Write(string xml)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "confCons.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Containers_become_folders_and_connections_become_hosts()
    {
        var root = new MRemoteNgSource(Write(Nested)).Read();

        Assert.Equal(["Connections", "Production"], root.Folders().Select(folder => folder.Name));
        Assert.Equal(["web-01", "db-01"], root.Entries().Select(entry => entry.Name));
    }

    [Fact]
    public void Inherit_true_means_take_it_from_the_folder()
    {
        // The point of the whole exercise. web-01 states no username and inherits svc-deploy from
        // Production, together with the domain, the protocol, the port and the gateway.
        var root = new MRemoteNgSource(Write(Nested)).Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("svc-deploy", ConnectionResolver.User(web).Value);
        Assert.Equal("CORP", ConnectionResolver.Domain(web).Value);
        Assert.Equal(3389, ConnectionResolver.Port(web).Value);
        Assert.Equal("gw.corp.example", ConnectionResolver.Gateway(web).Value);
        Assert.Equal(ConnectionProtocol.Rdp, ConnectionResolver.Protocol(web).Value);
    }

    [Fact]
    public void And_says_which_folder_it_came_from()
    {
        var root = new MRemoteNgSource(Write(Nested)).Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("svc-deploy (from Production)", ConnectionResolver.User(web).Describe(web));
    }

    [Fact]
    public void Inherit_false_means_the_host_states_it()
    {
        var root = new MRemoteNgSource(Write(Nested)).Read();
        var db = root.Entries().First(entry => entry.Name == "db-01");

        Assert.Equal("postgres", ConnectionResolver.User(db).Value);
        Assert.Same(db, ConnectionResolver.User(db).From);
        Assert.Equal(5432, ConnectionResolver.Port(db).Value);

        // And it still inherits everything it did not override.
        Assert.Equal("CORP", ConnectionResolver.Domain(db).Value);
    }

    [Fact]
    public void A_flat_file_reads_as_a_flat_file()
    {
        // Not every mRemoteNG file nests, and a host beside a container inherits from the root
        // rather than from the container next to it.
        var root = new MRemoteNgSource(Write(Sample)).Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("10.0.0.11", ConnectionResolver.Host(web).Value);
        Assert.False(ConnectionResolver.User(web).IsSet);
    }

    [Fact]
    public void Saving_keeps_everything_winmux_does_not_model()
    {
        var path = Write(Sample);
        var source = new MRemoteNgSource(path);
        var root = source.Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");
        web.Settings = web.Settings with { Host = Inherited<string>.Of("10.0.0.99") };

        source.Write(root);

        var node = XDocument.Load(path).Descendants("Node").First(n => n.Attribute("Name")?.Value == "web-01");

        Assert.Equal("10.0.0.99", node.Attribute("Hostname")?.Value);
        Assert.Equal("Colors16Bit", node.Attribute("Colors")?.Value);
        Assert.Equal("FitToWindow", node.Attribute("Resolution")?.Value);
        Assert.Equal("mRemoteNG", node.Attribute("Icon")?.Value);
    }

    [Fact]
    public void Stating_a_value_stops_it_claiming_to_inherit_one()
    {
        // A node that carries a username and also says InheritUsername="true" is a node mRemoteNG
        // will read the other way, so the flag has to move with the value.
        var path = Write(Nested);
        var source = new MRemoteNgSource(path);
        var root = source.Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");
        web.Settings = web.Settings with { User = Inherited<string>.Of("root") };

        source.Write(root);

        var node = XDocument.Load(path).Descendants("Node").First(n => n.Attribute("Name")?.Value == "web-01");

        Assert.Equal("root", node.Attribute("Username")?.Value);
        Assert.Equal("false", node.Attribute("InheritUsername")?.Value);
    }

    [Fact]
    public void A_password_is_decrypted_with_the_default_key()
    {
        // mRemoteNG ships with "mR3m" and most installations never change it.
        var encrypted = Encrypt("hunter2", MRemoteNgSource.DefaultPassword, iterations: 1000);
        var path = Write(Nested.Replace(
            @"Name=""db-01"" Type=""Connection""",
            $@"Name=""db-01"" Type=""Connection"" Password=""{encrypted}""",
            StringComparison.Ordinal));

        var source = new MRemoteNgSource(path);
        var found = Assert.Single(source.ReadCredentials(source.Read()));

        Assert.Equal("db-01", found.Node.Name);
        Assert.Equal("hunter2", found.Secret);
    }

    [Fact]
    public void A_file_with_its_own_password_needs_it()
    {
        var encrypted = Encrypt("hunter2", "the-real-password", iterations: 1000);
        var xml = Nested.Replace(
            @"Name=""db-01"" Type=""Connection""",
            $@"Name=""db-01"" Type=""Connection"" Password=""{encrypted}""",
            StringComparison.Ordinal);

        // Wrong key: the tag does not match, and a wrong answer must never come back as a password.
        Assert.Empty(new MRemoteNgSource(Write(xml)).ReadCredentials(new MRemoteNgSource(Write(xml)).Read()));

        var right = new MRemoteNgSource(Write(xml), "the-real-password");
        Assert.Equal("hunter2", Assert.Single(right.ReadCredentials(right.Read())).Secret);
    }

    [Fact]
    public void The_iteration_count_in_the_file_is_the_one_used()
    {
        // KdfIterations is per file, and deriving with the wrong count produces the wrong key —
        // which looks exactly like a wrong password.
        var encrypted = Encrypt("hunter2", MRemoteNgSource.DefaultPassword, iterations: 5000);
        var xml = Nested
            .Replace(@"KdfIterations=""1000""", @"KdfIterations=""5000""", StringComparison.Ordinal)
            .Replace(
                @"Name=""db-01"" Type=""Connection""",
                $@"Name=""db-01"" Type=""Connection"" Password=""{encrypted}""",
                StringComparison.Ordinal);

        var source = new MRemoteNgSource(Write(xml));

        Assert.Equal("hunter2", Assert.Single(source.ReadCredentials(source.Read())).Secret);
    }

    [Fact]
    public void Anything_that_is_not_an_encrypted_password_is_refused()
    {
        Assert.Null(MRemoteNgSource.Decrypt("not base64 at all", "mR3m", 1000));
        Assert.Null(MRemoteNgSource.Decrypt(Convert.ToBase64String(new byte[8]), "mR3m", 1000));
    }

    [Fact]
    public void A_fully_encrypted_file_says_so_rather_than_reading_as_empty()
    {
        var path = Write(Nested.Replace(
            @"FullFileEncryption=""false""", @"FullFileEncryption=""true""", StringComparison.Ordinal));

        var error = Assert.Throws<ConnectionSourceException>(() => new MRemoteNgSource(path).Read());

        Assert.Contains("fully encrypted", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Something_that_is_not_an_mremoteng_file_is_refused() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new MRemoteNgSource(Write("<?xml version=\"1.0\"?><Something />")).Read());

    [Fact]
    public void A_missing_file_says_so() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new MRemoteNgSource(Path.Combine(_directory, "absent.xml")).Read());

    /// <summary>
    /// mRemoteNG's own encryption, written out here so the decryptor is checked against the format
    /// rather than against itself: salt, a sixteen-byte nonce, then AES-GCM with a PBKDF2 key.
    /// </summary>
    private static string Encrypt(string secret, string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(16);

        var generator = new Pkcs5S2ParametersGenerator(new Org.BouncyCastle.Crypto.Digests.Sha1Digest());
        generator.Init(Encoding.UTF8.GetBytes(password), salt, iterations);
        var key = (KeyParameter)generator.GenerateDerivedMacParameters(256);

        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(key, 128, nonce));

        var plain = Encoding.UTF8.GetBytes(secret);
        var body = new byte[cipher.GetOutputSize(plain.Length)];
        var written = cipher.ProcessBytes(plain, 0, plain.Length, body, 0);
        written += cipher.DoFinal(body, written);

        return Convert.ToBase64String([.. salt, .. nonce, .. body.AsSpan(0, written)]);
    }
}
