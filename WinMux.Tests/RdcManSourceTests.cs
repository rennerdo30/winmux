using System.Text;
using WinMux.Connections;

namespace WinMux.Tests;

/// <summary>
/// Remote Desktop Connection Manager's <c>.rdg</c> files: the other hierarchical format, with a
/// coarser inheritance than mRemoteNG's — whole blocks at a time rather than field by field.
/// </summary>
public class RdcManSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winmux-rdcman-" + Guid.NewGuid().ToString("N"));

    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <RDCMan programVersion="2.83" schemaVersion="3">
          <file>
            <credentialsProfiles />
            <properties>
              <expanded>True</expanded>
              <name>Servers</name>
            </properties>
            <group>
              <properties>
                <expanded>True</expanded>
                <name>Production</name>
              </properties>
              <logonCredentials inherit="None">
                <profileName scope="Local">Custom</profileName>
                <userName>svc-deploy</userName>
                <password>AQAAANCMnd8=</password>
                <domain>CORP</domain>
              </logonCredentials>
              <gatewaySettings inherit="None">
                <enabled>True</enabled>
                <hostName>gw.corp.example</hostName>
              </gatewaySettings>
              <connectionSettings inherit="None">
                <port>3390</port>
              </connectionSettings>
              <server>
                <properties>
                  <displayName>web-01</displayName>
                  <name>10.0.0.11</name>
                </properties>
                <logonCredentials inherit="FromParent" />
                <gatewaySettings inherit="FromParent" />
                <connectionSettings inherit="FromParent" />
              </server>
              <server>
                <properties>
                  <name>10.0.0.12</name>
                </properties>
                <logonCredentials inherit="None">
                  <userName>local-admin</userName>
                  <password>AQAAANCMnd8=</password>
                  <domain />
                </logonCredentials>
                <connectionSettings inherit="None">
                  <port>13389</port>
                </connectionSettings>
              </server>
            </group>
          </file>
        </RDCMan>
        """;

    private string Write(string xml = Sample)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "servers.rdg");
        File.WriteAllText(path, xml);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Groups_become_folders_and_servers_become_hosts()
    {
        var root = new RdcManSource(Write()).Read();

        Assert.Equal(["Servers", "Production"], root.Folders().Select(folder => folder.Name));
        Assert.Equal(["web-01", "10.0.0.12"], root.Entries().Select(entry => entry.Name));
    }

    [Fact]
    public void A_server_with_no_display_name_is_shown_by_its_address()
    {
        // Which is what RDCMan itself does, rather than showing a blank row.
        var root = new RdcManSource(Write()).Read();

        Assert.Contains(root.Entries(), entry => entry.Name == "10.0.0.12");
    }

    [Fact]
    public void From_parent_takes_the_whole_block()
    {
        // RDCMan's inheritance is coarser than mRemoteNG's: logonCredentials inherit="FromParent"
        // takes the username, the domain and the password together.
        var root = new RdcManSource(Write()).Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("svc-deploy", ConnectionResolver.User(web).Value);
        Assert.Equal("CORP", ConnectionResolver.Domain(web).Value);
        Assert.Equal("gw.corp.example", ConnectionResolver.Gateway(web).Value);
        Assert.Equal(3390, ConnectionResolver.Port(web).Value);
    }

    [Fact]
    public void And_says_which_group_it_came_from()
    {
        var root = new RdcManSource(Write()).Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("svc-deploy (from Production)", ConnectionResolver.User(web).Describe(web));
    }

    [Fact]
    public void A_server_that_states_its_own_block_states_all_of_it()
    {
        var root = new RdcManSource(Write()).Read();
        var other = root.Entries().First(entry => entry.Name == "10.0.0.12");

        Assert.Equal("local-admin", ConnectionResolver.User(other).Value);
        Assert.Same(other, ConnectionResolver.User(other).From);
        Assert.Equal(13389, ConnectionResolver.Port(other).Value);

        // It has no gateway block of its own, so that one still comes from the group.
        Assert.Equal("gw.corp.example", ConnectionResolver.Gateway(other).Value);
    }

    [Fact]
    public void Everything_in_an_rdg_file_is_remote_desktop()
    {
        var root = new RdcManSource(Write()).Read();

        Assert.All(root.Entries(),
            entry => Assert.Equal(ConnectionProtocol.Rdp, ConnectionResolver.Protocol(entry).Value));
    }

    [Fact]
    public void The_host_is_the_address_and_not_the_display_name()
    {
        var root = new RdcManSource(Write()).Read();

        Assert.Equal("10.0.0.11",
            ConnectionResolver.Host(root.Entries().First(entry => entry.Name == "web-01")).Value);
    }

    [Fact]
    public void Writing_is_refused_in_words_rather_than_attempted()
    {
        // RDCMan is retired and undocumented. Reading a format well enough to show it is not the
        // same as knowing enough to rewrite somebody's server list.
        var source = new RdcManSource(Write());
        var root = source.Read();

        var error = Assert.Throws<ConnectionSourceException>(() => source.Write(root));

        Assert.Contains("does not write", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passwords_need_the_machine_that_saved_them()
    {
        // DPAPI ties them to a Windows account, so a file from a colleague keeps its servers and
        // loses its passwords. That is what DPAPI is for, not a shortcoming to work around.
        var source = new RdcManSource(Write(), _ => null);

        Assert.Empty(source.ReadCredentials(source.Read()));
    }

    [Fact]
    public void A_password_on_a_group_is_found_on_the_group()
    {
        // Where RDCMan puts it, and where a scan that looked only at servers would miss it: the
        // group holds one credential and every server under it inherits the whole block.
        var source = new RdcManSource(Write(), _ => Encoding.Unicode.GetBytes("hunter2"));

        var credentials = source.ReadCredentials(source.Read());

        Assert.Equal(["Production", "10.0.0.12"], credentials.Select(found => found.Node.Name));
        Assert.All(credentials, found => Assert.Equal("hunter2", found.Secret));
        Assert.Contains(credentials, found => found.Node is ConnectionFolder);
    }

    [Fact]
    public void A_server_that_inherits_its_credentials_has_none_of_its_own()
    {
        var source = new RdcManSource(Write(), _ => Encoding.Unicode.GetBytes("hunter2"));

        var credentials = source.ReadCredentials(source.Read());

        Assert.DoesNotContain(credentials, found => found.Node.Name == "web-01");
    }

    [Fact]
    public void Without_a_way_to_unprotect_nothing_is_claimed()
    {
        var source = new RdcManSource(Write());

        Assert.Empty(source.ReadCredentials(source.Read()));
    }

    [Fact]
    public void Something_that_is_not_an_rdg_file_is_refused() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new RdcManSource(Write("<?xml version=\"1.0\"?><RDCMan />")).Read());

    [Fact]
    public void A_missing_file_says_so() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new RdcManSource(Path.Combine(_directory, "absent.rdg")).Read());
}
