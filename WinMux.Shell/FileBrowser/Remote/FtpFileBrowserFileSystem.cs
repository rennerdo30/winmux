using FluentFTP;
using FluentFTP.Exceptions;

namespace WinMux.Shell.FileBrowser.Remote;

/// <summary>
/// A file browser over FTP, and over FTPS when the server offers it.
///
/// The same shape as <see cref="SftpFileBrowserFileSystem"/> and for the same reason: everything
/// above <see cref="IFileBrowserFileSystem"/> stays as it is. What differs is what the protocol can
/// promise, and the honest answer for FTP is "less".
///
/// <para>
/// Plain FTP sends the password and every byte in clear text. WinMux asks FluentFTP to use TLS when
/// the server supports it (<c>FtpEncryptionMode.Auto</c>), which upgrades silently where possible,
/// but a server that offers no encryption still connects — refusing would make WinMux unable to
/// reach exactly the old devices that still speak only FTP. The pane says which happened, because
/// "my password went over the wire in clear" is not something to find out later.
/// </para>
/// </summary>
internal sealed class FtpFileBrowserFileSystem : IFileBrowserFileSystem, IDisposable
{
    private readonly FtpClient _client;
    private readonly object _gate = new();
    private bool _disposed;

    public FtpFileBrowserFileSystem(FtpClient client) => _client = client;

    /// <summary>
    /// Ordinal. FTP servers are usually Unix, and treating two names that differ only in case as one
    /// would let a copy silently overwrite a different file.
    /// </summary>
    public StringComparer PathComparer => StringComparer.Ordinal;

    public bool CanModify => true;

    /// <summary>FTP has no trash either. Delete is permanent and the model must say so.</summary>
    public bool CanRecoverDeletes => false;

    /// <summary>True when the connection actually ended up encrypted, for the pane to report.</summary>
    public bool IsEncrypted => _client.IsEncrypted;

    public string GetFullPath(string path) => RemotePath.Normalize(path);

    public string Combine(string directory, string name) => RemotePath.Combine(directory, name);

    public string? GetParentDirectory(string path) => RemotePath.Parent(path);

    public bool DirectoryExists(string path) => Run(client => client.DirectoryExists(RemotePath.Normalize(path)));

    public bool FileExists(string path) => Run(client => client.FileExists(RemotePath.Normalize(path)));

    public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path)
    {
        var directory = RemotePath.Normalize(path);
        var listing = Run(client => client.GetListing(directory));

        foreach (var entry in listing)
        {
            if (entry.Name is "." or "..") continue;

            var isDirectory = entry.Type is FtpObjectType.Directory;
            yield return new FileBrowserNavigationItem(
                entry.Name,
                RemotePath.Combine(directory, entry.Name),
                isDirectory,
                // FluentFTP reports -1 and DateTime.MinValue for what the listing format did not
                // include; a blank column is honest, a zero or year-1 date is not.
                Size: isDirectory || entry.Size < 0 ? null : entry.Size,
                Modified: entry.Modified == DateTime.MinValue ? null : new DateTimeOffset(entry.Modified));
        }
    }

    public void CreateDirectory(string path) => Run(client =>
    {
        client.CreateDirectory(RemotePath.Normalize(path));
        return true;
    });

    public void Move(string source, string destination, bool isDirectory) => Run(client =>
    {
        var from = RemotePath.Normalize(source);
        var to = RemotePath.Normalize(destination);
        if (isDirectory) client.MoveDirectory(from, to);
        else client.MoveFile(from, to);
        return true;
    });

    public void Copy(string source, string destination, bool isDirectory, CancellationToken cancellationToken) =>
        Run(client =>
        {
            CopyInto(client, RemotePath.Normalize(source), RemotePath.Normalize(destination), isDirectory, cancellationToken);
            return true;
        });

    private static void CopyInto(
        FtpClient client, string source, string destination, bool isDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!isDirectory)
        {
            // No server-side copy in FTP either: down and back up again.
            using var read = client.OpenRead(source);
            using var write = client.OpenWrite(destination);
            read.CopyTo(write);
            return;
        }

        client.CreateDirectory(destination);
        foreach (var child in client.GetListing(source))
        {
            if (child.Name is "." or "..") continue;
            CopyInto(
                client,
                RemotePath.Combine(source, child.Name),
                RemotePath.Combine(destination, child.Name),
                child.Type is FtpObjectType.Directory,
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
        var target = RemotePath.Normalize(path);

        // FTP has no exclusive create; STOR replaces whatever is there. Checking first, under the
        // same lock as every other call on this connection, is the most the protocol allows.
        if (client.FileExists(target) || client.DirectoryExists(target))
        {
            throw new IOException($"'{target}' already exists");
        }

        using var stream = client.OpenWrite(target);
        write(stream);
        return true;
    });

    public void Delete(string path, bool isDirectory, bool permanent)
    {
        if (!permanent) throw new IOException("FTP has no Recycle Bin; this delete would be permanent");

        Run(client =>
        {
            var target = RemotePath.Normalize(path);
            // FluentFTP empties a directory itself; its recursive delete is the tested path.
            if (isDirectory) client.DeleteDirectory(target);
            else client.DeleteFile(target);
            return true;
        });
    }

    private T Run<T>(Func<FtpClient, T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            try
            {
                if (!_client.IsConnected) _client.Connect();
                return operation(_client);
            }
            catch (FtpAuthenticationException ex)
            {
                throw new IOException($"the server refused the credentials: {ex.Message}", ex);
            }
            catch (FtpCommandException ex)
            {
                throw new IOException($"the server refused: {ex.Message}", ex);
            }
            catch (FtpException ex)
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
                // Nothing useful to learn from a server that has already gone.
            }

            _client.Dispose();
        }
    }
}
