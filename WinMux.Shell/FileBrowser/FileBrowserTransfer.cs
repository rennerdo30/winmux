namespace WinMux.Shell.FileBrowser;

/// <summary>
/// Copying from one filesystem to another: a local folder onto an SFTP server, an FTP file into
/// Downloads, one server to a different one.
///
/// Within one filesystem the filesystem copies for itself (<see cref="IFileBrowserFileSystem.Copy"/>),
/// which on a local disk is the OS and on a server is the only path that avoids a round trip. Across
/// two there is nobody to delegate to, so the bytes come through WinMux: every directory is
/// enumerated on the source and created on the destination, and every file is streamed from one to
/// the other.
///
/// <para>
/// Two rules are carried over from the single-filesystem operations and are the point of this class:
/// nothing is ever overwritten (<see cref="IFileBrowserFileSystem.WriteNewFile"/> refuses an existing
/// name), and a file that was not completely written is removed, because a truncated file with the
/// right name is indistinguishable from the real one until somebody opens it.
/// </para>
/// </summary>
internal static class FileBrowserTransfer
{
    /// <summary>
    /// One transfer at a time, across every pane.
    ///
    /// A transfer holds the source filesystem's connection lock while it holds the destination's. Two
    /// transfers in opposite directions between the same two servers would take those locks in
    /// opposite orders and deadlock both panes. Serialising transfers removes the cycle, and costs
    /// nothing real: two transfers sharing one network link are not faster in parallel.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Deep enough for any real tree, and a stop for a symbolic link that leads to its own ancestor,
    /// which an SFTP listing reports as an ordinary directory.
    /// </summary>
    internal const int MaxDepth = 64;

    private const int BufferSize = 81920;

    /// <summary>
    /// Copy <paramref name="source"/> on <paramref name="from"/> to <paramref name="destination"/> on
    /// <paramref name="to"/>. The destination must not exist; the caller chose a free name.
    /// </summary>
    /// <returns>How many files were copied, for the status line.</returns>
    public static int Copy(
        IFileBrowserFileSystem from,
        string source,
        IFileBrowserFileSystem to,
        string destination,
        bool isDirectory,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        Gate.Wait(cancellationToken);
        try
        {
            var copied = 0;
            if (isDirectory)
            {
                CopyTree(from, source, to, destination, depth: 0, ref copied, cancellationToken, progress);
            }
            else
            {
                CopyFile(from, source, to, destination, cancellationToken);
                copied = 1;
            }

            return copied;
        }
        catch (ObjectDisposedException ex)
        {
            // The pane it was copied from has been closed, and its connection with it.
            throw new IOException("the pane it came from has been closed, and its connection with it", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void CopyTree(
        IFileBrowserFileSystem from,
        string source,
        IFileBrowserFileSystem to,
        string destination,
        int depth,
        ref int copied,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth)
        {
            throw new IOException(
                $"'{source}' is more than {MaxDepth} folders deep, which is usually a link that leads back into itself");
        }

        to.CreateDirectory(destination);

        // Materialised before anything is written: a lazily enumerated remote listing would hold the
        // source connection while the destination is being written to.
        foreach (var entry in from.EnumerateEntries(source).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = to.Combine(destination, entry.Name);

            if (entry.IsDirectory)
            {
                CopyTree(from, entry.Path, to, target, depth + 1, ref copied, cancellationToken, progress);
            }
            else
            {
                CopyFile(from, entry.Path, to, target, cancellationToken);
                copied++;
                progress?.Report($"Copying… {copied} file(s), last '{entry.Name}'");
            }
        }
    }

    private static void CopyFile(
        IFileBrowserFileSystem from,
        string source,
        IFileBrowserFileSystem to,
        string destination,
        CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            to.WriteNewFile(destination, write =>
            {
                created = true;
                from.ReadFile(source, read => Pump(read, write, cancellationToken));
            });
        }
        catch when (created)
        {
            // Only a file this call created is removed. If WriteNewFile refused because the name was
            // taken, the file there is somebody else's and must not be touched.
            TryDelete(to, destination);
            throw;
        }
    }

    /// <summary><see cref="Stream.CopyTo(Stream)"/>, but cancellable between blocks.</summary>
    private static void Pump(Stream read, Stream write, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        int count;
        while ((count = read.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            write.Write(buffer, 0, count);
        }
    }

    private static void TryDelete(IFileBrowserFileSystem filesystem, string path)
    {
        try
        {
            filesystem.Delete(path, isDirectory: false, permanent: true);
        }
        catch (Exception)
        {
            // The original failure is what the user needs to read. A partial file that could not be
            // cleaned up is left behind rather than hiding why the copy failed.
        }
    }
}
