using WinMux.Connections;

namespace WinMux.Platform.Win32.Windows;

/// <summary>Per-user App Paths registration used by ShellExecute and Explorer.</summary>
public sealed class Win32ApplicationRegistration(IRegistryStore registry) : IApplicationRegistration
{
    public Win32ApplicationRegistration() : this(new Win32RegistryStore()) { }

    public void RegisterExecutable(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Application registration requires an absolute .exe path.", nameof(executablePath));

        var fullPath = Path.GetFullPath(executablePath);
        var key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\" + Path.GetFileName(fullPath);
        if (string.Equals(registry.ReadValue(key, string.Empty), fullPath, StringComparison.OrdinalIgnoreCase)) return;
        // IRegistryStore is HKCU-only. The default value holds the full path, without quotes.
        // No Path value is needed, and neither the user nor machine PATH is changed.
        registry.WriteValue(key, string.Empty, fullPath);
    }
}
