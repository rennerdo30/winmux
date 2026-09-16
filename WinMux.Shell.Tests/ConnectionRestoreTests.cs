using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Core.Settings;
using WinMux.Shell.FileBrowser.Remote;
using WinMux.Shell.Panes;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a saved connection brings back when a session is restored.
///
/// Priority 1 of CLAUDE.md section 1 is that restore works, and a connection is where "works" is
/// easiest to get *almost* right: the pane comes back, pointed at nothing. A Remote Desktop pane
/// that restores without its <c>/v:</c> argument is a blank window that looks like a fault on the
/// server, and an SFTP pane that forgets its host is a local file browser wearing a server's name.
///
/// What is deliberately not restored is the far side. RDP reconnects to whatever session the server
/// kept; SFTP reopens a directory. Neither is WinMux's state to hold — and no descriptor ever
/// carries a password, which is Windows Credential Manager's job.
/// </summary>
public sealed class ConnectionRestoreTests
{
    private static Pane RoundTrip(Pane pane)
    {
        var tree = new LayoutTree(pane) { Bounds = new Rect(0, 0, 800, 600) };
        var text = SessionFile.Serialize(new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, "w")],
        });

        return SessionMapper.FromSnapshot(SessionFile.Deserialize(text).Windows[0]).Panes.First();
    }

    private static string Serialize(Pane pane)
    {
        var tree = new LayoutTree(pane) { Bounds = new Rect(0, 0, 800, 600) };
        return SessionFile.Serialize(new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, "w")],
        });
    }

    [Fact]
    public void A_remote_desktop_pane_restores_pointed_at_the_same_host()
    {
        var profile = new LaunchProfile
        {
            Id = "desk", Name = "the build box", Program = "", Kind = ProfileKind.Rdp,
            Host = "desk.example.com", Port = 3390,
        };

        var restored = RoundTrip(ProfilePaneFactory.Create(profile));

        Assert.Equal(PaneKind.ForeignApp, restored.Kind);
        Assert.Contains("mstsc", restored.Restore.Program, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/v:desk.example.com:3390", restored.Restore.Args);
        Assert.Equal("the build box", restored.Restore.Title);
    }

    [Fact]
    public void An_ssh_pane_restores_with_its_user_port_and_identity()
    {
        var profile = new LaunchProfile
        {
            Id = "build", Name = "build", Program = "", Kind = ProfileKind.Ssh,
            Host = "build.example.com", Port = 2222, User = "deploy",
            Identity = @"C:\keys\id_ed25519",
        };

        var restored = RoundTrip(ProfilePaneFactory.Create(profile));

        Assert.Equal(PaneKind.Terminal, restored.Kind);
        Assert.Contains("ssh", restored.Restore.Program, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ["-p", "2222", "-i", @"C:\keys\id_ed25519", "deploy@build.example.com"],
            restored.Restore.Args);
    }

    [Fact]
    public void An_sftp_pane_restores_its_host_and_the_directory_it_was_in()
    {
        var profile = new LaunchProfile
        {
            Id = "files", Name = "server files", Program = "", Kind = ProfileKind.Sftp,
            Host = "build.example.com", Port = 2222, User = "deploy",
            WorkingDirectory = "/srv/www",
        };

        var pane = ProfilePaneFactory.Create(profile);

        // Stand in for the user having browsed somewhere before the session was saved.
        pane.Restore = pane.Restore with
        {
            Extras = new Dictionary<string, string>(pane.Restore.Extras, StringComparer.Ordinal)
            {
                [FileBrowserModelAccess.CurrentDirectoryKey] = "/srv/www/logs",
            },
        };

        var restored = RoundTrip(pane);
        var target = RemoteFileBrowserTarget.From(restored.Restore);

        Assert.Equal(PaneKind.FileBrowser, restored.Kind);
        Assert.NotNull(target);
        Assert.Equal("sftp", target.Scheme);
        Assert.Equal("build.example.com", target.Host);
        Assert.Equal(2222, target.Port);
        Assert.Equal("deploy", target.User);
        Assert.Equal("/srv/www/logs", restored.Restore.Extras[FileBrowserModelAccess.CurrentDirectoryKey]);
    }

    [Fact]
    public void An_ftp_pane_restores_as_ftp_and_not_as_something_else()
    {
        var profile = new LaunchProfile
        {
            Id = "drop", Name = "drop box", Program = "", Kind = ProfileKind.Ftp, Host = "ftp.example.com",
        };

        var target = RemoteFileBrowserTarget.From(RoundTrip(ProfilePaneFactory.Create(profile)).Restore);

        Assert.NotNull(target);
        Assert.Equal("ftp", target.Scheme);
        Assert.Equal(21, target.EffectivePort);
    }

    [Theory]
    [InlineData(ProfileKind.Rdp)]
    [InlineData(ProfileKind.Ssh)]
    [InlineData(ProfileKind.Sftp)]
    [InlineData(ProfileKind.Ftp)]
    public void No_connection_writes_a_password_into_the_session_file(ProfileKind kind)
    {
        // The session file is plain text by design (ADR 0006) and gets copied between machines.
        var profile = new LaunchProfile
        {
            Id = "c", Name = "c", Program = "", Kind = kind, Host = "host.example.com", User = "someone",
        };

        var text = Serialize(ProfilePaneFactory.Create(profile));

        foreach (var forbidden in new[] { "password", "secret", "passphrase" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>The extras key the file browser stores its directory under, without reaching into it.</summary>
internal static class FileBrowserModelAccess
{
    public const string CurrentDirectoryKey = "current_directory";
}
