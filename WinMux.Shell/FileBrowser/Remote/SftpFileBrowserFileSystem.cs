using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace WinMux.Shell.FileBrowser.Remote;

/// <summary>
/// A file browser over SFTP.
///
/// This is why <see cref="IFileBrowserFileSystem"/> exists as an interface rather than a static
/// call to <c>Directory</c>: the model, the pane, the clipboard and every operation above this
/// line are unchanged, and a share on another continent behaves like a folder.
///
/// It is also why the interface is synchronous and called on a background thread. Every method here
/// blocks on a network round trip; SSH.NET's own API is blocking, and the pane already runs these
/// calls off the UI thread through one known place.
///
/// SCP is deliberately not implemented separately. SCP is a copy command, not a browsable
/// filesystem — there is no directory listing in the protocol — and every server that speaks it
/// speaks SFTP, which is what a file browser actually needs.
/// </summary>
internal sealed class SftpFileBrowserFileSystem : IFileBrowserFileSystem, IDisposable
{
    private readonly SftpClient _client;
    private readonly object _gate = new();
    private bool _disposed;

    public SftpFileBrowserFileSystem(SftpClient client) => _client = client;

    /// <summary>Case-sensitive: the remote is a Unix filesystem until proven otherwise.</summary>
    public StringComparer PathComparer => StringComparer.Ordinal;

    public bool CanModify => true;

    /// <summary>
    /// SFTP has no Recycle Bin, and inventing one — a hidden <c>.winmux-trash</c> directory — would
    /// be a promise WinMux could not keep once anyone touched the server by other means. So Delete
    /// here is permanent, and the model refuses an undoable delete rather than quietly doing this.
    /// </summary>
    public bool CanRecoverDeletes => false;

    public string GetFullPath(string path) => RemotePath.Normalize(path);

    public string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public string? GetParentDirectory(string path) => RemotePath.Parent(path);

    public bool DirectoryExists(string path) => Run(client =>
    {
        var target = RemotePath.Normalize(path);
        return client.Exists(target) && client.GetAttributes(target).IsDirectory;
    });

    public bool FileExists(string path) => Run(client =>
    {
        var target = RemotePath.Normalize(path);
        return client.Exists(target) && !client.GetAttributes(target).IsDirectory;
    });

    public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path)
    {
        var directory = RemotePath.Normalize(path);
        var listing = Run(client => client.ListDirectory(directory).ToArray());

        foreach (var entry in listing)
        {
            // "." and ".." are entries in SFTP's listing and navigation in the browser is by the
            // Up button, so showing them would be two ways to do one thing, one of them confusing.
            if (entry.Name is "." or "..") continue;

            // A symlink to a directory is browsable, so follow it rather than reporting the link.
            var isDirectory = entry.IsDirectory || (entry.IsSymbolicLink && LeadsToDirectory(entry));
            yield return new FileBrowserNavigationItem(
                entry.Name,
                RemotePath.Combine(directory, entry.Name),
                isDirectory,
                Size: isDirectory ? null : entry.Length,
                // The server reports UTC; shown in local time like everything else in the column.
                Modified: new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc)));
        }
    }

    private bool LeadsToDirectory(ISftpFile entry)
    {
        try
        {
            return Run(client => client.GetAttributes(entry.FullName).IsDirectory);
        }
        catch (IOException)
        {
            // A broken link. Show it as a file; it is at least visible and deletable that way.
            return false;
        }
    }

    public void CreateDirectory(string path) => Run(client =>
    {
        client.CreateDirectory(RemotePath.Normalize(path));
        return true;
    });

    public void Move(string source, string destination, bool isDirectory) => Run(client =>
    {
        client.RenameFile(RemotePath.Normalize(source), RemotePath.Normalize(destination));
        return true;
    });

    public void Copy(string source, string destination, bool isDirectory, CancellationToken cancellationToken) =>
        Run(client =>
        {
            CopyInto(client, RemotePath.Normalize(source), RemotePath.Normalize(destination), isDirectory, cancellationToken);
            return true;
        });

    private static void CopyInto(
        SftpClient client, string source, string destination, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!isDirectory)
        {
            // SFTP has no server-side copy, so the bytes come to us and go back. That is the
            // protocol's limitation, not a shortcut: a remote-to-remote copy of a large file costs
            // twice its size in transfer, and there is no way around it from a client.
            using var read = client.OpenRead(source);
            using var write = client.Create(destination);
            read.CopyTo(write);
            return;
        }

        client.CreateDirectory(destination);
        foreach (var child in client.ListDirectory(source))
        {
            if (child.Name is "." or "..") continue;
            CopyInto(
                client,
                RemotePath.Combine(source, child.Name),
                RemotePath.Combine(destination, child.Name),
                child.IsDirectory,
                cancellationToken);
        }
    }

    public void ReadFile(string path, Action<Stream> read) => Run(client =>
    {
        using var stream = client.OpenRead(RemotePath.Normalize(path));
        read(stream);
        return true;
    });

    public void WriteNewFile(string path, Action<Stream> write) => Run(client =>
    {
        // CreateNew becomes SSH_FXF_CREAT | SSH_FXF_EXCL: the server refuses an existing name
        // rather than truncating it, which Create would do.
        using var stream = client.Open(RemotePath.Normalize(path), FileMode.CreateNew, FileAccess.Write);
        write(stream);
        return true;
    });

    public void Delete(string path, bool isDirectory, bool permanent)
    {
        if (!permanent)
        {
            // The model checks CanRecoverDeletes first, so this is a guard against a future caller
            // that forgets rather than a path a user reaches.
            throw new IOException("SFTP has no Recycle Bin; this delete would be permanent");
        }

        Run(client =>
        {
            DeleteTree(client, RemotePath.Normalize(path), isDirectory);
            return true;
        });
    }

    private static void DeleteTree(SftpClient client, string path, bool isDirectory)
    {
        if (!isDirectory)
        {
            client.DeleteFile(path);
            return;
        }

        // SFTP's rmdir fails on a non-empty directory and says only "failure", so empty it first.
        foreach (var child in client.ListDirectory(path))
        {
            if (child.Name is "." or "..") continue;
            DeleteTree(client, RemotePath.Combine(path, child.Name), child.IsDirectory);
        }

        client.DeleteDirectory(path);
    }

    /// <summary>
    /// Run one operation, connecting if necessary, and turn SSH.NET's exception vocabulary into the
    /// <see cref="IOException"/> the model already knows how to report. Without this a dropped
    /// connection surfaces as an unhandled <c>SshConnectionException</c> and takes the pane with it.
    /// </summary>
    private T Run<T>(Func<SftpClient, T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                if (!_client.IsConnected) _client.Connect();
                return operation(_client);
            }
            catch (SftpPathNotFoundException ex)
            {
                throw new IOException(ex.Message, ex);
            }
            catch (SftpPermissionDeniedException ex)
            {
                throw new UnauthorizedAccessException(ex.Message, ex);
            }
            catch (SshAuthenticationException ex)
            {
                throw new IOException($"the server refused the credentials: {ex.Message}", ex);
            }
            catch (SshConnectionException ex)
            {
                throw new IOException($"the connection to the server failed: {ex.Message}", ex);
            }
            catch (SshException ex)
            {
                throw new IOException(ex.Message, ex);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                throw new IOException($"the server could not be reached: {ex.Message}", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_client.IsConnected) _client.Disconnect();
            }
            catch (Exception)
            {
                // Disposing is the last thing that happens to this object; a server that has already
                // gone away has nothing to tell us.
            }

            _client.Dispose();
        }
    }
}
