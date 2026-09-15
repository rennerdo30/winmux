using FluentFTP;
using Renci.SshNet;
using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Platform;

namespace WinMux.Shell.FileBrowser.Remote;

/// <summary>
/// What a file-browser pane needs to know to be a remote one, and how it survives a session.
///
/// The connection lives in the pane's restore descriptor rather than in a side table, so a saved
/// session brings the remote pane back the same way it brings back a directory — that is priority 1
/// in CLAUDE.md section 1, and a remote pane that restored as a local one would be a quiet lie.
///
/// **No password is written here.** The descriptor is part of the session file, which is plain text
/// by design (ADR 0006). What is stored is enough to find the password again: scheme, host, port and
/// user, which is exactly the key <see cref="CredentialTarget.ForHost"/> builds.
/// </summary>
internal sealed record RemoteFileBrowserTarget(
    string Scheme,
    string Host,
    int Port,
    string User,
    string Identity = "")
{
    public const string SchemeExtra = "remote_scheme";
    public const string HostExtra = "remote_host";
    public const string PortExtra = "remote_port";
    public const string UserExtra = "remote_user";

    /// <summary>
    /// The private key file, for SFTP. A path, not a secret, so it belongs in the session file —
    /// the key it points at does not, and never passes through WinMux.
    /// </summary>
    public const string IdentityExtra = "remote_identity";

    /// <summary>Whether this connection authenticates with a key rather than a password.</summary>
    public bool UsesKey => Scheme == Sftp && Identity.Length > 0;

    public const string Sftp = "sftp";
    public const string Ftp = "ftp";

    /// <summary>What the user sees: <c>sftp://alice@build.example.com</c>.</summary>
    public string Display => User.Length == 0
        ? $"{Scheme}://{Host}"
        : $"{Scheme}://{User}@{Host}";

    /// <summary>The name its password is kept under.</summary>
    public string CredentialTargetName => CredentialTarget.ForHost(Scheme, Host, User);

    /// <summary>Read a target back out of a restore descriptor, or null for an ordinary local pane.</summary>
    public static RemoteFileBrowserTarget? From(RestoreDescriptor descriptor)
    {
        var extras = descriptor.Extras;
        if (!extras.TryGetValue(SchemeExtra, out var scheme) || string.IsNullOrWhiteSpace(scheme))
            return null;

        scheme = scheme.Trim().ToLowerInvariant();
        if (scheme is not (Sftp or Ftp)) return null;

        if (!extras.TryGetValue(HostExtra, out var host) || string.IsNullOrWhiteSpace(host))
            return null;

        _ = int.TryParse(
            extras.TryGetValue(PortExtra, out var portText) ? portText : null,
            out var port);

        extras.TryGetValue(UserExtra, out var user);
        extras.TryGetValue(IdentityExtra, out var identity);

        return new RemoteFileBrowserTarget(
            scheme, host.Trim(), port, user?.Trim() ?? string.Empty, identity?.Trim() ?? string.Empty);
    }

    /// <summary>Build the target a connection profile describes.</summary>
    public static RemoteFileBrowserTarget? From(LaunchProfile profile)
    {
        var scheme = profile.Kind switch
        {
            ProfileKind.Sftp => Sftp,
            ProfileKind.Ftp => Ftp,
            _ => null,
        };

        if (scheme is null || string.IsNullOrWhiteSpace(profile.Host)) return null;

        return new RemoteFileBrowserTarget(
            scheme,
            profile.Host.Trim(),
            profile.Port > 0 ? profile.Port : RemoteConnection.DefaultPortFor(profile.Kind),
            profile.User.Trim(),
            profile.Identity.Trim());
    }

    /// <summary>The extras that carry this target through a save and back.</summary>
    public IReadOnlyDictionary<string, string> ToExtras(string directory)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchemeExtra] = Scheme,
            [HostExtra] = Host,
            [PortExtra] = Port.ToString(),
            [UserExtra] = User,
            [FileBrowserModel.CurrentDirectoryExtra] = directory,
        };

        // Only when there is one, so an ordinary password connection does not carry an empty key.
        if (Identity.Length > 0) extras[IdentityExtra] = Identity;

        return extras;
    }

    /// <summary>Plain FTP carries the password and the files in clear text; SFTP does not.</summary>
    public bool IsClearText => Scheme == Ftp;

    public int EffectivePort => Port > 0
        ? Port
        : Scheme == Ftp ? RemoteConnection.DefaultFtpPort : RemoteConnection.DefaultSftpPort;
}

/// <summary>
/// Turning a target plus a credential into a connected filesystem.
///
/// Deliberately not doing the connecting itself at construction time: the first call blocks on the
/// network, and the pane runs its filesystem calls on a background thread. Connecting lazily inside
/// the first operation means a slow or unreachable host shows as a status message in the pane rather
/// than a frozen window.
/// </summary>
internal static class RemoteFileSystemFactory
{
    public static IFileBrowserFileSystem Create(RemoteFileBrowserTarget target, StoredCredential credential)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(credential);

        return target.Scheme == RemoteFileBrowserTarget.Ftp
            ? CreateFtp(target, credential)
            : CreateSftp(target, credential);
    }

    private static IFileBrowserFileSystem CreateSftp(RemoteFileBrowserTarget target, StoredCredential credential)
    {
        var user = credential.User.Length > 0 ? credential.User : target.User;

        if (!target.UsesKey)
        {
            return new SftpFileBrowserFileSystem(
                new SftpClient(target.Host, target.EffectivePort, user, credential.Secret));
        }

        // Key authentication. The profile already had an Identity field for SSH; using it here means
        // an SFTP connection to a host you already reach by key needs no password at all, and the
        // key itself never passes through WinMux — SSH.NET reads the file.
        PrivateKeyFile key;
        try
        {
            key = credential.Secret.Length > 0
                ? new PrivateKeyFile(target.Identity, credential.Secret)
                : new PrivateKeyFile(target.Identity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Wrong passphrase, unreadable file, unsupported format. The model reports IOException,
            // and the path is worth naming because "it did not work" is useless when the setting is
            // a file name.
            throw new IOException($"the key file '{target.Identity}' could not be used: {ex.Message}", ex);
        }

        var info = new ConnectionInfo(
            target.Host,
            target.EffectivePort,
            user,
            new PrivateKeyAuthenticationMethod(user, key));

        return new SftpFileBrowserFileSystem(new SftpClient(info));
    }

    private static IFileBrowserFileSystem CreateFtp(RemoteFileBrowserTarget target, StoredCredential credential)
    {
        var user = credential.User.Length > 0 ? credential.User : target.User;
        var client = new FtpClient(target.Host, user, credential.Secret, target.EffectivePort)
        {
            Config =
            {
                // Upgrade to FTPS where the server offers it. Auto rather than Explicit so that a
                // server with no TLS still connects — those are exactly the old devices that have
                // nothing but FTP, and refusing would make WinMux unable to reach them at all. The
                // pane reports which way it went.
                EncryptionMode = FtpEncryptionMode.Auto,
                ValidateAnyCertificate = false,
            },
        };

        return new FtpFileBrowserFileSystem(client);
    }
}
