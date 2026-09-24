using System.Globalization;
using System.Text;
using WinMux.Connections;

namespace WinMux.Tests;

/// <summary>
/// WinSCP's sessions: a hierarchy hidden in the section names, and passwords that are obfuscated
/// rather than encrypted.
/// </summary>
public class WinScpSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winmux-winscp-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// WinSCP's real shape: the folder is part of the session name, and each session carries a
    /// pile of settings WinMux does not model.
    /// </summary>
    private const string Sample = """
        [Configuration\Interface]
        Theme=Dark

        [Sessions\Production/web-01]
        HostName=files.example.com
        PortNumber=2222
        UserName=deploy
        FSProtocol=5
        RemoteDirectory=/srv/www
        PublicKeyFile=C%3A%5Ckeys%5Cdeploy.ppk
        Utf=2
        FtpPingType=1

        [Sessions\Production/Databases/db-01]
        HostName=db.example.com
        UserName=postgres
        FSProtocol=5

        [Sessions\public%20mirror]
        HostName=ftp.example.org
        FSProtocol=2
        UserName=anonymous
        """;

    private string Write(string text = Sample)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "WinSCP.ini");
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_folder_hidden_in_a_session_name_becomes_a_folder()
    {
        var root = new WinScpSource(Write()).Read();

        Assert.Equal(["WinSCP", "Production", "Databases"], root.Folders().Select(folder => folder.Name));
        Assert.Equal(["web-01", "db-01", "public mirror"], root.Entries().Select(entry => entry.Name));
    }

    [Fact]
    public void A_session_keeps_its_settings()
    {
        var root = new WinScpSource(Write()).Read();

        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("files.example.com", ConnectionResolver.Host(web).Value);
        Assert.Equal(2222, ConnectionResolver.Port(web).Value);
        Assert.Equal("deploy", ConnectionResolver.User(web).Value);
        Assert.Equal("/srv/www", ConnectionResolver.RemoteDirectory(web).Value);
        Assert.Equal(@"C:\keys\deploy.ppk", ConnectionResolver.Identity(web).Value);
    }

    [Fact]
    public void Scp_and_sftp_are_both_sftp_and_ftp_is_ftp()
    {
        var root = new WinScpSource(Write()).Read();

        Assert.Equal(ConnectionProtocol.Sftp,
            ConnectionResolver.Protocol(root.Entries().First(e => e.Name == "web-01")).Value);
        Assert.Equal(ConnectionProtocol.Ftp,
            ConnectionResolver.Protocol(root.Entries().First(e => e.Name == "public mirror")).Value);
    }

    [Fact]
    public void A_winscp_folder_states_nothing_of_its_own()
    {
        // WinSCP has no inheritance, so its folders group and nothing more. A source must not
        // invent a feature the format does not have.
        var root = new WinScpSource(Write()).Read();
        var production = root.Folders().First(folder => folder.Name == "Production");

        Assert.False(production.Settings.User.IsStated);
        Assert.False(production.Settings.Host.IsStated);

        // Which means a host under it inherits nothing either.
        var db = root.Entries().First(entry => entry.Name == "db-01");
        Assert.False(ConnectionResolver.Port(db).IsSet);
    }

    [Fact]
    public void Saving_keeps_everything_winmux_does_not_model()
    {
        var path = Write();
        var source = new WinScpSource(path);
        var root = source.Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");
        web.Settings = web.Settings with { Host = Inherited<string>.Of("files2.example.com") };

        source.Write(root);

        var text = File.ReadAllText(path);
        Assert.Contains("HostName=files2.example.com", text, StringComparison.Ordinal);
        Assert.Contains("Utf=2", text, StringComparison.Ordinal);
        Assert.Contains("FtpPingType=1", text, StringComparison.Ordinal);
        Assert.Contains("[Configuration\\Interface]", text, StringComparison.Ordinal);
        Assert.Contains("Theme=Dark", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Renaming_a_session_rewrites_its_section_name()
    {
        var path = Write();
        var source = new WinScpSource(path);
        var root = source.Read();
        root.Entries().First(entry => entry.Name == "web-01").Name = "web-02";

        source.Write(root);

        var reread = new WinScpSource(path).Read();
        Assert.Contains(reread.Entries(), entry => entry.Name == "web-02");
        Assert.Equal("files.example.com",
            ConnectionResolver.Host(reread.Entries().First(entry => entry.Name == "web-02")).Value);
    }

    [Fact]
    public void A_name_with_a_separator_in_it_survives_the_round_trip()
    {
        // The reason the names are escaped at all: a session called "a/b" and a session "b" in a
        // folder "a" would otherwise be the same section.
        var path = Write();
        var source = new WinScpSource(path);
        var root = source.Read();
        root.Entries().First(entry => entry.Name == "public mirror").Name = "public/mirror";

        source.Write(root);

        Assert.Contains(new WinScpSource(path).Read().Entries(), entry => entry.Name == "public/mirror");
    }

    [Fact]
    public void The_first_save_leaves_the_original_beside_it()
    {
        var path = Write();
        var source = new WinScpSource(path);
        var root = source.Read();
        root.Entries().First().Name = "changed";

        source.Write(root);

        Assert.True(File.Exists(path + ".winmux-backup"));
    }

    [Fact]
    public void A_file_that_is_not_a_winscp_configuration_is_refused()
    {
        var source = new WinScpSource(Write("[Something]\nKey=Value"));

        Assert.Throws<ConnectionSourceException>(() => source.Read());
    }

    [Fact]
    public void A_missing_file_says_so() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new WinScpSource(Path.Combine(_directory, "absent.ini")).Read());

    [Theory]
    [InlineData("hunter2", "deployfiles.example.com")]
    [InlineData("p@ss w0rd!", "rootdb.example.com")]
    [InlineData("", "userhost")]
    public void A_stored_password_is_decoded(string password, string salt)
    {
        // Encoded here rather than pasted from a real WinSCP.ini, because a password out of
        // somebody's actual configuration is not a thing to commit. The encoder is WinSCP's own
        // algorithm written out, so a decoder that only agrees with itself would still fail the
        // padding and prefix cases below.
        var decoded = WinScpSource.Decode(Encode(password, salt, shift: 3), salt);

        Assert.Equal(password, decoded);
    }

    [Fact]
    public void A_password_from_the_wrong_session_is_refused_rather_than_returned_as_rubbish()
    {
        var stored = Encode("hunter2", "deployfiles.example.com", shift: 0);

        Assert.Null(WinScpSource.Decode(stored, "someone-elsewrong.host"));
    }

    [Fact]
    public void Anything_that_is_not_hex_is_refused()
    {
        Assert.Null(WinScpSource.Decode("not hex at all", "salt"));
        Assert.Null(WinScpSource.Decode("A3", "salt"));
    }

    [Fact]
    public void Credentials_are_read_only_when_asked_for()
    {
        var path = Write(Sample + "\nPassword=" + Encode("hunter2", "anonymousftp.example.org", shift: 2));
        var source = new WinScpSource(path);
        var root = source.Read();

        var found = Assert.Single(source.ReadCredentials(root));

        Assert.Equal("public mirror", found.Node.Name);
        Assert.Equal("hunter2", found.Secret);
    }

    /// <summary>WinSCP's own encoding, so the decoder is checked against the format and not itself.</summary>
    private static string Encode(string password, string salt, int shift)
    {
        const int Magic = 0xA3;
        var text = new StringBuilder();

        // The decoder computes ~(stored ^ magic), so the encoder is (~value) ^ magic.
        void Emit(int value) =>
            text.Append(((~value & 0xFF) ^ Magic).ToString("X2", CultureInfo.InvariantCulture));

        var payload = salt + password;
        Emit(0xFF);
        Emit(payload.Length);
        Emit(shift);
        for (var i = 0; i < shift; i++) Emit(0x5A);
        foreach (var character in payload) Emit(character);
        return text.ToString();
    }
}
