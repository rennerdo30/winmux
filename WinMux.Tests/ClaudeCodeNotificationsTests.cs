using System.Text.Json.Nodes;
using WinMux.Core.Settings;

namespace WinMux.Tests;

/// <summary>
/// Changing one key in another program's settings file — and nothing else about it.
/// </summary>
public sealed class ClaudeCodeNotificationsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("winmux-claude-").FullName;
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Every_other_setting_is_kept_and_the_old_file_is_backed_up()
    {
        const string original = """
            {
              "model": "opus",
              "permissions": { "allow": ["Bash(git status)"] },
              "preferredNotifChannel": "auto"
            }
            """;
        File.WriteAllText(SettingsPath, original);

        ClaudeCodeNotifications.Apply(SettingsPath);

        var written = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("iterm2", (string?)written["preferredNotifChannel"]);
        Assert.Equal("opus", (string?)written["model"]);
        Assert.Equal("Bash(git status)", (string?)written["permissions"]!["allow"]![0]);
        Assert.Equal(original, File.ReadAllText(SettingsPath + ".winmux-backup"));
    }

    [Fact]
    public void A_missing_file_is_created_with_just_the_one_key()
    {
        var path = Path.Combine(_directory, "new", "settings.json");

        Assert.False(ClaudeCodeNotifications.Inspect(path).Exists);
        ClaudeCodeNotifications.Apply(path);

        var written = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Single(written);
        Assert.False(File.Exists(path + ".winmux-backup"), "there was nothing to back up");
    }

    [Fact]
    public void A_file_with_comments_is_refused_rather_than_rewritten_without_them()
    {
        const string commented = "{\n  // my model\n  \"model\": \"opus\"\n}";
        File.WriteAllText(SettingsPath, commented);

        var state = ClaudeCodeNotifications.Inspect(SettingsPath);

        Assert.NotNull(state.Problem);
        Assert.Contains("preferredNotifChannel", state.Problem);
        Assert.Throws<InvalidOperationException>(() => ClaudeCodeNotifications.Apply(SettingsPath));
        Assert.Equal(commented, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("iterm2", true)]
    [InlineData("ghostty", true)]
    [InlineData("kitty", true)]
    [InlineData("terminal_bell", false)]
    [InlineData("auto", false)]
    public void Channels_WinMux_can_show_count_as_set_up(string channel, bool setUp)
    {
        File.WriteAllText(SettingsPath, $$"""{ "preferredNotifChannel": "{{channel}}" }""");

        Assert.Equal(setUp, ClaudeCodeNotifications.Inspect(SettingsPath).IsSetUp);
    }

    [Fact]
    public void The_config_directory_variable_is_respected()
    {
        var path = ClaudeCodeNotifications.DefaultPath(name => name == "CLAUDE_CONFIG_DIR" ? @"D:\claude-config" : null);

        Assert.Equal(Path.Combine(@"D:\claude-config", "settings.json"), path);
    }
}
