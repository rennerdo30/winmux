using WinMux.Core.Layout;
using WinMux.Core.Settings;

namespace WinMux.Tests;

/// <summary>
/// Settings are preferences, not work, and the round trip matters less than the failure mode:
/// a typo in a preferences file must never stop WinMux starting, and must never change behaviour
/// silently either.
/// </summary>
public class SettingsFileTests
{
    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        var result = SettingsFile.Load(Path.Combine(Path.GetTempPath(), "winmux-no-such-" + Guid.NewGuid().ToString("N")));

        Assert.Null(result.Warning);
        Assert.Equal(WinMuxSettings.Defaults, result.Settings);
    }

    [Fact]
    public void Every_setting_survives_a_round_trip()
    {
        var settings = new WinMuxSettings
        {
            Theme = ThemePreference.Light,
            DefaultTerminal = "powershell",
            DefaultTabPlacement = TabStripPlacement.Left,
            ConfirmBeforeClosingPanes = false,
        };

        var restored = SettingsFile.Parse(SettingsFile.Serialize(settings)).Settings;

        Assert.Equal(settings, restored);
    }

    [Fact]
    public void An_unreadable_value_falls_back_and_names_the_key()
    {
        // The point of the whole design: never start differently without saying so.
        var result = SettingsFile.Parse("theme = 'aubergine'");

        Assert.Equal(ThemePreference.System, result.Settings.Theme);
        Assert.NotNull(result.Warning);
        Assert.Contains("theme", result.Warning);
        Assert.Contains("aubergine", result.Warning);
        Assert.Contains("\"dark\"", result.Warning);
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_reported_too()
    {
        var result = SettingsFile.Parse("confirm_before_closing_panes = 'yes please'");

        Assert.True(result.Settings.ConfirmBeforeClosingPanes);
        Assert.Contains("true or false", result.Warning);
    }

    [Fact]
    public void A_file_that_is_not_TOML_at_all_still_starts_WinMux()
    {
        var result = SettingsFile.Parse("{ this is not toml ]");

        Assert.Equal(WinMuxSettings.Defaults, result.Settings);
        Assert.Contains("not valid TOML", result.Warning);
    }

    [Fact]
    public void Several_bad_keys_are_all_reported_at_once()
    {
        // Fixing settings one error per launch would be its own small torture.
        var result = SettingsFile.Parse("theme = 'aubergine'\ndefault_tab_placement = 'sideways'");

        Assert.Contains("theme", result.Warning);
        Assert.Contains("default_tab_placement", result.Warning);
    }

    [Fact]
    public void Keys_that_are_absent_keep_their_defaults()
    {
        var result = SettingsFile.Parse("theme = 'dark'");

        Assert.Equal(ThemePreference.Dark, result.Settings.Theme);
        Assert.Equal(WinMuxSettings.Defaults.DefaultTerminal, result.Settings.DefaultTerminal);
        Assert.Equal(WinMuxSettings.Defaults.DefaultTabPlacement, result.Settings.DefaultTabPlacement);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void The_written_file_is_readable_by_a_person()
    {
        // It is hand-editable by design, so it carries its own documentation like a session does.
        var text = SettingsFile.Serialize(WinMuxSettings.Defaults);

        Assert.Contains("# WinMux settings", text);
        Assert.Contains("theme", text);
        Assert.Contains("cmd, windows-powershell, powershell, wsl", text);
    }

    [Fact]
    public void Saving_creates_the_directory_and_replaces_the_file_atomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winmux-settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "nested", SettingsFile.FileName);
        try
        {
            SettingsFile.Save(path, WinMuxSettings.Defaults with { Theme = ThemePreference.Dark });
            SettingsFile.Save(path, WinMuxSettings.Defaults with { Theme = ThemePreference.Light });

            Assert.Equal(ThemePreference.Light, SettingsFile.Load(path).Settings.Theme);
            Assert.False(File.Exists(path + ".tmp"), "the temporary file should not survive a save");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
