using WinMux.Core.Model;
using WinMux.Core.Settings;

namespace WinMux.Tests;

/// <summary>
/// Profiles are the list a user curates, so the failure modes that matter are about not losing it:
/// one broken entry must not cost the rest, and nothing here may stop WinMux starting.
/// </summary>
public class ProfilesFileTests
{
    [Fact]
    public void A_first_run_gets_the_shipped_profiles()
    {
        var result = ProfilesFile.Load(Path.Combine(Path.GetTempPath(), "winmux-none-" + Guid.NewGuid().ToString("N")));

        Assert.Null(result.Warning);
        Assert.Equal(LaunchProfile.Defaults, result.Profiles);
        Assert.Contains(result.Profiles, p => p.Kind == ProfileKind.Application);
    }

    [Fact]
    public void Every_field_survives_a_round_trip()
    {
        var profiles = new List<LaunchProfile>
        {
            new()
            {
                Id = "vs-code", Name = "VS Code", Kind = ProfileKind.Application,
                Program = @"C:\Program Files\Microsoft VS Code\Code.exe",
                Args = ["--new-window", "."],
                WorkingDirectory = @"C:\work",
                Strategy = HostStrategy.Attach,
                WindowClass = "Chrome_WidgetWin_1",
                TitleContains = "Visual Studio Code",
                Source = "Start Menu",
            },
        };

        var restored = ProfilesFile.Parse(ProfilesFile.Serialize(profiles)).Profiles;

        Assert.Equal(profiles[0], Assert.Single(restored));
    }

    [Fact]
    public void Windows_paths_keep_their_backslashes()
    {
        // ADR 0006's reason for literal strings. A doubled backslash here would be a broken path.
        var text = ProfilesFile.Serialize([Profile("x", @"C:\Program Files\thing\app.exe")]);

        Assert.Contains(@"'C:\Program Files\thing\app.exe'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_containing_an_apostrophe_still_round_trips()
    {
        // A TOML literal string cannot contain a single quote, so this has to fall back.
        var profile = Profile("odd", @"C:\Bob's Tools\run.exe");

        var restored = Assert.Single(ProfilesFile.Parse(ProfilesFile.Serialize([profile])).Profiles);

        Assert.Equal(@"C:\Bob's Tools\run.exe", restored.Program);
    }

    [Fact]
    public void One_broken_entry_does_not_cost_the_others()
    {
        var text = """
            [[profiles]]
            name    = 'good'
            program = 'good.exe'

            [[profiles]]
            name    = 'no program'

            [[profiles]]
            name    = 'also good'
            program = 'other.exe'
            """;

        var result = ProfilesFile.Parse(text);

        Assert.Equal(2, result.Profiles.Count);
        Assert.Contains("no program", result.Warning);
    }

    [Fact]
    public void A_duplicate_id_is_skipped_rather_than_shadowing_the_first()
    {
        var text = """
            [[profiles]]
            id      = 'dup'
            name    = 'first'
            program = 'a.exe'

            [[profiles]]
            id      = 'dup'
            name    = 'second'
            program = 'b.exe'
            """;

        var result = ProfilesFile.Parse(text);

        Assert.Equal("first", Assert.Single(result.Profiles).Name);
        Assert.Contains("repeats the id", result.Warning);
    }

    [Fact]
    public void A_file_with_nothing_usable_falls_back_and_says_so()
    {
        var result = ProfilesFile.Parse("[[profiles]]\nname = 'broken'");

        Assert.Equal(LaunchProfile.Defaults, result.Profiles);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void A_file_that_is_not_TOML_still_starts_WinMux()
    {
        var result = ProfilesFile.Parse("}{ nonsense");

        Assert.Equal(LaunchProfile.Defaults, result.Profiles);
        Assert.Contains("not valid TOML", result.Warning);
    }

    [Fact]
    public void An_id_is_generated_from_the_name_when_one_is_missing()
    {
        var result = ProfilesFile.Parse("[[profiles]]\nname = 'My  Cool App!'\nprogram = 'a.exe'");

        Assert.Equal("my-cool-app", Assert.Single(result.Profiles).Id);
    }

    [Theory]
    [InlineData("Visual Studio Code", "visual-studio-code")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("!!!", "profile")]
    [InlineData("7-Zip", "7-zip")]
    public void Generated_ids_are_stable_and_safe(string name, string expected)
    {
        Assert.Equal(expected, LaunchProfile.MakeId(name));
    }

    [Fact]
    public void A_terminal_profile_opens_a_terminal_pane_and_an_application_opens_a_foreign_pane()
    {
        // The whole point of one concept covering both: the profile picks the provider.
        Assert.Equal(PaneKind.Terminal, Profile("t", "cmd.exe").PaneKind);
        Assert.Equal(PaneKind.ForeignApp, (Profile("a", "app.exe") with { Kind = ProfileKind.Application }).PaneKind);
    }

    [Fact]
    public void Saving_creates_the_directory_and_replaces_atomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winmux-profiles-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, ProfilesFile.FileName);
        try
        {
            ProfilesFile.Save(path, [Profile("one", "a.exe")]);
            ProfilesFile.Save(path, [Profile("two", "b.exe")]);

            Assert.Equal("two", Assert.Single(ProfilesFile.Load(path).Profiles).Name);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Two_profiles_differing_only_in_arguments_are_not_equal()
    {
        // A record compares a list member by reference. The settings UI asks "did this change?"
        // before writing, so the generated behaviour would have dropped edits silently.
        var a = Profile("x", "a.exe") with { Args = ["--one"] };
        var b = Profile("x", "a.exe") with { Args = ["--two"] };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Two_profiles_with_equal_arguments_are_equal_and_hash_alike()
    {
        var a = Profile("x", "a.exe") with { Args = ["--one", "--two"] };
        var b = Profile("x", "a.exe") with { Args = ["--one", "--two"] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    private static LaunchProfile Profile(string name, string program) => new()
    {
        Id = LaunchProfile.MakeId(name),
        Name = name,
        Program = program,
    };
}
