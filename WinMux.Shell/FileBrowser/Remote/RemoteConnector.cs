using Avalonia.Controls;
using WinMux.Platform;

namespace WinMux.Shell.FileBrowser.Remote;

/// <summary>
/// Getting a credential for a remote host, and turning it into a filesystem.
///
/// The order matters and is the whole content of this class: try the credential store first, ask
/// only if it has nothing, and save only when the user says to. A file browser that prompts on
/// every reconnect trains people to type passwords without reading the dialog, and one that saves
/// without asking puts a secret somewhere the user did not choose.
/// </summary>
internal sealed class RemoteConnector(ICredentialStore credentials)
{
    private readonly ICredentialStore _credentials = credentials;

    /// <summary>
    /// Connect to <paramref name="target"/>, prompting if necessary.
    /// </summary>
    /// <param name="owner">The window to own the prompt. Without one, only a saved credential works.</param>
    /// <param name="askAgain">
    /// True after the server refused what was tried: skip the saved password, which is the one it
    /// refused, and ask. Ticking "remember" in the prompt then replaces it.
    /// </param>
    /// <returns>The filesystem, or null when the user cancelled or nothing could be obtained.</returns>
    public async Task<IFileBrowserFileSystem?> ConnectAsync(RemoteFileBrowserTarget target, Window? owner, bool askAgain = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!askAgain && Saved(target) is { } saved)
        {
            return RemoteFileSystemFactory.Create(target, saved);
        }

        if (target.UsesKey && !askAgain)
        {
            // A key connection with nothing saved means an unencrypted key, which is the common case
            // for one generated for a machine. Asking for a password here would be asking for
            // something that does not exist; if the key turns out to need a passphrase, the attempt
            // fails with a message naming the file, and saving a credential supplies it next time.
            return RemoteFileSystemFactory.Create(target, new StoredCredential(target.User, string.Empty));
        }

        if (owner is null)
        {
            // No window to ask in — during a headless restore, say. Better to open the pane with an
            // explanation than to block startup on a dialog nobody can see.
            return null;
        }

        var prompt = new CredentialWindow(
            target.Display,
            target.User,
            canRemember: _credentials.IsAvailable,
            warning: target.IsClearText
                ? "FTP sends your password and your files in clear text unless the server offers " +
                  "FTPS. WinMux will use FTPS if it can."
                : null);

        await prompt.ShowDialog(owner);

        if (prompt.User is not { } user) return null;

        var credential = new StoredCredential(user, prompt.Secret);

        if (prompt.Remember && _credentials.IsAvailable)
        {
            // A failure to save is not a failure to connect: say nothing here and let the connection
            // proceed. The user will simply be asked again next time.
            _credentials.TrySave(WithUser(target, user).CredentialTargetName, credential, out _);
        }

        return RemoteFileSystemFactory.Create(WithUser(target, user), credential);
    }

    /// <summary>Forget a saved password, for when the server starts refusing it.</summary>
    public bool Forget(RemoteFileBrowserTarget target) =>
        _credentials.IsAvailable && _credentials.Delete(target.CredentialTargetName);

    private StoredCredential? Saved(RemoteFileBrowserTarget target) =>
        _credentials.IsAvailable ? _credentials.Load(target.CredentialTargetName) : null;

    /// <summary>
    /// The credential is keyed by user, so a profile that left the user blank must be re-keyed once
    /// the user has typed one — otherwise the password saves under a name the next lookup will not
    /// build.
    /// </summary>
    private static RemoteFileBrowserTarget WithUser(RemoteFileBrowserTarget target, string user) =>
        target.User == user ? target : target with { User = user };
}
