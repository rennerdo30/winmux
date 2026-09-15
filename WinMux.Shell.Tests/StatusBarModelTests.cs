using WinMux.Core.Model;

namespace WinMux.Shell.Tests;

/// <summary>
/// What the status bar says.
///
/// It was one debug string — "4 panes   focus: ABC   Ctrl+B then a key" — which spent its width on
/// a pane count nobody wants and a focus label that only existed because nothing on screen showed
/// focus. The two facts that demonstrate the product works, the captured working directory with its
/// provenance and whether the session is saved, were not in the interface at all.
/// </summary>
public sealed class StatusBarModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_captured_directory_says_how_it_was_captured()
    {
        // ADR 0004: a stale shell report and a live process read do not deserve equal trust, so the
        // interface does not present them identically either.
        var cwd = new WorkingDirectory(@"E:\Development\winmux", CwdSource.ShellReported, Now);

        Assert.Equal(@"E:\Development\winmux · shell-reported", StatusBarModel.Describe(cwd));
    }

    [Fact]
    public void Each_strategy_is_named_in_words_rather_than_by_its_enum()
    {
        Assert.Contains("running program", StatusBarModel.Describe(new WorkingDirectory("C:\\", CwdSource.ProcessDeepest, Now)));
        Assert.Contains("launched", StatusBarModel.Describe(new WorkingDirectory("C:\\", CwdSource.LaunchDirectory, Now)));
    }

    [Fact]
    public void An_uncaptured_directory_says_so_rather_than_showing_nothing()
    {
        // "not captured" is the state a user can act on: it means the shell snippet is missing.
        Assert.Equal("directory not captured", StatusBarModel.Describe(WorkingDirectory.None));
    }

    [Fact]
    public void A_path_with_no_source_is_still_not_trusted()
    {
        var unknown = new WorkingDirectory(@"C:\somewhere", CwdSource.Unknown, Now);

        Assert.Equal("directory not captured", StatusBarModel.Describe(unknown));
    }

    [Fact]
    public void The_location_segment_names_the_pane_and_where_it_is()
    {
        var cwd = new WorkingDirectory(@"C:\work", CwdSource.ShellReported, Now);

        Assert.Equal(@"build  ·  C:\work · shell-reported", StatusBarModel.Location("build", cwd));
    }

    [Fact]
    public void With_no_pane_the_location_says_so()
    {
        Assert.Equal("no pane", StatusBarModel.Location(null, null));
        Assert.Equal("no pane", StatusBarModel.Location("   ", null));
    }

    [Fact]
    public void The_session_segment_shows_the_file_name_not_the_whole_path()
    {
        var text = StatusBarModel.Session(@"E:\Development\winmux\work.toml", Now, saving: false, Now);

        Assert.StartsWith("work.toml", text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"E:\", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_that_has_never_been_written_says_so()
    {
        Assert.Contains("not saved yet", StatusBarModel.Session("work.toml", null, saving: false, Now));
    }

    [Fact]
    public void Saving_outranks_the_last_save_time()
    {
        Assert.Contains("saving", StatusBarModel.Session("work.toml", Now, saving: true, Now));
    }

    [Theory]
    [InlineData(2, "just now")]
    [InlineData(30, "30s ago")]
    [InlineData(300, "5m ago")]
    public void A_recent_save_is_described_relatively(int secondsAgo, string expected)
    {
        // An absolute timestamp is not a status; "saved 4s ago" is what says persistence is alive.
        var text = StatusBarModel.Session("work.toml", Now.AddSeconds(-secondsAgo), saving: false, Now);

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_save_falls_back_to_a_clock_time()
    {
        var text = StatusBarModel.Session("work.toml", Now.AddHours(-3), saving: false, Now);

        Assert.DoesNotContain("ago", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prefix_segment_shows_the_gesture_until_it_is_armed()
    {
        Assert.Equal("Ctrl+B", StatusBarModel.Prefix("Ctrl+B", armed: false));

        // Armed is a mode, and a mode with no visible state is how a stray keystroke closes a pane.
        var armed = StatusBarModel.Prefix("Ctrl+B", armed: true);
        Assert.Contains("close", armed, StringComparison.Ordinal);
        Assert.Contains("split", armed, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_fills_all_three_segments()
    {
        var text = StatusBarModel.Build(
            "build",
            new WorkingDirectory(@"C:\work", CwdSource.ShellReported, Now),
            "work.toml",
            Now,
            saving: false,
            prefixGesture: "Ctrl+B",
            armed: false,
            now: Now);

        Assert.Contains("build", text.Location, StringComparison.Ordinal);
        Assert.Contains("work.toml", text.Session, StringComparison.Ordinal);
        Assert.Equal("Ctrl+B", text.Prefix);
    }
}
