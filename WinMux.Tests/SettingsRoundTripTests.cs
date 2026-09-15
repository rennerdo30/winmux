using WinMux.Core.Layout;
using WinMux.Core.Settings;
using WinMux.Core.Update;

namespace WinMux.Tests;

/// <summary>
/// Settings surviving a trip through the file.
///
/// Worth a test because the file is hand-written rather than serialised from the type, so adding a
/// property to <see cref="WinMuxSettings"/> and forgetting the reader or the writer compiles
/// perfectly and silently resets that setting on every launch. That had already happened twice —
/// to the update settings — before anything checked.
/// </summary>
public sealed class SettingsRoundTripTests
{
    /// <summary>Every setting set to something other than its default.</summary>
    private static readonly WinMuxSettings NonDefault = new()
    {
        Theme = ThemePreference.Light,
        DefaultTerminal = "powershell",
        DefaultTabPlacement = TabStripPlacement.Left,
        ConfirmBeforeClosingPanes = false,
        CheckForUpdates = false,
        UpdateChannel = UpdateChannel.Prerelease,
        TerminalFontFamily = "Consolas",
        TerminalFontSize = 18,
    };

    private static WinMuxSettings RoundTrip(WinMuxSettings settings) =>
        SettingsFile.Parse(SettingsFile.Serialize(settings), "test").Settings;

    [Fact]
    public void Every_setting_survives_being_written_and_read()
    {
        // Each of these differs from the default, so a property the file forgets comes back as the
        // default and fails here.
        var restored = RoundTrip(NonDefault);

        Assert.Equal(NonDefault.Theme, restored.Theme);
        Assert.Equal(NonDefault.DefaultTerminal, restored.DefaultTerminal);
        Assert.Equal(NonDefault.DefaultTabPlacement, restored.DefaultTabPlacement);
        Assert.Equal(NonDefault.ConfirmBeforeClosingPanes, restored.ConfirmBeforeClosingPanes);
        Assert.Equal(NonDefault.CheckForUpdates, restored.CheckForUpdates);
        Assert.Equal(NonDefault.UpdateChannel, restored.UpdateChannel);
        Assert.Equal(NonDefault.TerminalFontFamily, restored.TerminalFontFamily);
        Assert.Equal(NonDefault.TerminalFontSize, restored.TerminalFontSize);
    }

    [Fact]
    public void The_defaults_survive_too()
    {
        var restored = RoundTrip(WinMuxSettings.Defaults);

        Assert.Equal(WinMuxSettings.Defaults, restored with { Version = WinMuxSettings.Defaults.Version });
    }

    [Fact]
    public void A_missing_setting_falls_back_to_its_default_without_complaint()
    {
        // Hand-edited files are expected and an absent key is not an error.
        var result = SettingsFile.Parse("version = 1", "test");

        Assert.Null(result.Warning);
        Assert.Equal(WinMuxSettings.Defaults.TerminalFontFamily, result.Settings.TerminalFontFamily);
        Assert.Equal(WinMuxSettings.Defaults.TerminalFontSize, result.Settings.TerminalFontSize);
    }

    [Theory]
    [InlineData("terminal_font_size = 16", 16)]
    [InlineData("terminal_font_size = 16.5", 16.5)]
    public void A_font_size_may_be_written_as_an_integer_or_a_float(string line, double expected)
    {
        // TOML tells 16 and 16.0 apart; a person editing the file does not.
        Assert.Equal(expected, SettingsFile.Parse(line, "test").Settings.TerminalFontSize);
    }

    [Theory]
    [InlineData("terminal_font_size = 2")]
    [InlineData("terminal_font_size = 500")]
    [InlineData("terminal_font_size = 'large'")]
    public void An_unusable_font_size_is_refused_and_explained(string line)
    {
        // Refused rather than clamped: a two-pixel font is a typo, and quietly using 6 would hide it.
        var result = SettingsFile.Parse(line, "test");

        Assert.Equal(WinMuxSettings.Defaults.TerminalFontSize, result.Settings.TerminalFontSize);
        Assert.NotNull(result.Warning);
        Assert.Contains("terminal_font_size", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_font_family_falls_back_rather_than_leaving_no_font()
    {
        Assert.Equal(
            WinMuxSettings.Defaults.TerminalFontFamily,
            SettingsFile.Parse("terminal_font_family = ''", "test").Settings.TerminalFontFamily);
    }

    [Fact]
    public void The_written_file_is_still_readable_by_a_person()
    {
        // The file is meant to be hand-edited, so every key carries a comment saying what it does.
        var text = SettingsFile.Serialize(WinMuxSettings.Defaults);

        foreach (var key in new[]
                 {
                     "theme", "default_terminal", "default_tab_placement",
                     "confirm_before_closing_panes", "check_for_updates", "update_channel",
                     "terminal_font_family", "terminal_font_size",
                 })
        {
            Assert.Contains(key, text, StringComparison.Ordinal);
        }

        Assert.Contains("#", text, StringComparison.Ordinal);
    }
}
