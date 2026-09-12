using System.Text;
using WinMux.Shell.Cwd;

namespace WinMux.Shell.Tests;

public sealed class ProfileInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winmux-profile-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();

    [Fact]
    public void PowerShell_profiles_are_appended_backed_up_and_reported_separately()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowsPowerShellProfile)!);
        var original = Encoding.UTF8.GetBytes("function prompt { 'mine' }\n# keep me\n");
        File.WriteAllBytes(paths.WindowsPowerShellProfile, original);

        var report = CreateInstaller(paths).Install();

        var windowsPowerShell = Status(report, CwdProfileShell.WindowsPowerShell);
        var powerShell = Status(report, CwdProfileShell.PowerShell);
        Assert.Equal(ProfileInstallState.Installed, windowsPowerShell.State);
        Assert.Equal(ProfileInstallState.Installed, powerShell.State);
        Assert.NotNull(windowsPowerShell.Backup);
        Assert.Equal(original, File.ReadAllBytes(windowsPowerShell.Backup!));
        Assert.True(File.ReadAllBytes(paths.WindowsPowerShellProfile).AsSpan()[..original.Length].SequenceEqual(original));
        Assert.Contains("# >>> WinMux cwd reporting [winmux-cwd-v1] >>>", File.ReadAllText(paths.WindowsPowerShellProfile));
        Assert.Contains(". '", File.ReadAllText(paths.PowerShellProfile));
    }

    [Fact]
    public void Reinstall_is_idempotent_and_does_not_make_another_backup()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.WindowsPowerShellProfile)!);
        File.WriteAllText(paths.WindowsPowerShellProfile, "# user profile\n");
        var installer = CreateInstaller(paths);
        installer.Install();
        var afterFirstInstall = File.ReadAllBytes(paths.WindowsPowerShellProfile);
        var backupsAfterFirstInstall = Directory.GetFiles(
            Path.GetDirectoryName(paths.WindowsPowerShellProfile)!, "*.winmux-backup-*");

        var second = installer.Install();

        Assert.Equal(ProfileInstallState.AlreadyInstalled,
            Status(second, CwdProfileShell.WindowsPowerShell).State);
        Assert.Equal(afterFirstInstall, File.ReadAllBytes(paths.WindowsPowerShellProfile));
        Assert.Equal(backupsAfterFirstInstall,
            Directory.GetFiles(Path.GetDirectoryName(paths.WindowsPowerShellProfile)!, "*.winmux-backup-*"));
    }

    [Fact]
    public void Utf16_profile_keeps_its_encoding_and_original_bytes()
    {
        var paths = CreatePaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PowerShellProfile)!);
        var preamble = Encoding.Unicode.GetPreamble();
        var body = Encoding.Unicode.GetBytes("# café\r\n");
        var original = preamble.Concat(body).ToArray();
        File.WriteAllBytes(paths.PowerShellProfile, original);

        var result = Status(CreateInstaller(paths).Install(), CwdProfileShell.PowerShell);
        var modified = File.ReadAllBytes(paths.PowerShellProfile);

        Assert.Equal(ProfileInstallState.Installed, result.State);
        Assert.True(modified.AsSpan()[..original.Length].SequenceEqual(original));
        Assert.StartsWith("# café\r\n# >>> WinMux", Encoding.Unicode.GetString(modified[preamble.Length..]));
        Assert.Equal(original, File.ReadAllBytes(result.Backup!));
    }

    [Fact]
    public void Cmd_preserves_existing_autorun_and_writes_a_recovery_value()
    {
        var paths = CreatePaths();
        _registry.Values["AutoRun"] = new RegistryStringValue(true, "doskey /macrofile=C:\\mine.txt", true);

        var status = Status(CreateInstaller(paths).Install(), CwdProfileShell.CommandPrompt);

        Assert.Equal(ProfileInstallState.Installed, status.State);
        var autoRun = _registry.Values["AutoRun"];
        Assert.StartsWith("doskey /macrofile=C:\\mine.txt & call \"", autoRun.Value);
        Assert.Contains("WinMux cwd reporting [winmux-cwd-v1]", autoRun.Value);
        Assert.True(autoRun.ExpandEnvironmentVariables);
        Assert.NotNull(status.Backup);
        var backupName = status.Backup!.Split('\\')[^1];
        Assert.Equal("doskey /macrofile=C:\\mine.txt", _registry.Values[backupName].Value);
    }

    [Fact]
    public void Cmd_reinstall_is_idempotent()
    {
        var paths = CreatePaths();
        var installer = CreateInstaller(paths);
        installer.Install();
        var afterFirst = _registry.Values.ToDictionary(pair => pair.Key, pair => pair.Value);

        var status = Status(installer.Install(), CwdProfileShell.CommandPrompt);

        Assert.Equal(ProfileInstallState.AlreadyInstalled, status.State);
        Assert.Equal(afterFirst, _registry.Values);
    }

    [Fact]
    public void Wsl_returns_a_precise_manual_instruction_without_touching_bashrc()
    {
        var paths = CreatePaths();
        var pretendBashrc = Path.Combine(_root, ".bashrc");
        Directory.CreateDirectory(_root);
        File.WriteAllText(pretendBashrc, "# mine\n");

        var status = Status(CreateInstaller(paths).Install(), CwdProfileShell.WslBash);

        Assert.Equal(ProfileInstallState.ManualActionRequired, status.State);
        Assert.Contains("add this line to ~/.bashrc: source '/mnt/", status.Message);
        Assert.Equal("# mine\n", File.ReadAllText(pretendBashrc).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(Directory.GetFiles(paths.IntegrationDirectory, "*.sh").Length == 1);
    }

    [Fact]
    public void Inspect_reports_status_without_mutating_files_or_registry()
    {
        var paths = CreatePaths();

        var report = CreateInstaller(paths).Inspect();

        Assert.Equal(ProfileInstallState.NotInstalled,
            Status(report, CwdProfileShell.WindowsPowerShell).State);
        Assert.Equal(ProfileInstallState.NotInstalled,
            Status(report, CwdProfileShell.CommandPrompt).State);
        Assert.Equal(ProfileInstallState.ManualActionRequired,
            Status(report, CwdProfileShell.WslBash).State);
        Assert.False(Directory.Exists(paths.IntegrationDirectory));
        Assert.Empty(_registry.Values);
    }

    [Fact]
    public void Registry_failure_is_returned_in_plain_words_and_does_not_escape()
    {
        var paths = CreatePaths();
        _registry.Error = new UnauthorizedAccessException("access was denied");

        var status = Status(CreateInstaller(paths).Install(), CwdProfileShell.CommandPrompt);

        Assert.Equal(ProfileInstallState.Failed, status.State);
        Assert.Contains("could not install cwd reporting for cmd", status.Message);
        Assert.Contains("access was denied", status.Message);
    }

    private ProfileInstallerPaths CreatePaths() => new(
        Path.Combine(_root, "Documents", "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
        Path.Combine(_root, "Documents", "PowerShell", "Microsoft.PowerShell_profile.ps1"),
        Path.Combine(_root, "LocalAppData", "WinMux", "profiles"));

    private ProfileInstaller CreateInstaller(ProfileInstallerPaths paths) =>
        new(paths, _registry, () => new DateTimeOffset(2026, 9, 12, 1, 2, 3, TimeSpan.Zero));

    private static ShellProfileStatus Status(ProfileInstallationReport report, CwdProfileShell shell) =>
        Assert.Single(report.Shells, result => result.Shell == shell);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeRegistry : ICommandProcessorRegistry
    {
        public Dictionary<string, RegistryStringValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Exception? Error { get; set; }

        public RegistryStringValue Read(string valueName)
        {
            ThrowIfConfigured();
            return Values.TryGetValue(valueName, out var value) ? value : RegistryStringValue.Missing;
        }

        public void Write(string valueName, RegistryStringValue value)
        {
            ThrowIfConfigured();
            Values[valueName] = value;
        }

        public void Delete(string valueName)
        {
            ThrowIfConfigured();
            Values.Remove(valueName);
        }

        private void ThrowIfConfigured()
        {
            if (Error is not null)
            {
                throw Error;
            }
        }
    }
}
