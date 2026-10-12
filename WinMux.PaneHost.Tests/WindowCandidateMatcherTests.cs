using WinMux.PaneHost;

namespace WinMux.PaneHost.Tests;

public sealed class WindowCandidateMatcherTests
{
    [Fact]
    public void Old_matching_window_is_never_a_launch_candidate()
    {
        var old = new IntPtr(42);
        var fresh = new IntPtr(84);
        Assert.Equal(IntPtr.Zero, WindowCandidateMatcher.LastNewMatch(
            [old], new HashSet<IntPtr> { old }, new HashSet<IntPtr>(), _ => true));
        Assert.Equal(fresh, WindowCandidateMatcher.LastNewMatch(
            [old, fresh], new HashSet<IntPtr> { old }, new HashSet<IntPtr>(), _ => true));
    }

    [Fact]
    public void Shared_chromium_class_cannot_adopt_another_app()
    {
        var launch = LaunchSpec.Parse([
            "--program", "Code.exe", "--match-mode", "class-name",
            "--window-class", "Chrome_WidgetWin_1", "--process-name", "Code.exe",
        ]);

        Assert.False(WindowCandidateMatcher.Matches(launch, 100, 200,
            "chrome.exe", "Chrome_WidgetWin_1", "Project"));
        Assert.True(WindowCandidateMatcher.Matches(launch, 100, 200,
            "Code.exe", "Chrome_WidgetWin_1", "Project"));
    }

    [Fact]
    public void Packaged_app_rule_cannot_adopt_settings_with_the_same_class_and_process()
    {
        var launch = LaunchSpec.Parse([
            "--program", "calc.exe", "--match-mode", "class-name",
            "--window-class", "ApplicationFrameWindow",
            "--process-name", "ApplicationFrameHost.exe",
            "--window-title-contains", "Calculator",
        ]);

        Assert.False(WindowCandidateMatcher.Matches(launch, 100, 200,
            "ApplicationFrameHost.exe", "ApplicationFrameWindow", "Settings"));
        Assert.True(WindowCandidateMatcher.Matches(launch, 100, 200,
            "ApplicationFrameHost.exe", "ApplicationFrameWindow", "Calculator"));
    }

    [Fact]
    public void Pid_rule_rejects_a_different_process_even_with_the_same_title()
    {
        var launch = LaunchSpec.Parse(["--program", "charmap.exe", "--match-mode", "pid"]);

        Assert.False(WindowCandidateMatcher.Matches(launch, 100, 200, "charmap.exe", "#32770", "Character Map"));
        Assert.True(WindowCandidateMatcher.Matches(launch, 100, 100, "charmap.exe", "#32770", "Character Map"));
    }
}
