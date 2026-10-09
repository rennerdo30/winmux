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
    public const string Licence = $"{Repo}/blob/main/LICENSE";

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
    /// <summary>
    /// The running version, including any prerelease suffix.
    ///
    /// <para>
    /// It used to come from the assembly version, which has four numbers and nowhere to put
    /// <c>-test.8</c>. A test build therefore believed it was <c>0.7.8</c>, and since SemVer puts
    /// every <c>0.7.8-…</c> prerelease <em>before</em> <c>0.7.8</c>, no later test build was ever
    /// newer than it: the updater on the prerelease channel never offered anything.
    /// </para>
    /// </summary>
    public static ReleaseVersion Current { get; } = RunningVersion(
        typeof(UpdateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion,
        typeof(UpdateService).Assembly.GetName().Version);

    /// <summary>
    /// The product version as built: the informational version, which carries the suffix (and
    /// build metadata after <c>+</c>, which ordering ignores), else the assembly version.
    /// </summary>
    internal static ReleaseVersion RunningVersion(string? informational, Version? assembly)
    {
        if (ReleaseVersion.TryParse(informational, out var full)) return full;
        return ReleaseVersion.TryParse(assembly?.ToString(3), out var numbers)
            ? numbers
            : new ReleaseVersion(0, 0, 0, "");
    }

    /// <summary>
    /// Ask GitHub what is published. Always goes to the network: whether to check <em>on start</em>
    /// is the caller's decision, and a check the user asked for by hand must never answer "up to
    /// date" merely because automatic checks are off — which is what it used to do.
    /// </summary>
    public static async Task<UpdateDecision> CheckAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        using var http = GitHubReleases.CreateClient();
        var releases = await new GitHubReleases(http, Links.Owner, Links.Repository)
            .ListAsync(cancellationToken);

        return UpdateCheck.Decide(releases, Current, channel);
    }

    /// <summary>Download and verify an update, leaving it staged for the next restart.</summary>
    public static async Task<UpdateStageResult> StageAsync(
        UpdateDecision decision,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (UpdateInstaller.InstallDirectoryProblem() is { } problem)
            return new UpdateStageResult(false, null, problem);

        using var http = GitHubReleases.CreateClient();
        var installer = new UpdateInstaller(new GitHubReleases(http, Links.Owner, Links.Repository));
        return await installer.StageAsync(decision, progress, cancellationToken);
    }
}
