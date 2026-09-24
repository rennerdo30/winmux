using WinMux.Core.Connections;

namespace WinMux.Tests;

/// <summary>
/// MobaXterm's bookmarks: folders as sections, sessions as percent-separated lines with dozens of
/// positional fields — the shape where a writer that reassembles only what it understood does the
/// most damage.
/// </summary>
public class MobaXtermSourceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "winmux-moba-" + Guid.NewGuid().ToString("N"));

    private const string Sample = """
        [Misc]
        Theme=Dark

        [Bookmarks]
        SubRep=
        ImgNum=42
        0=jump box%#109#0%jump.example.com%22%root%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%

        [Bookmarks_2]
        SubRep=Production
        ImgNum=41
        0=web-01%#109#0%files.example.com%2222%deploy%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%0%0%1%
        1=desk-01%#109#4%10.0.0.50%3389%CORP\admin%%-1%-1%%%3389%%0%0%0%

        [Bookmarks_3]
        SubRep=Production\Databases
        ImgNum=41
        0=db-01%#109#0%db.example.com%22%postgres%%-1%-1%%%22%%0%0%0%
        """;

    private string Write(string text = Sample)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "MobaXterm.ini");
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SubRep_becomes_a_folder_tree()
    {
        var root = new MobaXtermSource(Write()).Read();

        Assert.Equal(["MobaXterm", "Production", "Databases"], root.Folders().Select(folder => folder.Name));
    }

    [Fact]
    public void Sessions_land_in_the_folder_their_section_names()
    {
        var root = new MobaXtermSource(Write()).Read();

        Assert.Equal("MobaXterm / jump box",
            root.Entries().First(entry => entry.Name == "jump box").Path);
        Assert.Equal("MobaXterm / Production / web-01",
            root.Entries().First(entry => entry.Name == "web-01").Path);
        Assert.Equal("MobaXterm / Production / Databases / db-01",
            root.Entries().First(entry => entry.Name == "db-01").Path);
    }

    [Fact]
    public void A_session_keeps_its_settings()
    {
        var root = new MobaXtermSource(Write()).Read();

        var web = root.Entries().First(entry => entry.Name == "web-01");

        Assert.Equal("files.example.com", ConnectionResolver.Host(web).Value);
        Assert.Equal(2222, ConnectionResolver.Port(web).Value);
        Assert.Equal("deploy", ConnectionResolver.User(web).Value);
        Assert.Equal(ConnectionProtocol.Ssh, ConnectionResolver.Protocol(web).Value);
    }

    [Fact]
    public void An_rdp_bookmark_is_not_called_ssh()
    {
        var root = new MobaXtermSource(Write()).Read();

        var desk = root.Entries().First(entry => entry.Name == "desk-01");

        Assert.Equal(ConnectionProtocol.Rdp, ConnectionResolver.Protocol(desk).Value);
        Assert.Equal("10.0.0.50", ConnectionResolver.Host(desk).Value);
        Assert.Equal(@"CORP\admin", ConnectionResolver.User(desk).Value);
    }

    [Fact]
    public void Saving_keeps_every_positional_field_after_the_ones_winmux_reads()
    {
        // The test this source exists to pass. Dozens of fields per line are terminal preferences,
        // macros and X11 settings, and rebuilding the line from what was understood would drop all
        // of them the first time somebody renamed a bookmark.
        var path = Write();
        var source = new MobaXtermSource(path);
        var root = source.Read();
        var web = root.Entries().First(entry => entry.Name == "web-01");
        web.Name = "web-01 (renamed)";
        web.Settings = web.Settings with { Host = Inherited<string>.Of("files2.example.com") };

        source.Write(root);

        var line = File.ReadAllLines(path).First(text => text.StartsWith("0=web-01 (renamed)", StringComparison.Ordinal));

        Assert.Equal(
            "0=web-01 (renamed)%#109#0%files2.example.com%2222%deploy%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%0%0%1%",
            line);
    }

    [Fact]
    public void Saving_keeps_the_rest_of_the_file()
    {
        var path = Write();
        var source = new MobaXtermSource(path);
        source.Write(source.Read());

        var text = File.ReadAllText(path);

        Assert.Contains("[Misc]", text, StringComparison.Ordinal);
        Assert.Contains("Theme=Dark", text, StringComparison.Ordinal);
        Assert.Contains("ImgNum=42", text, StringComparison.Ordinal);
        Assert.Contains("SubRep=Production\\Databases", text, StringComparison.Ordinal);
    }

    [Fact]
    public void There_are_no_passwords_in_this_file()
    {
        // MobaXterm keeps them elsewhere and bound to the machine. Saying so is better than an
        // empty list that could mean either "none" or "did not look".
        var source = new MobaXtermSource(Write());

        Assert.Empty(source.ReadCredentials(source.Read()));
    }

    [Fact]
    public void A_file_without_bookmarks_is_refused() =>
        Assert.Throws<ConnectionSourceException>(() => new MobaXtermSource(Write("[Misc]\nTheme=Dark")).Read());

    [Fact]
    public void A_missing_file_says_so() =>
        Assert.Throws<ConnectionSourceException>(() =>
            new MobaXtermSource(Path.Combine(_directory, "absent.ini")).Read());

    [Fact]
    public void MobaXterm_must_not_be_written_underneath() =>
        Assert.False(new MobaXtermSource(Write()).CanWriteWhileOtherToolRuns);
}
