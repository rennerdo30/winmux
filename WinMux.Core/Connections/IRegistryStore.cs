namespace WinMux.Core.Connections;

/// <summary>
/// The little of the Windows registry that saved-session formats need.
///
/// <para>
/// PuTTY and WinSCP keep their sessions under <c>HKEY_CURRENT_USER</c> rather than in a file, so a
/// source for either is a registry reader. The reading is three operations; putting them behind
/// this keeps the sources themselves in <c>WinMux.Core</c>, where they are ordinary logic that can
/// be tested against an invented registry instead of whatever is on the machine — and keeps Core
/// free of a platform reference, which CLAUDE.md section 3 requires.
/// </para>
///
/// <para>
/// Paths are relative to <c>HKCU</c> and use backslashes, as the registry does:
/// <c>Software\SimonTatham\PuTTY\Sessions</c>.
/// </para>
/// </summary>
public interface IRegistryStore
{
    /// <summary>Whether the key is there at all.</summary>
    bool KeyExists(string path);

    /// <summary>The names of the immediate subkeys, or empty when the key is absent.</summary>
    IReadOnlyList<string> SubKeyNames(string path);

    /// <summary>A value under a key, as text, or null when either is absent.</summary>
    string? ReadValue(string path, string name);

    /// <summary>
    /// Write a value, creating the key if it is not there. Throws
    /// <see cref="ConnectionSourceException"/> when the registry refuses.
    /// </summary>
    void WriteValue(string path, string name, string value);

    /// <summary>Rename a subkey, for when a session is renamed. Returns false when it is not there.</summary>
    bool RenameSubKey(string path, string from, string to);
}
