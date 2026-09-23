using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// A name the user gave a pane is a decision; the title a program reports about itself is a report.
/// A decision outranks a report, and it has to survive save and restore like any other layout
/// choice (priority 1).
/// </summary>
public class CustomPaneTitleTests
{
    [Fact]
    public void A_custom_title_survives_a_round_trip()
    {
        var pane = Pane.Terminal("build", "cmd.exe");
        pane.Title = "build";
        pane.Restore = pane.Restore with { Title = "build", TitleIsCustom = true };

        var restored = RoundTrip(pane);

        Assert.Equal("build", restored.Title);
        Assert.True(restored.Restore.TitleIsCustom);
    }

    [Fact]
    public void A_pane_that_replaces_a_named_one_keeps_the_name()
    {
        // Name an empty pane, then open cmd in it: the cmd pane is the one that was named. Reported
        // 2026-09-23 — the name vanished, because the replacement was a fresh pane.
        var empty = Pane.Empty();
        empty.Title = "build";
        empty.Restore = empty.Restore with { Title = "build", TitleIsCustom = true };
        var terminal = Pane.Terminal("Command Prompt", "cmd.exe");

        terminal.KeepCustomTitleOf(empty);

        Assert.Equal("build", terminal.Title);
        Assert.Equal("build", terminal.Restore.Title);
        Assert.True(terminal.Restore.TitleIsCustom);
        Assert.Equal("cmd.exe", terminal.Restore.Program);
        Assert.True(RoundTrip(terminal).Restore.TitleIsCustom, "and it survives the session file");
    }

    [Fact]
    public void An_automatic_title_is_not_carried_to_the_replacement()
    {
        var terminal = Pane.Terminal("Command Prompt", "cmd.exe");

        terminal.KeepCustomTitleOf(Pane.Empty());

        Assert.Equal("Command Prompt", terminal.Title);
        Assert.False(terminal.Restore.TitleIsCustom);
    }

    [Fact]
    public void An_automatic_title_round_trips_without_the_flag()
    {
        var restored = RoundTrip(Pane.Terminal("cmd", "cmd.exe"));

        Assert.False(restored.Restore.TitleIsCustom);
    }

    [Fact]
    public void The_flag_is_written_only_when_it_is_true()
    {
        // The pane table is the part of the session file meant to be read and hand-edited
        // (ADR 0006); a title_custom = false on every entry would be noise.
        Assert.DoesNotContain("title_custom", Serialize(Pane.Terminal("cmd", "cmd.exe")), StringComparison.Ordinal);

        var named = Pane.Terminal("build", "cmd.exe");
        named.Restore = named.Restore with { TitleIsCustom = true };
        Assert.Contains("title_custom", Serialize(named), StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_written_before_custom_titles_existed_still_loads()
    {
        var named = Pane.Terminal("build", "cmd.exe");
        named.Restore = named.Restore with { TitleIsCustom = true };

        var withoutKey = string.Join('\n', Serialize(named).Split('\n')
            .Where(line => !line.TrimStart().StartsWith("title_custom", StringComparison.Ordinal)));

        var restored = FirstPane(SessionFile.Deserialize(withoutKey));

        Assert.Equal("build", restored.Title);
        Assert.False(restored.Restore.TitleIsCustom);
    }

    [Fact]
    public void Clearing_the_flag_leaves_the_text_alone()
    {
        // "Use automatic name" hands the pane back to its program. The current text has to stay
        // until the program reports something, or the tab would go blank in the meantime.
        var pane = Pane.Terminal("build", "cmd.exe");
        pane.Restore = pane.Restore with { Title = "build", TitleIsCustom = true };

        pane.Restore = pane.Restore with { TitleIsCustom = false };

        Assert.Equal("build", pane.Restore.Title);
        Assert.False(pane.Restore.TitleIsCustom);
    }

    [Fact]
    public void Every_pane_kind_can_carry_a_custom_title()
    {
        // Renaming is a layout affordance, not a terminal one: an adopted browser window is
        // exactly the thing somebody wants to call "docs".
        foreach (var pane in new[] { Pane.Terminal("t"), Pane.FileBrowser(@"C:\x"), Pane.Empty() })
        {
            pane.Title = "named";
            pane.Restore = pane.Restore with { Title = "named", TitleIsCustom = true };

            var restored = RoundTrip(pane);

            Assert.Equal("named", restored.Title);
            Assert.True(restored.Restore.TitleIsCustom);
        }
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

    private static Pane RoundTrip(Pane pane) => FirstPane(SessionFile.Deserialize(Serialize(pane)));

    private static Pane FirstPane(SessionSnapshot snapshot) =>
        SessionMapper.FromSnapshot(snapshot.Windows[0]).Panes.First();
}
