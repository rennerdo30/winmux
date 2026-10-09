using Avalonia.Threading;
using WinMux.Core.Update;
using WinMux.Shell.Update;

namespace WinMux.Shell.Tests;

/// <summary>
/// The update, from check to restart, without a network: the two operations are fakes, and the
/// state the About window shows is what is asserted.
/// </summary>
public sealed class UpdateControllerTests
{
    private static readonly ReleaseAsset Package = new("WinMux-0.9.0-win-x64.zip", "https://example.invalid/a.zip", 10);
    private static readonly ReleaseAsset Sums = new("checksums.txt", "https://example.invalid/sums", 1);

    private static readonly Release Newer =
        new(ReleaseVersion.Parse("0.9.0"), false, "notes", "https://example.invalid/r", [Package, Sums]);

    private static readonly UpdateDecision Available = new(UpdateOutcome.UpdateAvailable, Newer, Package, Sums);

    private static UpdateController Controller(
        Func<UpdateDecision>? check = null,
        Func<IProgress<double>, UpdateStageResult>? stage = null) =>
        new(
            _ => Task.FromResult((check ?? (() => Available))()),
            (_, progress, _) => Task.FromResult((stage ?? (_ => new UpdateStageResult(true, @"C:\staged", "ready")))(progress)));

    [Fact]
    public async Task A_check_that_finds_a_release_makes_it_available()
    {
        var updates = Controller();

        await updates.CheckAsync();

        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal(Newer, updates.Release);
        Assert.NotNull(updates.LastChecked);
    }

    [Fact]
    public async Task Nothing_newer_is_up_to_date()
    {
        var updates = Controller(check: () => UpdateDecision.UpToDate);

        await updates.CheckAsync();

        Assert.Equal(UpdateState.UpToDate, updates.State);
        Assert.Null(updates.Release);
    }

    [Fact]
    public async Task Downloading_ends_ready_to_restart_with_the_staged_folder()
    {
        var reported = new List<double>();
        var updates = Controller(stage: progress =>
        {
            progress.Report(0.5);
            return new UpdateStageResult(true, @"C:\staged", "ready");
        });
        updates.Changed += () => reported.Add(updates.Progress);

        await updates.CheckAsync();
        Assert.True(await updates.DownloadAsync());

        Assert.Equal(UpdateState.ReadyToRestart, updates.State);
        Assert.Equal(@"C:\staged", updates.StagedDirectory);
        Assert.Contains(0.5, reported);
        Assert.Equal(1, updates.Progress);
    }

    [Fact]
    public async Task Nothing_is_downloaded_before_a_release_is_found()
    {
        var staged = false;
        var updates = Controller(stage: _ => { staged = true; return new UpdateStageResult(true, "x", ""); });

        Assert.False(await updates.DownloadAsync());
        Assert.False(staged);
        Assert.Equal(UpdateState.Idle, updates.State);
    }

    [Fact]
    public async Task A_failed_download_says_why_and_retrying_downloads_again()
    {
        var attempts = 0;
        var updates = Controller(stage: _ => ++attempts == 1
            ? new UpdateStageResult(false, null, "the download did not match its published checksum")
            : new UpdateStageResult(true, @"C:\staged", "ready"));

        await updates.CheckAsync();
        await updates.DownloadAsync();

        Assert.Equal(UpdateState.Failed, updates.State);
        Assert.Contains("checksum", updates.Error);

        await updates.RetryAsync();

        Assert.Equal(2, attempts);
        Assert.Equal(UpdateState.ReadyToRestart, updates.State);
        Assert.Null(updates.Error);
    }

    [Fact]
    public async Task Being_offline_is_a_failure_to_check_not_an_answer()
    {
        var updates = Controller(check: () => throw new HttpRequestException("No such host is known."));

        await updates.CheckAsync();

        Assert.Equal(UpdateState.Failed, updates.State);
        Assert.Contains("No such host", updates.Error);
        Assert.Null(updates.Decision);
    }

    [Fact]
    public async Task A_second_check_while_one_runs_waits_for_the_same_answer()
    {
        var gate = new TaskCompletionSource<UpdateDecision>();
        var calls = 0;
        var updates = new UpdateController(
            _ => { calls++; return gate.Task; },
            (_, _, _) => Task.FromResult(new UpdateStageResult(true, "x", "")));

        var first = updates.CheckAsync();
        var second = updates.CheckAsync();
        gate.SetResult(Available);
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(UpdateState.Available, updates.State);
    }

    [Fact]
    public async Task A_downloaded_update_is_not_thrown_away_by_another_check()
    {
        var updates = Controller();
        await updates.CheckAsync();
        await updates.DownloadAsync();

        await updates.CheckAsync();

        Assert.Equal(UpdateState.ReadyToRestart, updates.State);
    }

    [Theory]
    [InlineData("0.7.8-test.8+3e6dc9d", "0.7.8-test.8")]
    [InlineData("0.7.8-test.8", "0.7.8-test.8")]
    [InlineData("0.8.0", "0.8.0")]
    public void The_running_version_keeps_its_prerelease_suffix(string informational, string expected) =>
        Assert.Equal(ReleaseVersion.Parse(expected), UpdateService.RunningVersion(informational, new Version(0, 7, 8, 0)));

    [Fact]
    public void A_test_build_is_offered_the_next_test_build()
    {
        // The fault this pins: read from the assembly version, test.8 thought it was 0.7.8, and
        // every 0.7.8-test.N sorts before 0.7.8, so nothing was ever newer.
        var running = UpdateService.RunningVersion("0.7.8-test.8+abc", new Version(0, 7, 8, 0));
        var next = new Release(ReleaseVersion.Parse("0.7.8-test.9"), true, "", "", [Package with { Name = "WinMux-0.7.8-test.9-win-x64.zip" }]);

        var decision = UpdateCheck.Decide([next], running, UpdateChannel.Prerelease);

        Assert.Equal(UpdateOutcome.UpdateAvailable, decision.Outcome);
    }

    [Fact]
    public void Without_an_informational_version_the_assembly_version_is_used() =>
        Assert.Equal(ReleaseVersion.Parse("0.7.8"), UpdateService.RunningVersion(null, new Version(0, 7, 8, 0)));

    [Fact]
    public Task The_About_window_walks_from_available_to_restart() => Headless.RunSync(() =>
    {
        var updates = Controller();
        var restarted = false;
        var about = new AboutWindow(updates, () => true, () => restarted = true);
        about.Show();
        Dispatcher.UIThread.RunJobs();

        // Opening it checked, the way a browser's About does.
        Assert.Equal(UpdateState.Available, updates.State);
        Assert.Equal("WinMux 0.9.0 is available", about.StatusText);
        Assert.Equal("Download and install", about.PrimaryText);

        about.ClickPrimary();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Restart to finish updating to 0.9.0", about.StatusText);
        Assert.Equal("Restart WinMux", about.PrimaryText);
        Assert.False(restarted, "downloading never restarts by itself");

        about.ClickPrimary();
        Assert.True(restarted);
        about.Close();
    });

    [Fact]
    public Task With_checks_turned_off_opening_About_does_not_go_to_the_network() => Headless.RunSync(() =>
    {
        var checks = 0;
        var updates = new UpdateController(
            _ => { checks++; return Task.FromResult(UpdateDecision.UpToDate); },
            (_, _, _) => Task.FromResult(new UpdateStageResult(true, "x", "")));
        var about = new AboutWindow(updates, () => false, () => { });
        about.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, checks);
        Assert.Equal("Automatic update checks are off", about.StatusText);
        Assert.Equal("Check for updates", about.PrimaryText);

        about.ClickPrimary();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, checks);
        Assert.Equal("WinMux is up to date", about.StatusText);
        about.Close();
    });

    [Fact]
    public void The_copyright_line_is_the_one_the_assembly_was_built_with() =>
        Assert.StartsWith("Copyright © ", AboutWindow.Copyright);
}
