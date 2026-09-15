using WinMux.Core.Model;
using WinMux.Core.Settings;

namespace WinMux.Tests;

/// <summary>
/// Turning a saved host into a command line.
///
/// A connection is a <see cref="LaunchProfile"/> like any other — CLAUDE.md section 5a requires one
/// list behind every surface that opens a pane — so the only new thing is how host, port, user and
/// identity become arguments. That is worth testing on its own because argument construction is
/// where quoting mistakes live, and a wrong one here connects somewhere else.
/// </summary>
public sealed class RemoteConnectionTests
{
    private static LaunchProfile Ssh(
        string host = "build.example.com",
        string user = "",
        int port = 0,
        string identity = "",
        params string[] extra) =>
        new()
        {
            Id = "ssh", Name = "build", Program = "", Kind = ProfileKind.Ssh,
            Host = host, User = user, Port = port, Identity = identity, Args = extra,
        };

    private static LaunchProfile Rdp(string host = "desk.example.com", int port = 0, string user = "") =>
        new()
        {
            Id = "rdp", Name = "desk", Program = "", Kind = ProfileKind.Rdp,
            Host = host, Port = port, User = user,
        };

    [Fact]
    public void An_ssh_connection_runs_the_openssh_client()
    {
        var command = RemoteConnection.Resolve(Ssh());

        Assert.Equal("ssh.exe", command.Program);
        Assert.Equal(["build.example.com"], command.Args);
    }

    [Fact]
    public void A_user_is_joined_to_the_host()
    {
        Assert.Equal(["deploy@build.example.com"], RemoteConnection.Resolve(Ssh(user: "deploy")).Args);
    }

    [Fact]
    public void The_default_ssh_port_is_left_off_the_command_line()
    {
        // An explicit -p 22 in every connection is noise, and it overrides whatever the user's
        // ssh_config says for that host.
        Assert.Equal(["build.example.com"], RemoteConnection.Resolve(Ssh(port: 22)).Args);
        Assert.Equal(["build.example.com"], RemoteConnection.Resolve(Ssh(port: 0)).Args);
    }

    [Fact]
    public void A_non_default_port_is_passed()
    {
        Assert.Equal(["-p", "2222", "build.example.com"], RemoteConnection.Resolve(Ssh(port: 2222)).Args);
    }

    [Fact]
    public void An_identity_file_is_passed_as_one_argument()
    {
        // As a list entry, not a quoted fragment of a command line: quoting it here would put the
        // quotes into the argument and ssh would look for a key file whose name contains them.
        var args = RemoteConnection.Resolve(Ssh(identity: @"C:\keys\my key")).Args;

        Assert.Equal(["-i", @"C:\keys\my key", "build.example.com"], args);
    }

    [Fact]
    public void Extra_arguments_come_last_so_they_can_override()
    {
        var args = RemoteConnection.Resolve(Ssh(user: "deploy", extra: ["-A"])).Args;

        Assert.Equal(["deploy@build.example.com", "-A"], args);
    }

    [Fact]
    public void An_rdp_connection_runs_the_windows_client()
    {
        var command = RemoteConnection.Resolve(Rdp());

        Assert.Equal("mstsc.exe", command.Program);
        Assert.Equal(["/v:desk.example.com"], command.Args);
    }

    [Fact]
    public void An_rdp_port_goes_in_the_address_not_a_switch()
    {
        Assert.Equal(["/v:desk.example.com:3390"], RemoteConnection.Resolve(Rdp(port: 3390)).Args);
        Assert.Equal(["/v:desk.example.com"], RemoteConnection.Resolve(Rdp(port: 3389)).Args);
    }

    [Fact]
    public void An_rdp_user_is_not_put_on_the_command_line()
    {
        // mstsc has no switch for one, and a password would have to live somewhere; both belong in
        // Windows Credential Manager, which mstsc already uses.
        Assert.Equal(["/v:desk.example.com"], RemoteConnection.Resolve(Rdp(user: "admin")).Args);
    }

    [Fact]
    public void A_connection_with_no_host_is_refused_rather_than_run()
    {
        Assert.Throws<ArgumentException>(() => RemoteConnection.Resolve(Ssh(host: "  ")));
    }

    [Fact]
    public void An_ordinary_profile_is_not_a_connection()
    {
        var profile = new LaunchProfile { Id = "cmd", Name = "cmd", Program = "cmd.exe" };

        Assert.False(RemoteConnection.IsConnection(profile.Kind));
        Assert.Throws<ArgumentException>(() => RemoteConnection.Resolve(profile));
    }

    [Fact]
    public void Ssh_opens_a_terminal_and_rdp_opens_a_hosted_window()
    {
        // The connection kinds pick an existing provider; they are not new pane kinds.
        Assert.Equal(PaneKind.Terminal, Ssh().PaneKind);
        Assert.Equal(PaneKind.ForeignApp, Rdp().PaneKind);
    }

    [Theory]
    [InlineData("build.example.com", "", 0, "build.example.com")]
    [InlineData("build.example.com", "deploy", 0, "deploy@build.example.com")]
    [InlineData("build.example.com", "deploy", 2222, "deploy@build.example.com:2222")]
    [InlineData("build.example.com", "", 22, "build.example.com")]
    public void A_connection_describes_itself_without_its_defaults(
        string host, string user, int port, string expected)
    {
        Assert.Equal(expected, RemoteConnection.Describe(Ssh(host, user, port)));
    }
}

/// <summary>Connections surviving the profiles file, which is hand-written like the settings one.</summary>
public sealed class ConnectionProfilesFileTests
{
    private static LaunchProfile RoundTrip(LaunchProfile profile)
    {
        var text = ProfilesFile.Serialize([profile]);
        return ProfilesFile.Parse(text, "test").Profiles.Single();
    }

    [Fact]
    public void An_ssh_connection_survives_being_written_and_read()
    {
        var profile = new LaunchProfile
        {
            Id = "build", Name = "build box", Kind = ProfileKind.Ssh, Program = "",
            Host = "build.example.com", Port = 2222, User = "deploy", Identity = @"C:\keys\id_ed25519",
        };

        var restored = RoundTrip(profile);

        Assert.Equal(ProfileKind.Ssh, restored.Kind);
        Assert.Equal("build.example.com", restored.Host);
        Assert.Equal(2222, restored.Port);
        Assert.Equal("deploy", restored.User);
        Assert.Equal(@"C:\keys\id_ed25519", restored.Identity);
    }

    [Fact]
    public void An_rdp_connection_survives_too()
    {
        var profile = new LaunchProfile
        {
            Id = "desk", Name = "desk", Kind = ProfileKind.Rdp, Program = "", Host = "desk.example.com",
        };

        var restored = RoundTrip(profile);

        Assert.Equal(ProfileKind.Rdp, restored.Kind);
        Assert.Equal("desk.example.com", restored.Host);
    }

    [Fact]
    public void A_connection_needs_no_program_field()
    {
        // The reader used to require one for every profile; a connection derives it from the host.
        var text = """
            [[profiles]]
            id   = 'build'
            name = 'build box'
            kind = 'ssh'
            host = 'build.example.com'
            """;

        var result = ProfilesFile.Parse(text, "test");

        Assert.Null(result.Warning);
        Assert.Equal("build.example.com", result.Profiles.Single().Host);
    }

    [Fact]
    public void A_connection_with_no_host_is_skipped_and_explained()
    {
        var text = """
            [[profiles]]
            name = 'nowhere'
            kind = 'ssh'

            [[profiles]]
            name = 'cmd'
            program = 'cmd.exe'
            """;

        var result = ProfilesFile.Parse(text, "test");

        Assert.Single(result.Profiles);
        Assert.NotNull(result.Warning);
        Assert.Contains("host", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_impossible_port_is_refused_and_the_default_used()
    {
        var text = """
            [[profiles]]
            name = 'build'
            kind = 'ssh'
            host = 'build.example.com'
            port = 70000
            """;

        var result = ProfilesFile.Parse(text, "test");

        Assert.Equal(0, result.Profiles.Single().Port);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void No_password_field_is_ever_written()
    {
        // Passwords belong in Windows Credential Manager. Asserted so that adding one to the model
        // later cannot quietly start writing secrets into a plain-text file.
        var text = ProfilesFile.Serialize(
        [
            new LaunchProfile
            {
                Id = "build", Name = "build", Kind = ProfileKind.Ssh, Program = "",
                Host = "build.example.com", User = "deploy",
            },
        ]);

        // A key assignment, not any mention: the file's header comment explains that no password is
        // stored, and a bare substring check would fail on the explanation itself.
        var keys = text
            .Split(Environment.NewLine)
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith('#') && line.Contains('='))
            .Select(line => line[..line.IndexOf('=')].Trim().ToLowerInvariant());

        Assert.DoesNotContain("password", keys);
        Assert.DoesNotContain("secret", keys);
        Assert.DoesNotContain("credential", keys);
    }
}
