namespace WinMux.Platform;

/// <summary>
/// A saved user name and secret.
///
/// The secret is a <see cref="string"/> rather than anything cleverer. .NET strings cannot be
/// reliably zeroed — they are immutable, interned and moved by the collector — and
/// <c>SecureString</c> is documented by Microsoft as not recommended for new development precisely
/// because it cannot deliver what its name promises on a modern runtime.
///
/// The protection that is real is elsewhere: the secret lives in the operating system's store,
/// encrypted to the user, and is materialised only at the moment a connection is made. So the rules
/// for callers are to fetch it late, hold it briefly, and never write it anywhere — a log line, an
/// exception message or a status bar is a leak that no string type would have prevented.
/// </summary>
/// <param name="User">The user name. May be empty for a secret with no account attached.</param>
/// <param name="Secret">The password, token or passphrase.</param>
public sealed record StoredCredential(string User, string Secret);

/// <summary>
/// Somewhere to keep a password that is not a file WinMux owns.
///
/// WinMux stores no secret of its own: SSH delegates to the agent and RDP to `mstsc`, both of which
/// already use the system's store. A protocol WinMux speaks *itself* — SMB, and later SFTP or FTP —
/// has nobody to delegate to, so it needs one, and the answer must not be the profiles file. That
/// file is plain text, hand-editable, copied between machines and committed to repositories by
/// mistake; it is the last place a password should be.
///
/// So this is the contract, and like everything else in <c>WinMux.Platform</c> it states intent
/// rather than steps: keep this secret under this name, give it back, forget it. Windows implements
/// it with Credential Manager, which encrypts per user. A system with no such facility implements
/// it by refusing, and the caller asks for the password each time instead — which is worse but
/// still correct, where inventing our own encrypted file would be neither.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Whether this system has a credential store at all. False means every other method fails,
    /// and a caller should prompt each time rather than pretend a saved password exists.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Save a credential under <paramref name="target"/>, replacing any credential already there.
    /// </summary>
    /// <param name="target">The name to keep it under; see <see cref="CredentialTarget"/>.</param>
    /// <param name="credential">The user name and secret to keep.</param>
    /// <param name="error">Why it failed, in words a user can act on, or null on success.</param>
    bool TrySave(string target, StoredCredential credential, out string? error);

    /// <summary>The credential saved under <paramref name="target"/>, or null if there is none.</summary>
    StoredCredential? Load(string target);

    /// <summary>Forget it. True when something was removed; false when there was nothing to remove.</summary>
    bool Delete(string target);
}

/// <summary>
/// How a credential is named in the store.
///
/// Namespaced, because the store is shared with every other application on the machine and a bare
/// host name would collide with whatever else has saved one — and because a user looking at
/// Credential Manager should be able to see which entries are WinMux's and delete them.
/// </summary>
public static class CredentialTarget
{
    public const string Prefix = "WinMux";

    /// <summary>The target for a saved connection profile.</summary>
    public static string ForProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return $"{Prefix}:profile:{profileId.Trim()}";
    }

    /// <summary>
    /// The target for a host reached without a saved profile — an ad-hoc share, say.
    ///
    /// Keyed by scheme, host and user together: the same machine reached over SMB and over SFTP is
    /// two different passwords, and two accounts on one host are two more.
    /// </summary>
    public static string ForHost(string scheme, string host, string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return $"{Prefix}:{scheme.Trim().ToLowerInvariant()}:{host.Trim().ToLowerInvariant()}:{user?.Trim() ?? string.Empty}";
    }
}
