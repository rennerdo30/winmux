using WinMux.Core.Settings;
using WinMux.Core.Update;

namespace WinMux.Shell.Update;

/// <summary>Where WinMux lives on the web. One place, so a rename is one edit.</summary>
internal static class Links
{
    public const string Owner = "rennerdo30";
    public const string Repository = "winmux";

    public const string Repo = $"https://github.com/{Owner}/{Repository}";
    public const string Documentation = $"https://{Owner}.github.io/{Repository}/";
    public const string Releases = $"{Repo}/releases";
    public const string Issues = $"{Repo}/issues";

    /// <summary>
    /// Open a URL in whatever the user uses for URLs.
    ///
    /// <c>UseShellExecute</c> is what makes the string a *request to the shell* rather than an
    /// attempt to execute it, which is also what keeps this from being a way to run arbitrary
    /// programs: every caller passes a constant from this class.
    /// </summary>
    public static bool Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Checking GitHub for a newer WinMux, and installing one when asked.
///
/// The judgement lives in <see cref="UpdateCheck"/> in Core and is unit-tested; this is the wiring
/// that gives it a network and a running application to act on.
/// </summary>
internal static class UpdateService
{
    /// <summary>The running version, from the assembly the shell was built as.</summary>
    public static ReleaseVersion Current { get; } =
        ReleaseVersion.TryParse(
            typeof(UpdateService).Assembly.GetName().Version?.ToString(3), out var version)
            ? version
            : new ReleaseVersion(0, 0, 0, "");

    public static async Task<UpdateDecision> CheckAsync(
        WinMuxSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.CheckForUpdates) return UpdateDecision.UpToDate;

        using var http = GitHubReleases.CreateClient();
        var releases = await new GitHubReleases(http, Links.Owner, Links.Repository)
            .ListAsync(cancellationToken);

        return UpdateCheck.Decide(releases, Current, settings.UpdateChannel);
    }

    /// <summary>Download and verify an update, leaving it staged for the next restart.</summary>
    public static async Task<UpdateStageResult> StageAsync(
        UpdateDecision decision,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var http = GitHubReleases.CreateClient();
        var installer = new UpdateInstaller(new GitHubReleases(http, Links.Owner, Links.Repository));
        return await installer.StageAsync(decision, progress, cancellationToken);
    }
}
