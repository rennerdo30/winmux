using System.Xml.Linq;
using WinMux.Core.Connections;

namespace WinMux.Tests;

/// <summary>
/// Reading and writing FileZilla's own site manager, in place.
///
/// <para>
/// The test that matters most here is not that the hosts come back. It is that everything WinMux
/// has no opinion about survives a save: a source that round-trips only the fields it understands
/// would delete the rest of somebody's configuration the first time they renamed a server
/// (ADR 0025).
/// </para>
/// </summary>
public class FileZillaSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winmux-filezilla-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A file in FileZilla's real shape: a nested folder, two sites, a base64 password, and a pile
    /// of per-site settings WinMux does not model.
    /// </summary>
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FileZilla3 version="3.66.5" platform="windows">
          <Servers>
            <Folder expanded="1">Production
              <Server>
                <Host>files.example.com</Host>
                <Port>22</Port>
                <Protocol>1</Protocol>
                <Type>0</Type>
                <User>deploy</User>
                <Pass encoding="base64">c2VjcmV0LXNhdWNl</Pass>
                <Logontype>1</Logontype>
                <TimezoneOffset>0</TimezoneOffset>
                <PasvMode>MODE_DEFAULT</PasvMode>
                <EncodingType>Auto</EncodingType>
                <BypassProxy>0</BypassProxy>
                <RemoteDir>1 0 5 files 3 log</RemoteDir>
                <SyncBrowsing>1</SyncBrowsing>
                <Name>web-01</Name>
              </Server>
            </Folder>
            <Server>
              <Host>ftp.example.org</Host>
              <Port>21</Port>
              <Protocol>0</Protocol>
              <User>anonymous</User>
              <Logontype>0</Logontype>
              <EncodingType>Auto</EncodingType>
              <Name>public mirror</Name>
            </Server>
          </Servers>
        </FileZilla3>
        """;

    private string Write(string xml = Sample)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "sitemanager.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_tree_comes_back_with_its_folders()
    {
        var source = new FileZillaSource(Write());

        var root = source.Read();

        Assert.Equal(["Production"], root.Folders().Skip(1).Select(folder => folder.Name));
        Assert.Equal(["web-01", "public mirror"], root.Entries().Select(entry => entry.Name));
    }

    [Fact]
    public void A_site_keeps_its_settings()
    {
        var root = new FileZillaSource(Write()).Read();

        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("files.example.com", ConnectionResolver.Host(web).Value);
        Assert.Equal(22, ConnectionResolver.Port(web).Value);
        Assert.Equal("deploy", ConnectionResolver.User(web).Value);
        Assert.Equal(ConnectionProtocol.Sftp, ConnectionResolver.Protocol(web).Value);
    }

    [Fact]
    public void A_remote_directory_is_read_out_of_its_length_prefixed_list()
    {
        var root = new FileZillaSource(Write()).Read();

        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("/files/log", ConnectionResolver.RemoteDirectory(web).Value);
    }

    [Fact]
    public void Plain_ftp_is_told_apart_from_sftp()
    {
        var root = new FileZillaSource(Write()).Read();

        var mirror = root.Entries().First(entry => entry.Name == "public mirror");

        Assert.Equal(ConnectionProtocol.Ftp, ConnectionResolver.Protocol(mirror).Value);
    }

    [Fact]
    public void A_stored_password_is_read_but_never_put_in_the_tree()
    {
        // ADR 0020 and ADR 0025: a secret is fetched when connecting, not carried around with the
        // settings. ReadCredentials is a separate, deliberate act.
        var source = new FileZillaSource(Write());
        var root = source.Read();

        var credentials = source.ReadCredentials(root);

        var found = Assert.Single(credentials);
        Assert.Equal("web-01", found.Entry.Name);
        Assert.Equal("secret-sauce", found.Secret);
    }

    [Fact]
    public void Saving_keeps_every_setting_winmux_has_no_opinion_about()
    {
        // The one that guards the user's configuration. Rename a site, save, and the timezone
        // offset, the passive mode, the sync browsing and the password must all still be there.
        var path = Write();
        var source = new FileZillaSource(path);
        var root = source.Read();
        root.Entries().First(entry => entry.Name == "web-01").Name = "web-01 (renamed)";

        source.Write(root);

        var server = XDocument.Load(path).Descendants("Server")
            .First(element => element.Element("Host")?.Value == "files.example.com");

        Assert.Equal("web-01 (renamed)", server.Element("Name")?.Value);
        Assert.Equal("0", server.Element("TimezoneOffset")?.Value);
        Assert.Equal("MODE_DEFAULT", server.Element("PasvMode")?.Value);
        Assert.Equal("1", server.Element("SyncBrowsing")?.Value);
        Assert.Equal("c2VjcmV0LXNhdWNl", server.Element("Pass")?.Value);
    }

    [Fact]
    public void Saving_keeps_the_rest_of_the_document_too()
    {
        var path = Write();
        var source = new FileZillaSource(path);
        source.Write(source.Read());

        var document = XDocument.Load(path);

        Assert.Equal("3.66.5", document.Root?.Attribute("version")?.Value);
        Assert.Equal("1", document.Descendants("Folder").First().Attribute("expanded")?.Value);
    }

    [Fact]
    public void An_edited_host_is_written_back()
    {
        var path = Write();
        var source = new FileZillaSource(path);
        var root = source.Read();
        var mirror = root.Entries().First(entry => entry.Name == "public mirror");
        mirror.Settings = mirror.Settings with { Host = Inherited<string>.Of("mirror2.example.org") };

        source.Write(root);

        var reread = new FileZillaSource(path).Read().Entries().First(entry => entry.Name == "public mirror");
        Assert.Equal("mirror2.example.org", ConnectionResolver.Host(reread).Value);
    }

    [Fact]
    public void A_renamed_folder_keeps_the_sites_inside_it()
    {
        var path = Write();
        var source = new FileZillaSource(path);
        var root = source.Read();
        root.Folders().First(folder => folder.Name == "Production").Name = "Prod";

        source.Write(root);

        var reread = new FileZillaSource(path).Read();
        Assert.Equal(["Prod"], reread.Folders().Skip(1).Select(folder => folder.Name));
        Assert.Contains(reread.Entries(), entry => entry.Name == "web-01");
    }

    [Fact]
    public void The_first_save_leaves_the_original_beside_it()
    {
        // Somebody else's configuration, edited by the newcomer. Never the only copy.
        var path = Write();
        var source = new FileZillaSource(path);
        var root = source.Read();
        root.Entries().First().Name = "changed";

        source.Write(root);

        var backup = path + ".winmux-backup";
        Assert.True(File.Exists(backup));
        Assert.Contains("<Name>web-01</Name>", File.ReadAllText(backup), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_there_says_so()
    {
        var source = new FileZillaSource(Path.Combine(_directory, "absent.xml"));

        Assert.False(source.Exists);
        var error = Assert.Throws<ConnectionSourceException>(() => source.Read());
        Assert.Contains("absent.xml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_site_manager_is_refused_rather_than_read_as_empty()
    {
        // "Your sites are not here" and "this file is not valid" must not look the same, because
        // the second one must never lead to writing over it.
        var source = new FileZillaSource(Write("<?xml version=\"1.0\"?><something-else />"));

        Assert.Throws<ConnectionSourceException>(() => source.Read());
    }

    [Fact]
    public void Broken_xml_is_refused()
    {
        var source = new FileZillaSource(Write("<FileZilla3><Servers>"));

        Assert.Throws<ConnectionSourceException>(() => source.Read());
    }

    [Fact]
    public void Writing_before_reading_is_refused_rather_than_writing_an_empty_file()
    {
        var source = new FileZillaSource(Write());

        Assert.Throws<ConnectionSourceException>(() => source.Write(new ConnectionFolder("FileZilla")));
    }

    [Fact]
    public void FileZilla_must_not_be_written_underneath()
    {
        // It reads the file when the Site Manager opens and writes all of it when that closes, so
        // a change saved underneath a running FileZilla is lost the next time the user looks.
        Assert.False(new FileZillaSource(Write()).CanWriteWhileOtherToolRuns);
    }
}
