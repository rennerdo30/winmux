using System.Security.Cryptography;
using System.Reflection;
using System.Text;

namespace WinMux.Shell.Cwd;

/// <summary>
/// Installs opt-in cwd reporting without replacing a user's shell configuration. Existing
/// profile bytes are retained verbatim, and every modified profile receives a byte-exact backup.
/// </summary>
public sealed class ProfileInstaller
{
    private const string Marker = "WinMux cwd reporting [winmux-cwd-v1]";
    private const string AutoRunName = "AutoRun";

    // These are the measured snippets from spike 4, embedded as inspectable product assets.
    private static readonly string PowerShellSnippet = ReadSnippet("winmux.ps1");
    private static readonly string CmdSnippet = ReadSnippet("winmux.cmd");
    private static readonly string BashSnippet = ReadSnippet("winmux.sh");

    private readonly ProfileInstallerPaths _paths;
    private readonly ICommandProcessorRegistry _registry;
    private readonly Func<DateTimeOffset> _now;

    public ProfileInstaller()
        : this(ProfileInstallerPaths.ForCurrentUser(), new CurrentUserCommandProcessorRegistry())
    {
    }

    public ProfileInstaller(
        ProfileInstallerPaths paths,
        ICommandProcessorRegistry registry,
        Func<DateTimeOffset>? now = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _now = now ?? (() => DateTimeOffset.UtcNow);

        EnsureAbsolutePath(paths.WindowsPowerShellProfile, nameof(paths.WindowsPowerShellProfile));
        EnsureAbsolutePath(paths.PowerShellProfile, nameof(paths.PowerShellProfile));
        EnsureAbsolutePath(paths.IntegrationDirectory, nameof(paths.IntegrationDirectory));
    }

    /// <summary>Reports current integration state without changing files or the registry.</summary>
    public ProfileInstallationReport Inspect()
    {
        var powerShellAsset = AssetPath("powershell", ".ps1", PowerShellSnippet);
        var cmdAsset = AssetPath("cmd", ".cmd", CmdSnippet);
        var bashAsset = AssetPath("bash", ".sh", BashSnippet);

        return new ProfileInstallationReport([
            InspectPowerShell(CwdProfileShell.WindowsPowerShell, _paths.WindowsPowerShellProfile, powerShellAsset),
            InspectPowerShell(CwdProfileShell.PowerShell, _paths.PowerShellProfile, powerShellAsset),
            InspectCmd(cmdAsset),
            ManualWslStatus(bashAsset),
        ]);
    }

    /// <summary>
    /// Installs PowerShell and cmd integration and prepares the measured WSL script. WSL startup
    /// files are never changed automatically; its result contains the exact manual command.
    /// </summary>
    public ProfileInstallationReport Install()
    {
        string? powerShellAsset = null;
        string? cmdAsset = null;
        string? bashAsset = null;
        Exception? powerShellAssetError = null;
        Exception? cmdAssetError = null;
        Exception? bashAssetError = null;

        TryPrepareAsset("powershell", ".ps1", PowerShellSnippet, out powerShellAsset, out powerShellAssetError);
        TryPrepareAsset("cmd", ".cmd", CmdSnippet, out cmdAsset, out cmdAssetError);
        TryPrepareAsset("bash", ".sh", BashSnippet, out bashAsset, out bashAssetError);

        return new ProfileInstallationReport([
            powerShellAssetError is null
                ? InstallPowerShell(CwdProfileShell.WindowsPowerShell, _paths.WindowsPowerShellProfile, powerShellAsset!)
                : Failure(CwdProfileShell.WindowsPowerShell, _paths.WindowsPowerShellProfile, powerShellAssetError),
            powerShellAssetError is null
                ? InstallPowerShell(CwdProfileShell.PowerShell, _paths.PowerShellProfile, powerShellAsset!)
                : Failure(CwdProfileShell.PowerShell, _paths.PowerShellProfile, powerShellAssetError),
            cmdAssetError is null
                ? InstallCmd(cmdAsset!)
                : Failure(CwdProfileShell.CommandPrompt, "HKCU\\Software\\Microsoft\\Command Processor\\AutoRun", cmdAssetError),
            bashAssetError is null
                ? ManualWslStatus(bashAsset!)
                : Failure(CwdProfileShell.WslBash, _paths.IntegrationDirectory, bashAssetError),
        ]);
    }

    private ShellProfileStatus InspectPowerShell(CwdProfileShell shell, string profile, string asset)
    {
        try
        {
            if (!File.Exists(profile))
            {
                return new ShellProfileStatus(shell, ProfileInstallState.NotInstalled,
                    $"The profile does not exist yet. WinMux can create it safely at {profile}.", profile);
            }

            return ProfileContainsMarker(profile)
                ? new ShellProfileStatus(shell, ProfileInstallState.AlreadyInstalled,
                    "WinMux cwd reporting is already imported by this profile.", profile)
                : new ShellProfileStatus(shell, ProfileInstallState.NotInstalled,
                    $"WinMux cwd reporting is not imported. The installer will append an import for {asset}.", profile);
        }
        catch (Exception ex) when (IsExpectedInstallFailure(ex))
        {
            return Failure(shell, profile, ex);
        }
    }

    private ShellProfileStatus InstallPowerShell(CwdProfileShell shell, string profile, string asset)
    {
        try
        {
            if (File.Exists(profile) && ProfileContainsMarker(profile))
            {
                return new ShellProfileStatus(shell, ProfileInstallState.AlreadyInstalled,
                    "WinMux cwd reporting was already present; no file was changed.", profile);
            }

            var escapedAsset = asset.Replace("'", "''", StringComparison.Ordinal);
            var block = $"# >>> {Marker} >>>\r\n. '{escapedAsset}'\r\n# <<< {Marker} <<<";
            var backup = AppendProfileBlock(profile, block);
            var message = backup is null
                ? "Created the profile and added WinMux cwd reporting."
                : $"Added WinMux cwd reporting and saved the original profile as {backup}.";
            return new ShellProfileStatus(shell, ProfileInstallState.Installed, message, profile, backup);
        }
        catch (Exception ex) when (IsExpectedInstallFailure(ex))
        {
            return Failure(shell, profile, ex);
        }
    }

    private ShellProfileStatus InspectCmd(string asset)
    {
        const string target = "HKCU\\Software\\Microsoft\\Command Processor\\AutoRun";
        try
        {
            var current = _registry.Read(AutoRunName);
            return current.Exists && current.Value?.Contains(Marker, StringComparison.Ordinal) == true
                ? new ShellProfileStatus(CwdProfileShell.CommandPrompt, ProfileInstallState.AlreadyInstalled,
                    "WinMux cwd reporting is already included in the current-user cmd AutoRun value.", target)
                : new ShellProfileStatus(CwdProfileShell.CommandPrompt, ProfileInstallState.NotInstalled,
                    $"WinMux can preserve the current AutoRun command and append a call to {asset}.", target);
        }
        catch (Exception ex) when (IsExpectedInstallFailure(ex))
        {
            return Failure(CwdProfileShell.CommandPrompt, target, ex);
        }
    }

    private ShellProfileStatus InstallCmd(string asset)
    {
        const string target = "HKCU\\Software\\Microsoft\\Command Processor\\AutoRun";
        try
        {
            var current = _registry.Read(AutoRunName);
            if (current.Exists && current.Value?.Contains(Marker, StringComparison.Ordinal) == true)
            {
                return new ShellProfileStatus(CwdProfileShell.CommandPrompt, ProfileInstallState.AlreadyInstalled,
                    "WinMux cwd reporting was already present; the registry was not changed.", target);
            }

            if (asset.Contains('%', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The WinMux integration path contains a percent sign, which cmd may expand as an environment variable. " +
                    "The AutoRun value was left unchanged; choose an integration directory without a percent sign.");
            }

            string? backupName = null;
            if (current.Exists)
            {
                backupName = $"WinMuxAutoRunBackup-{_now():yyyyMMddHHmmss}-{Guid.NewGuid():N}";
                _registry.Write(backupName, current);
            }

            var import = $"call \"{asset}\" & rem {Marker}";
            var separator = string.IsNullOrWhiteSpace(current.Value) ? string.Empty :
                current.Value.TrimEnd().EndsWith('&') ? " " : " & ";
            var combined = (current.Value ?? string.Empty) + separator + import;

            try
            {
                _registry.Write(AutoRunName,
                    new RegistryStringValue(true, combined, current.ExpandEnvironmentVariables));
            }
            catch
            {
                RestoreRegistryValue(current);
                throw;
            }

            var backup = backupName is null
                ? null
                : $"HKCU\\Software\\Microsoft\\Command Processor\\{backupName}";
            var message = backup is null
                ? "Added WinMux cwd reporting to the current-user cmd AutoRun value."
                : $"Preserved the previous AutoRun value in {backup} and appended WinMux cwd reporting.";
            return new ShellProfileStatus(CwdProfileShell.CommandPrompt, ProfileInstallState.Installed,
                message, target, backup);
        }
        catch (Exception ex) when (IsExpectedInstallFailure(ex))
        {
            return Failure(CwdProfileShell.CommandPrompt, target, ex);
        }
    }

    private void RestoreRegistryValue(RegistryStringValue original)
    {
        try
        {
            if (original.Exists)
            {
                _registry.Write(AutoRunName, original);
            }
            else
            {
                _registry.Delete(AutoRunName);
            }
        }
        catch
        {
            // The original failure is reported. The separately saved backup remains recoverable.
        }
    }

    private ShellProfileStatus ManualWslStatus(string asset)
    {
        var source = ToWslPath(asset);
        var quoted = QuoteForBash(source);
        var instruction =
            $"WinMux did not change any WSL file because the active distribution, Linux home, and permissions " +
            $"cannot be determined safely from Windows. In the intended WSL distribution, add this line to ~/.bashrc: source {quoted}";
        return new ShellProfileStatus(CwdProfileShell.WslBash, ProfileInstallState.ManualActionRequired,
            instruction, "~/.bashrc");
    }

    private string? AppendProfileBlock(string profile, string block)
    {
        var directory = Path.GetDirectoryName(profile);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The profile path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        if (!File.Exists(profile))
        {
            File.WriteAllText(profile, block + Environment.NewLine, new UTF8Encoding(false));
            return null;
        }

        var original = File.ReadAllBytes(profile);
        var format = DetectTextFormat(original);
        var newline = DetectNewline(original, format.Encoding);
        var prefix = EndsWithNewline(original, format.Encoding) ? string.Empty : newline;
        var textToAppend = prefix + block.Replace("\r\n", newline, StringComparison.Ordinal) + newline;
        if (format.IsLegacyFallback && textToAppend.Any(character => character > 0x7f))
        {
            throw new InvalidDataException(
                "The existing profile has no recognizable Unicode encoding and the import path contains non-ASCII text. " +
                "The profile was left unchanged; save it as UTF-8 or UTF-16 and try again.");
        }

        var suffix = format.Encoding.GetBytes(textToAppend);
        var replacement = profile + ".winmux-new-" + Guid.NewGuid().ToString("N");
        var backup = UniqueBackupPath(profile);

        try
        {
            using (var stream = new FileStream(replacement, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(original);
                stream.Write(suffix);
                stream.Flush(flushToDisk: true);
            }

            File.Replace(replacement, profile, backup, ignoreMetadataErrors: true);
            return backup;
        }
        finally
        {
            if (File.Exists(replacement))
            {
                File.Delete(replacement);
            }
        }
    }

    private bool ProfileContainsMarker(string profile)
    {
        var bytes = File.ReadAllBytes(profile);
        var format = DetectTextFormat(bytes);
        return format.Encoding.GetString(bytes, format.PreambleLength, bytes.Length - format.PreambleLength)
            .Contains(Marker, StringComparison.Ordinal);
    }

    private string UniqueBackupPath(string profile)
    {
        while (true)
        {
            var candidate = $"{profile}.winmux-backup-{_now():yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void TryPrepareAsset(
        string name,
        string extension,
        string content,
        out string? path,
        out Exception? error)
    {
        path = null;
        error = null;
        try
        {
            path = AssetPath(name, extension, content);
            Directory.CreateDirectory(_paths.IntegrationDirectory);
            var bytes = new UTF8Encoding(false).GetBytes(content.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
            if (File.Exists(path))
            {
                if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    throw new IOException($"The integration file {path} already exists with unexpected content; it was left unchanged.");
                }

                return;
            }

            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (IsExpectedInstallFailure(ex))
        {
            error = ex;
        }
    }

    private string AssetPath(string name, string extension, string content)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant();
        return Path.Combine(_paths.IntegrationDirectory, $"winmux-cwd-{name}-{hash}{extension}");
    }

    private static TextFormat DetectTextFormat(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
        {
            return new TextFormat(Encoding.UTF32, Encoding.UTF32.GetPreamble().Length, false);
        }

        var utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (bytes.AsSpan().StartsWith(utf32BigEndian.GetPreamble()))
        {
            return new TextFormat(utf32BigEndian, utf32BigEndian.GetPreamble().Length, false);
        }

        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            return new TextFormat(Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.GetPreamble().Length, false);
        }

        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            return new TextFormat(Encoding.Unicode, Encoding.Unicode.GetPreamble().Length, false);
        }

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return new TextFormat(new UTF8Encoding(true), Encoding.UTF8.GetPreamble().Length, false);
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new TextFormat(new UTF8Encoding(false), 0, false);
        }
        catch (DecoderFallbackException)
        {
            // Appended marker/import text is ASCII in the usual installation location, so this
            // preserves legacy ANSI profile bytes without recoding the user's content.
            return new TextFormat(Encoding.Latin1, 0, true);
        }
    }

    private static string DetectNewline(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" :
            text.Contains('\n', StringComparison.Ordinal) ? "\n" : Environment.NewLine;
    }

    private static bool EndsWithNewline(byte[] bytes, Encoding encoding)
    {
        if (bytes.Length == 0)
        {
            return true;
        }

        var text = encoding.GetString(bytes);
        return text.EndsWith('\n') || text.EndsWith('\r');
    }

    private static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length >= 3 && char.IsLetter(full[0]) && full[1] == ':' &&
            (full[2] == '\\' || full[2] == '/'))
        {
            return $"/mnt/{char.ToLowerInvariant(full[0])}/{full[3..].Replace('\\', '/')}";
        }

        return full.Replace('\\', '/');
    }

    private static string QuoteForBash(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void EnsureAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Profile installer paths must be absolute.", parameterName);
        }
    }

    private static ShellProfileStatus Failure(CwdProfileShell shell, string target, Exception error) =>
        new(shell, ProfileInstallState.Failed,
            $"WinMux could not install cwd reporting for {ShellName(shell)}. Nothing was intentionally removed. " +
            $"Windows reported: {error.Message}", target);

    private static string ShellName(CwdProfileShell shell) => shell switch
    {
        CwdProfileShell.WindowsPowerShell => "Windows PowerShell 5.1",
        CwdProfileShell.PowerShell => "PowerShell 7",
        CwdProfileShell.CommandPrompt => "cmd",
        CwdProfileShell.WslBash => "WSL bash",
        _ => shell.ToString(),
    };

    private static bool IsExpectedInstallFailure(Exception error) => error is
        IOException or UnauthorizedAccessException or InvalidOperationException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static string ReadSnippet(string fileName)
    {
        var assembly = typeof(ProfileInstaller).Assembly;
        var resourceName = assembly.GetManifestResourceNames().SingleOrDefault(name =>
            name.EndsWith(".Cwd.Profiles." + fileName, StringComparison.Ordinal));
        if (resourceName is null)
        {
            throw new InvalidOperationException($"The embedded cwd profile asset '{fileName}' is missing.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded cwd profile asset '{fileName}' could not be opened.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().TrimEnd('\r', '\n');
    }

    private sealed record TextFormat(Encoding Encoding, int PreambleLength, bool IsLegacyFallback);
}
