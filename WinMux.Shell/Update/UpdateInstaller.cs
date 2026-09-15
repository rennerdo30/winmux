using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using WinMux.Core.Update;

namespace WinMux.Shell.Update;

/// <summary>What happened when an update was staged.</summary>
/// <param name="Succeeded">False means nothing was changed on disk.</param>
/// <param name="StagedDirectory">Where the new version is waiting, when it succeeded.</param>
/// <param name="Message">What to tell the user, in either case.</param>
public sealed record UpdateStageResult(bool Succeeded, string? StagedDirectory, string Message);

/// <summary>
/// Downloading a release, checking it, and putting it in place.
///
/// **Windows will not let a running executable be replaced**, so this cannot be a download-and-copy.
/// It works in two halves: stage the new version into a directory next to the installation while
/// WinMux is running, then hand a small script the job of swapping the directories once WinMux has
/// exited and relaunching it. That is the same shape as the auto-update in `bifrost-proxy`, for the
/// same reason.
///
/// **Nothing is installed that has not been verified.** The release publishes `checksums.txt`, and
/// an archive whose SHA-256 is absent from it or does not match is discarded — the download is over
/// HTTPS, but a checksum published alongside the artifact is what turns "whatever the API pointed
/// at" into "the file the release build produced".
/// </summary>
internal sealed class UpdateInstaller(GitHubReleases releases)
{
    private readonly GitHubReleases _releases = releases ?? throw new ArgumentNullException(nameof(releases));

    /// <summary>Where a staged update waits, beside the installation rather than in temp.</summary>
    public const string StagedDirectoryName = "update-staged";

    /// <summary>
    /// Fetch, verify and unpack an update. Changes nothing about the running installation.
    /// </summary>
    public async Task<UpdateStageResult> StageAsync(
        UpdateDecision decision,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Outcome != UpdateOutcome.UpdateAvailable || decision.Package is not { } package)
            return new UpdateStageResult(false, null, "there is no update to install");

        // Refuse rather than trust. A release with no manifest is a release we cannot check, and
        // installing it anyway would make the whole verification story decorative.
        if (decision.Checksums is not { } manifestAsset)
        {
            return new UpdateStageResult(
                false, null,
                $"release {decision.Release?.Version} publishes no {UpdateCheck.ChecksumsFileName}, so it cannot be verified");
        }

        var root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var staging = Path.Combine(Path.GetDirectoryName(root) ?? root, StagedDirectoryName);
        var archive = Path.Combine(Path.GetTempPath(), package.Name);

        try
        {
            var manifest = await _releases.GetTextAsync(manifestAsset.Url, cancellationToken);
            var expected = UpdateCheck.FindChecksum(manifest, package.Name);
            if (expected is null)
            {
                return new UpdateStageResult(
                    false, null, $"{package.Name} is not listed in {UpdateCheck.ChecksumsFileName}");
            }

            await DownloadAsync(package, archive, progress, cancellationToken);

            var actual = await HashAsync(archive, cancellationToken);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateStageResult(
                    false, null,
                    $"the download did not match its published checksum and was discarded (expected {expected[..12]}…, got {actual[..12]}…)");
            }

            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            ZipFile.ExtractToDirectory(archive, staging);

            // The archive contains what publish.ps1 packaged; if WinMux.exe is not in it, the swap
            // would leave an installation with no application in it.
            if (!File.Exists(Path.Combine(staging, "WinMux.exe")))
            {
                Directory.Delete(staging, recursive: true);
                return new UpdateStageResult(false, null, "the downloaded archive does not contain WinMux.exe");
            }

            return new UpdateStageResult(
                true, staging, $"WinMux {decision.Release?.Version} is ready to install");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return new UpdateStageResult(false, null, "the update could not be downloaded: " + ex.Message);
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    /// <summary>
    /// Hand the swap to a script and quit.
    ///
    /// The script waits for this process to exit — Windows holds a lock on a running image and the
    /// replacement fails outright otherwise — then moves the current installation aside, moves the
    /// staged one into place, and starts WinMux again. The old installation is kept until the new
    /// one has started, so a failed move leaves something to go back to.
    /// </summary>
    public static bool LaunchSwapAndExit(string stagedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);

        var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var previous = current + ".previous";
        var script = Path.Combine(Path.GetTempPath(), $"winmux-update-{Environment.ProcessId}.ps1");
        var exe = Path.Combine(current, "WinMux.exe");

        // $$ so a single brace is literal: the script is full of PowerShell blocks, and {{ }} is
        // what interpolates.
        var body = $$"""
            # Written by WinMux to replace itself. Safe to delete.
            $ErrorActionPreference = 'Stop'
            $pidToWait  = {{Environment.ProcessId}}
            $current    = {{Quote(current)}}
            $staged     = {{Quote(stagedDirectory)}}
            $previous   = {{Quote(previous)}}
            $exe        = {{Quote(exe)}}

            # Windows keeps a running image locked, so nothing can move until WinMux is gone.
            try { Wait-Process -Id $pidToWait -Timeout 60 -ErrorAction Stop } catch { }
            Start-Sleep -Milliseconds 500

            try {
                if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
                Move-Item -LiteralPath $current -Destination $previous
                Move-Item -LiteralPath $staged  -Destination $current
            } catch {
                # Put it back rather than leaving no installation at all.
                if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $current)) {
                    Move-Item -LiteralPath $previous -Destination $current
                }
                throw
            }

            Start-Process -FilePath $exe
            Start-Sleep -Seconds 3
            if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force -ErrorAction SilentlyContinue }
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;

        try
        {
            File.WriteAllText(script, body);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task DownloadAsync(
        ReleaseAsset package,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var client = GitHubReleases.CreateClient();
        using var response = await client.GetAsync(
            package.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? package.Size;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            if (total > 0) progress?.Report((double)copied / total);
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Single-quoted for PowerShell, which needs its own quotes doubled.</summary>
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
