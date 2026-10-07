using WinMux.Connections;
using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Shell.Tests;

public sealed class ApplicationRegistrationTests
{
    private sealed class Registration : IApplicationRegistration
    {
        public string? Registered;
        public Exception? Failure;
        public void RegisterExecutable(string executablePath)
        {
            if (Failure is not null) throw Failure;
            Registered = executablePath;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\tools\dotnet.exe")]
    [InlineData(@"C:\tools\testhost.exe")]
    [InlineData(@"C:\tools\WinMux.Shell.Tests.exe")]
    public void Non_gui_processes_do_not_register(string? executable)
    {
        var registration = new Registration();
        Assert.Null(ApplicationRegistration.RegisterCurrentExecutable(executable, registration));
        Assert.Null(registration.Registered);
    }

    [Fact]
    public void Gui_registers_the_current_executable_including_spaces()
    {
        var registration = new Registration();
        const string path = @"C:\Portable apps\WinMux\WinMux.exe";
        Assert.Null(ApplicationRegistration.RegisterCurrentExecutable(path, registration));
        Assert.Equal(path, registration.Registered);
    }

    [Fact]
    public void Registration_failure_is_actionable_without_preventing_startup()
    {
        var registration = new Registration { Failure = new ConnectionSourceException("Access denied") };
        var notice = ApplicationRegistration.RegisterCurrentExecutable(@"C:\apps\WinMux.exe", registration);
        Assert.Contains("Access denied", notice);
        Assert.Contains(@"C:\apps\WinMux.exe", notice);
        Assert.Contains("directly", notice);
    }

    private sealed class Registry : IRegistryStore
    {
        public readonly Dictionary<(string Key, string Name), string> Values = [];
        public int Writes;
        public bool KeyExists(string path) => false;
        public IReadOnlyList<string> SubKeyNames(string path) => [];
        public string? ReadValue(string path, string name) => Values.GetValueOrDefault((path, name));
        public void WriteValue(string path, string name, string value) { Values[(path, name)] = value; Writes++; }
        public bool RenameSubKey(string path, string from, string to) => throw new NotSupportedException();
    }

    [Fact]
    public void App_paths_registers_default_value_updates_moved_folder_and_skips_unchanged_path()
    {
        var registry = new Registry();
        var registration = new Win32ApplicationRegistration(registry);
        const string key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\WinMux.exe";
        const string original = @"C:\Portable apps\WinMux.exe";
        registration.RegisterExecutable(original);
        registration.RegisterExecutable(original);
        Assert.Equal(1, registry.Writes);
        Assert.Equal(original, registry.Values[(key, "")]);
        const string moved = @"D:\Moved apps\WinMux.exe";
        registration.RegisterExecutable(moved);
        Assert.Equal(moved, registry.Values[(key, "")]);
        Assert.Single(registry.Values); // No PATH or machine-level registration.
    }

    [Theory]
    [InlineData("WinMux.exe")]
    [InlineData(@"C:\apps\WinMux.dll")]
    public void App_paths_rejects_relative_or_non_executable_paths(string path)
    {
        var registry = new Registry();
        Assert.Throws<ArgumentException>(() => new Win32ApplicationRegistration(registry).RegisterExecutable(path));
        Assert.Empty(registry.Values);
    }
}
