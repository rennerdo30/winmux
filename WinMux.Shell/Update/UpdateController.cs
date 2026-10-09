using WinMux.Core.Update;

namespace WinMux.Shell.Update;

/// <summary>Where an update stands, as the About window shows it.</summary>
internal enum UpdateState
{
    /// <summary>Nobody has asked yet this run.</summary>
    Idle,
    Checking,
    UpToDate,
    Available,

    /// <summary>A newer release exists but published nothing this machine can install.</summary>
    NotInstallable,
    Downloading,

    /// <summary>Downloaded, verified and unpacked; a restart puts it in place.</summary>
    ReadyToRestart,
    Failed,
}

/// <summary>
/// One update, from "is there one?" to "restart to finish", shared by everything that shows it.
///
/// <para>
/// It used to be two menu items — "Check for updates" and "Install update…" — each a one-shot
/// action that reported into the status bar. That is not how people expect an application to
/// update: they open its About box and watch it check, download and offer a restart, the way every
/// browser does. Doing that needs the state to outlive any one action, so it lives here, and the
/// About window, the startup check and the palette actions all drive the same instance.
/// </para>
///
/// <para>
/// Pure state plus two injected operations, so it is tested without a network or a window. Every
/// await resumes on the caller's context, so <see cref="Changed"/> is raised on the UI thread when
/// the UI thread drives it.
/// </para>
/// </summary>
internal sealed class UpdateController(
    Func<CancellationToken, Task<UpdateDecision>> check,
    Func<UpdateDecision, IProgress<double>, CancellationToken, Task<UpdateStageResult>> stage)
{
    public UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>The release found by the last check, while there is one to talk about.</summary>
    public Release? Release => Decision?.Release;

    /// <summary>The last check's answer; kept through a failed download so it can be retried.</summary>
    public UpdateDecision? Decision { get; private set; }

    /// <summary>0 to 1 while downloading.</summary>
    public double Progress { get; private set; }

    /// <summary>What went wrong, in words fit for the window, when <see cref="State"/> is Failed.</summary>
    public string? Error { get; private set; }

    /// <summary>Where the verified update waits, once <see cref="State"/> is ReadyToRestart.</summary>
    public string? StagedDirectory { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    /// <summary>Raised after every change of state or progress.</summary>
    public event Action? Changed;

    /// <summary>
    /// Ask whether there is a newer release. Does nothing while busy, and nothing once an update is
    /// downloaded and waiting: a fresh check then could only throw away a verified download.
    /// </summary>
    /// <returns>The check already under way, when there is one, so a caller can wait for its answer.</returns>
    public Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (State == UpdateState.Checking && _checking is { } running) return running;
        if (IsBusy || State == UpdateState.ReadyToRestart) return Task.CompletedTask;
        return _checking = CheckCoreAsync(cancellationToken);
    }

    private Task? _checking;

    private async Task CheckCoreAsync(CancellationToken cancellationToken)
    {

        Set(UpdateState.Checking);
        try
        {
            var decision = await check(cancellationToken);
            Decision = decision;
            LastChecked = DateTimeOffset.Now;
            Set(decision.Outcome switch
            {
                UpdateOutcome.UpdateAvailable => UpdateState.Available,
                UpdateOutcome.UpdateNotInstallable => UpdateState.NotInstallable,
                _ => UpdateState.UpToDate,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or System.Text.Json.JsonException)
        {
            // A failed check is a fact, not a fault: WinMux works offline.
            Decision = null;
            Fail("could not reach GitHub to check for updates: " + ex.Message);
        }
    }

    /// <summary>
    /// Download and verify the release the last check found. Nothing is installed: that waits for
    /// <see cref="UpdateState.ReadyToRestart"/> and the user saying restart.
    /// </summary>
    /// <returns>True when the update is ready to restart into.</returns>
    public async Task<bool> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (State == UpdateState.ReadyToRestart) return true;
        if (IsBusy || Decision is not { Outcome: UpdateOutcome.UpdateAvailable } decision) return false;

        Progress = 0;
        Set(UpdateState.Downloading);
        var progress = new Reporter(this);
        try
        {
            var result = await stage(decision, progress, cancellationToken);
            if (result.Succeeded && result.StagedDirectory is { } staged)
            {
                StagedDirectory = staged;
                Progress = 1;
                Set(UpdateState.ReadyToRestart);
                return true;
            }

            Fail(result.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException
                                       or UnauthorizedAccessException)
        {
            Fail("the download failed: " + ex.Message);
        }

        return false;
    }

    /// <summary>
    /// Whatever failed, try it again: the download when a release was found, the check otherwise.
    /// </summary>
    public Task RetryAsync(CancellationToken cancellationToken = default)
    {
        if (State != UpdateState.Failed) return Task.CompletedTask;
        if (Decision is { Outcome: UpdateOutcome.UpdateAvailable })
        {
            Set(UpdateState.Available);
            return DownloadAsync(cancellationToken);
        }

        Set(UpdateState.Idle);
        return CheckAsync(cancellationToken);
    }

    private void Fail(string message)
    {
        Error = message;
        Set(UpdateState.Failed);
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Progress straight into the controller, not through <see cref="Progress{T}"/>: that one posts
    /// every report to the captured context, and a report that lands after the download finished
    /// would set a completed bar back to 97 %.
    /// </summary>
    private sealed class Reporter(UpdateController owner) : IProgress<double>
    {
        public void Report(double value)
        {
            if (owner.State != UpdateState.Downloading) return;
            owner.Progress = Math.Clamp(value, 0, 1);
            owner.Changed?.Invoke();
        }
    }
}
