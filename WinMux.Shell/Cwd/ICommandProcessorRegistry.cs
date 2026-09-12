using Microsoft.Win32;

namespace WinMux.Shell.Cwd;

/// <summary>A string registry value, including whether environment variables expand when read.</summary>
public sealed record RegistryStringValue(bool Exists, string? Value, bool ExpandEnvironmentVariables = false)
{
    public static RegistryStringValue Missing { get; } = new(false, null);
}

/// <summary>
/// Abstraction over HKCU\Software\Microsoft\Command Processor. It keeps tests away from the
/// real user registry and makes the installer's current-user boundary explicit.
/// </summary>
public interface ICommandProcessorRegistry
{
    RegistryStringValue Read(string valueName);

    void Write(string valueName, RegistryStringValue value);

    void Delete(string valueName);
}

/// <summary>The real current-user cmd profile registry store.</summary>
public sealed class CurrentUserCommandProcessorRegistry : ICommandProcessorRegistry
{
    private const string KeyPath = @"Software\Microsoft\Command Processor";

    public RegistryStringValue Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return RegistryStringValue.Missing;
        }

        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is not string text)
        {
            throw new InvalidDataException($"The cmd {valueName} registry value is not text.");
        }

        var expandable = key.GetValueKind(valueName) == RegistryValueKind.ExpandString;
        return new RegistryStringValue(true, text, expandable);
    }

    public void Write(string valueName, RegistryStringValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Exists || value.Value is null)
        {
            throw new ArgumentException("A registry value to write must exist and contain text.", nameof(value));
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not allow the current-user cmd profile key to be opened.");
        key.SetValue(
            valueName,
            value.Value,
            value.ExpandEnvironmentVariables ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
