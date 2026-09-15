using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// A remote file-browser pane surviving a session, and never carrying a password while it does.
/// </summary>
public sealed class RemoteFileBrowserTargetTests
{
    private static LaunchProfile Profile(
        ProfileKind kind = ProfileKind.Sftp,
        string host = "build.example.com",
        int port = 0,
        string user = "deploy",
        string directory = "") =>
        new()
        {
            Id = "remote", Name = "build files", Program = "", Kind = kind,
            Host = host, Port = port, User = user, WorkingDirectory = directory,
        };

    [Fact]
    public void An_sftp_profile_opens_a_file_browser_not_a_program()
    {
        // The whole point of the connection kinds: they pick an existing provider.
        Assert.Equal(PaneKind.FileBrowser, Profile().PaneKind);
        Assert.Equal(PaneKind.FileBrowser, Profile(ProfileKind.Ftp).PaneKind);
    }

    [Fact]
    public void An_sftp_connection_has_no_command_line_and_says_so_usefully()
    {
        // It is still a connection — it needs a host, not a program — but there is nothing to run.
        Assert.True(RemoteConnection.IsConnection(ProfileKind.Sftp));
        Assert.False(RemoteConnection.LaunchesProgram(ProfileKind.Sftp));

        var error = Assert.Throws<ArgumentException>(() => RemoteConnection.Resolve(Profile()));
        Assert.Contains("file-browser", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sftp_defaults_to_the_ssh_port_and_ftp_to_twenty_one()
    {
        Assert.Equal(22, RemoteFileBrowserTarget.From(Profile())!.EffectivePort);
        Assert.Equal(21, RemoteFileBrowserTarget.From(Profile(ProfileKind.Ftp))!.EffectivePort);
        Assert.Equal(2222, RemoteFileBrowserTarget.From(Profile(port: 2222))!.EffectivePort);
    }

    [Fact]
    public void A_target_survives_the_descriptor_round_trip()
    {
        var target = RemoteFileBrowserTarget.From(Profile(port: 2222))!;

        var descriptor = new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "build files",
            Extras = target.ToExtras("/srv/www"),
        };

        var restored = RemoteFileBrowserTarget.From(descriptor);

        Assert.NotNull(restored);
        Assert.Equal("sftp", restored.Scheme);
        Assert.Equal("build.example.com", restored.Host);
        Assert.Equal(2222, restored.Port);
        Assert.Equal("deploy", restored.User);
        Assert.Equal("/srv/www", descriptor.Extras["current_directory"]);
    }

    [Fact]
    public void No_password_is_ever_put_in_the_descriptor()
    {
        // The descriptor goes into the session file, which is plain text by design (ADR 0006).
        // Asserted so that adding a field later cannot quietly start writing secrets into it.
        var extras = RemoteFileBrowserTarget.From(Profile())!.ToExtras("/");

        foreach (var key in extras.Keys)
        {
            Assert.DoesNotContain("password", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", key, StringComparison.OrdinalIgnoreCase);
        }

        // The exact set, so a new key has to be added here deliberately and looked at.
        Assert.Equal(
            ["current_directory", "remote_host", "remote_port", "remote_scheme", "remote_user"],
            extras.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_key_file_travels_with_the_connection_but_the_key_never_does()
    {
        var profile = Profile() with { Identity = @"C:\keys\id_ed25519" };
        var target = RemoteFileBrowserTarget.From(profile)!;

        Assert.True(target.UsesKey);

        var extras = target.ToExtras("/srv");
        Assert.Equal(@"C:\keys\id_ed25519", extras["remote_identity"]);

        // A path, not a secret. The key itself is read by SSH.NET from disk and never stored here.
        var restored = RemoteFileBrowserTarget.From(new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser, Title = "t", Extras = extras,
        });

        Assert.Equal(@"C:\keys\id_ed25519", restored!.Identity);
        Assert.True(restored.UsesKey);
    }

    [Fact]
    public void A_connection_without_a_key_carries_no_empty_identity()
    {
        // An empty key entry in the session file would be noise, and would read as "configured".
        Assert.DoesNotContain("remote_identity", RemoteFileBrowserTarget.From(Profile())!.ToExtras("/").Keys);
    }

    [Fact]
    public void Ftp_never_uses_a_key_even_if_one_is_configured()
    {
        // There is no such thing as key authentication in FTP; honouring the field would mean
        // silently ignoring the password the user actually needs to give.
        var profile = Profile(ProfileKind.Ftp) with { Identity = @"C:\keys\id_ed25519" };

        Assert.False(RemoteFileBrowserTarget.From(profile)!.UsesKey);
    }

    [Fact]
    public void An_ordinary_local_descriptor_is_not_remote()
    {
        var descriptor = new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["current_directory"] = @"C:\Users",
            },
        };

        Assert.Null(RemoteFileBrowserTarget.From(descriptor));
    }

    [Fact]
    public void A_descriptor_naming_a_scheme_we_do_not_speak_is_not_remote()
    {
        // Forward compatibility: an unknown kind round-trips through TOML untouched (ADR 0012), so
        // it can appear here. Treating it as remote would connect somewhere with the wrong protocol.
        var descriptor = new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["remote_scheme"] = "webdav",
                ["remote_host"] = "example.com",
            },
        };

        Assert.Null(RemoteFileBrowserTarget.From(descriptor));
    }

    [Fact]
    public void A_credential_target_distinguishes_the_protocol_and_the_user()
    {
        var sftp = RemoteFileBrowserTarget.From(Profile())!;
        var ftp = RemoteFileBrowserTarget.From(Profile(ProfileKind.Ftp))!;
        var other = RemoteFileBrowserTarget.From(Profile(user: "root"))!;

        Assert.NotEqual(sftp.CredentialTargetName, ftp.CredentialTargetName);
        Assert.NotEqual(sftp.CredentialTargetName, other.CredentialTargetName);
        Assert.StartsWith("WinMux", sftp.CredentialTargetName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_display_name_reads_like_a_url()
    {
        Assert.Equal("sftp://deploy@build.example.com", RemoteFileBrowserTarget.From(Profile())!.Display);
        Assert.Equal("sftp://build.example.com", RemoteFileBrowserTarget.From(Profile(user: ""))!.Display);
    }

    [Fact]
    public void Ftp_is_flagged_as_clear_text_and_sftp_is_not()
    {
        // Drives the warning in the credential dialog. Getting this backwards would tell people
        // their encrypted connection was insecure, or worse, the reverse.
        Assert.True(RemoteFileBrowserTarget.From(Profile(ProfileKind.Ftp))!.IsClearText);
        Assert.False(RemoteFileBrowserTarget.From(Profile())!.IsClearText);
    }

    [Fact]
    public void A_connection_profile_with_no_host_describes_no_target()
    {
        Assert.Null(RemoteFileBrowserTarget.From(Profile(host: "   ")));
    }
}

/// <summary>Connection fields taking part in profile equality, which decides whether edits save.</summary>
public sealed class ConnectionProfileEqualityTests
{
    private static LaunchProfile Base() => new()
    {
        Id = "build", Name = "build", Program = "", Kind = ProfileKind.Sftp,
        Host = "build.example.com", Port = 22, User = "deploy", Identity = @"C:\keys\id",
    };

    [Theory]
    [MemberData(nameof(Variations))]
    public void Changing_a_connection_field_makes_the_profile_different(LaunchProfile changed)
    {
        // These were missing from Equals when connections were added, so the settings UI asked
        // "did this change?", heard no, and discarded the edit. A record's generated equality would
        // have caught it; the hand-written one has to be kept honest by this test.
        Assert.NotEqual(Base(), changed);
        Assert.NotEqual(Base().GetHashCode(), changed.GetHashCode());
    }

    public static TheoryData<LaunchProfile> Variations() =>
    [
        Base() with { Host = "other.example.com" },
        Base() with { Port = 2222 },
        Base() with { User = "root" },
        Base() with { Identity = @"C:\keys\other" },
    ];

    [Fact]
    public void An_unchanged_profile_still_compares_equal()
    {
        Assert.Equal(Base(), Base());
        Assert.Equal(Base().GetHashCode(), Base().GetHashCode());
    }
}
