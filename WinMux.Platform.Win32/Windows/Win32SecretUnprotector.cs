using System.Security.Cryptography;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// DPAPI, which is how Windows protects a secret for one user on one machine.
///
/// <para>
/// Remote Desktop Connection Manager stores its passwords this way. The tie to the account is the
/// feature rather than an obstacle: a <c>.rdg</c> file copied from a colleague keeps its servers
/// and loses its passwords, and WinMux reading it on their machine and not on anyone else's is
/// exactly right.
/// </para>
/// </summary>
public static class Win32SecretUnprotector
{
    /// <summary>
    /// Undo <c>CryptProtectData</c> for the current user, or return null when this account cannot.
    ///
    /// <para>
    /// Null rather than an exception, because "this was saved by somebody else" is the ordinary
    /// case for a file that has been shared, not a failure worth interrupting anyone about.
    /// </para>
    /// </summary>
    public static byte[]? Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        if (protectedBytes.Length == 0) return null;

        try
        {
            return ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
