using Microsoft.Win32;
using WinMux.Connections;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// The registry under <c>HKEY_CURRENT_USER</c>, which is where PuTTY and WinSCP keep their saved
/// sessions.
///
/// <para>
/// Per-user only, and deliberately. A source reads somebody's own saved connections; nothing here
/// has a reason to touch <c>HKLM</c>, and not being able to is a better guarantee than remembering
/// not to.
/// </para>
/// </summary>
public sealed class Win32RegistryStore : IRegistryStore
{
    public bool KeyExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> SubKeyNames(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
                                             or IOException)
        {
            return [];
        }
    }

    public string? ReadValue(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(name);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            // Invariant formatting: a DWORD comes back as an int, and the caller parses text.
            return key?.GetValue(name) switch
            {
                null => null,
                string text => text,
                int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                var other => other.ToString(),
            };
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
                                             or IOException)
        {
            return null;
        }
    }

    public void WriteValue(string path, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
                ?? throw new ConnectionSourceException($@"HKCU\{path} could not be opened for writing.");

            // The kind an existing value already has is kept. Rewriting a DWORD port as a string
            // leaves a session the other tool can no longer read, which is worse than not saving.
            var kind = key.GetValue(name) is null ? RegistryValueKind.String : key.GetValueKind(name);
            key.SetValue(name, Convert(value, kind), kind);
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
                                             or IOException)
        {
            throw new ConnectionSourceException($@"HKCU\{path} could not be written: {exception.Message}", exception);
        }
    }

    private static object Convert(string value, RegistryValueKind kind) =>
        kind == RegistryValueKind.DWord &&
        int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : value;

    /// <summary>
    /// The registry has no rename, so this is a copy and a delete. Done in that order: a failure
    /// partway through leaves two keys rather than none, and two keys is something the user can
    /// see and sort out.
    /// </summary>
    public bool RenameSubKey(string path, string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (string.Equals(from, to, StringComparison.Ordinal)) return true;

        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (parent is null) return false;

            using var source = parent.OpenSubKey(from);
            if (source is null) return false;

            using (var target = parent.CreateSubKey(to, writable: true))
            {
                if (target is null) return false;
                foreach (var name in source.GetValueNames())
                {
                    var value = source.GetValue(name);
                    if (value is not null) target.SetValue(name, value, source.GetValueKind(name));
                }
            }

            parent.DeleteSubKeyTree(from, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException
                                             or IOException)
        {
            throw new ConnectionSourceException(
                $@"HKCU\{path}\{from} could not be renamed: {exception.Message}", exception);
        }
    }
}
