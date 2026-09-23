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

    /// <summary>
    /// Where a staged update waits: in the user's own data directory, which is always writable. It
    /// used to be beside the installation, whose parent directory need not be.
    /// </summary>
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

        var staging = Path.Combine(DataDirectory, StagedDirectoryName);
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
            // would leave an installation with no application in it. And *is* the application: the
            // command-line tool once shipped under this very name, and a check that the file merely
            // existed was satisfied by it (CLAUDE.md section 3).
            var exe = Path.Combine(staging, "WinMux.exe");
            if (!File.Exists(exe) || !IsWindowsApplication(exe))
            {
                Directory.Delete(staging, recursive: true);
                return new UpdateStageResult(false, null, "the downloaded archive does not contain the WinMux application");
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
    /// Hand the replacement to a script, and let WinMux close.
    ///
    /// <para>
    /// The first version renamed the whole installation directory aside and moved the new one in.
    /// A directory cannot be renamed while anything holds a handle inside it — a terminal host
    /// started from it, a shell whose working directory it is, an Explorer window showing it — so the
    /// swap failed, the failure was thrown inside a hidden window, and nothing restarted WinMux. It
    /// also took the session with it when the session lived in the installation directory.
    /// </para>
    ///
    /// <para>
    /// This one replaces files. Each existing file is renamed to <c>*.winmux-old</c> first — Windows
    /// allows renaming a file that is in use, it only refuses to overwrite or delete one — and the new
    /// file is copied into its place, with retries for the moment a process takes to let go. If any
    /// file cannot be replaced, everything already replaced is put back. Either way WinMux is started
    /// again, on the same session, and the outcome is written where the next start reads it; the
    /// whole run is logged to <see cref="LogPath"/>.
    /// </para>
    /// </summary>
    public static bool LaunchSwapAndExit(string stagedDirectory, string sessionPath, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionPath);

        var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var script = Path.Combine(Path.GetTempPath(), $"winmux-update-{Environment.ProcessId}.ps1");
        var body = BuildScript(
            Environment.ProcessId, current, stagedDirectory, sessionPath, version, LogPath, ResultPath, relaunch: true);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(script, body);
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Everything an update did, in order. Kept small; see the script.</summary>
    public static string LogPath => Path.Combine(DataDirectory, "update.log");

    /// <summary>One line the script leaves for the next start: <c>ok|version</c> or <c>failed|reason</c>.</summary>
    public static string ResultPath => Path.Combine(DataDirectory, "update-result.txt");

    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMux");

    /// <summary>
    /// What the last update did, for the status line — once — and tidy up after it: the old files it
    /// renamed aside are deleted now that nothing is running them. Null when there was no update.
    /// </summary>
    public static string? CollectResult()
    {
        DeleteOldFiles(AppContext.BaseDirectory);

        string? line;
        try
        {
            if (!File.Exists(ResultPath)) return null;
            line = File.ReadAllText(ResultPath).Trim();
            File.Delete(ResultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var split = line.IndexOf('|');
        var status = split < 0 ? line : line[..split];
        var detail = split < 0 ? string.Empty : line[(split + 1)..];
        return status == "ok"
            ? $"Updated to WinMux {detail}"
            : $"The update could not be installed, so this is still the version you had: {detail} (details in {LogPath})";
    }

    private static void DeleteOldFiles(string directory)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(directory, "*.winmux-old", SearchOption.AllDirectories))
            {
                try { File.Delete(old); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use; next time */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The replacement script. Separate from launching it, so a test can run it for real.</summary>
    internal static string BuildScript(
        int processId,
        string current,
        string staged,
        string sessionPath,
        string version,
        string logPath,
        string resultPath,
        bool relaunch,
        int attempts = 40)
    {
        // $$ so a single brace is literal: the script is full of PowerShell blocks, and {{ }} is
        // what interpolates.
        return $$"""
            # Written by WinMux to replace itself. Safe to delete.
            $ErrorActionPreference = 'Stop'
            $pidToWait = {{processId}}
            $current   = {{Quote(current)}}
            $staged    = {{Quote(staged)}}
            $session   = {{Quote(sessionPath)}}
            $version   = {{Quote(version)}}
            $log       = {{Quote(logPath)}}
            $result    = {{Quote(resultPath)}}
            $relaunch  = ${{(relaunch ? "true" : "false")}}
            $attempts  = {{attempts}}

            function Log([string]$message) {
                try {
                    if ((Test-Path -LiteralPath $log) -and ((Get-Item -LiteralPath $log).Length -gt 1MB)) { Remove-Item -LiteralPath $log -Force }
                    Add-Content -LiteralPath $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $message)
                } catch { }
            }

            # A process takes a moment to let go of its files after it exits; try again briefly.
            function Retry([scriptblock]$action) {
                for ($attempt = 1; ; $attempt++) {
                    try { & $action; return }
                    catch { if ($attempt -ge $attempts) { throw }; Start-Sleep -Milliseconds 250 }
                }
            }

            Log "installing WinMux $version into $current"

            # Nothing can be replaced while WinMux runs. Wait for it — and if it never goes, change
            # nothing rather than half-install over a running program.
            $deadline = (Get-Date).AddMinutes(5)
            while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {
                if ((Get-Date) -gt $deadline) {
                    Log "WinMux (process $pidToWait) did not close within five minutes; nothing was changed"
                    Set-Content -LiteralPath $result -Value "failed|WinMux did not close, so nothing was changed"
                    exit 1
                }
                Start-Sleep -Milliseconds 250
            }

            $replaced = New-Object System.Collections.Generic.List[string]
            $added    = New-Object System.Collections.Generic.List[string]
            $ok = $true

            try {
                # The quirks file is meant to be edited by hand; keep the user's copy beside the new one.
                $quirks = Join-Path $current 'foreign-app-quirks.json'
                if (Test-Path -LiteralPath $quirks) {
                    Copy-Item -LiteralPath $quirks -Destination ($quirks + '.before-update') -Force
                }

                foreach ($file in Get-ChildItem -LiteralPath $staged -Recurse -File) {
                    $source   = $file.FullName
                    $relative = $source.Substring($staged.Length).TrimStart('\')
                    $target   = Join-Path $current $relative
                    $aside    = $target + '.winmux-old'

                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                    if (Test-Path -LiteralPath $target) {
                        if (Test-Path -LiteralPath $aside) { Retry { Remove-Item -LiteralPath $aside -Force } }
                        # Renaming works even while the file is in use; overwriting does not.
                        Retry { Move-Item -LiteralPath $target -Destination $aside }
                        $replaced.Add($target)
                    } else {
                        $added.Add($target)
                    }

                    Retry { Copy-Item -LiteralPath $source -Destination $target }
                }
            } catch {
                $ok = $false
                $reason = $_.Exception.Message
                Log "failed: $reason"
                Log "rolling back $($replaced.Count) replaced and $($added.Count) added file(s)"
                foreach ($target in $added) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
                foreach ($target in $replaced) {
                    $aside = $target + '.winmux-old'
                    if (Test-Path -LiteralPath $aside) {
                        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                        Move-Item -LiteralPath $aside -Destination $target -Force -ErrorAction SilentlyContinue
                    }
                }
                Set-Content -LiteralPath $result -Value ("failed|" + $reason)
            }

            if ($ok) {
                Log "installed $($replaced.Count) replaced and $($added.Count) new file(s)"
                Remove-Item -LiteralPath $staged -Recurse -Force -ErrorAction SilentlyContinue
                Set-Content -LiteralPath $result -Value ("ok|" + $version)
            }

            # Always start WinMux again — the new version, or the old one put back — on the same
            # session, from its own directory. Being left with nothing is the one outcome to avoid.
            if ($relaunch) {
                $exe = Join-Path $current 'WinMux.exe'
                try {
                    Start-Process -FilePath $exe -WorkingDirectory $current -ArgumentList ('"' + $session + '"')
                    Log "started $exe"
                } catch {
                    Log ("could not start WinMux: " + $_.Exception.Message)
                }
            }

            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
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

    /// <summary>The PE subsystem is 2 for a windowed application and 3 for a console one.</summary>
    internal static bool IsWindowsApplication(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5A4D) return false;
            stream.Position = 0x3C;
            var header = reader.ReadInt32();
            stream.Position = header + 24 + 68;
            return reader.ReadUInt16() == 2;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return false;
        }
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
