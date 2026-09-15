using WinMux.Platform;

namespace WinMux.Shell.FileBrowser;

/// <summary>
/// Narrow filesystem boundary for the navigation model. It keeps directory policy testable and
/// leaves platform-specific presentation concerns outside the model.
///
/// It is also the seam a non-local filesystem plugs into — SFTP, FTP — so nothing here may assume
/// a local path. In particular <see cref="Combine"/> exists rather than calling
/// <c>Path.Combine</c> at the call sites, because a remote filesystem separates with <c>/</c>
/// whatever the host operating system prefers.
///
/// The methods are synchronous and are called on a background thread by the pane runtime. That is
/// deliberate: a remote filesystem blocks, and wrapping a blocking call in <c>Task.Run</c> at one
/// known place is simpler to reason about than an async interface whose implementations mostly
/// have nothing to await.
/// </summary>
internal interface IFileBrowserFileSystem
{
    StringComparer PathComparer { get; }
    string GetFullPath(string path);
    bool DirectoryExists(string path);
    string? GetParentDirectory(string path);
    IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path);

    /// <summary>Join a directory and a child name, in this filesystem's own spelling.</summary>
    string Combine(string directory, string name);

    /// <summary>Whether a file exists at this path. Directories answer false.</summary>
    bool FileExists(string path);

    /// <summary>
    /// Whether this filesystem can be changed at all. False for a read-only mount or an FTP account
    /// without write permission, and the model hides every modifying command rather than letting
    /// the user find out by being refused.
    /// </summary>
    bool CanModify => false;

    void CreateDirectory(string path) => throw new NotSupportedException();

    /// <summary>Rename or move within this filesystem. Never overwrites; the caller picks a free name.</summary>
    void Move(string source, string destination, bool isDirectory) => throw new NotSupportedException();

    /// <summary>Copy within this filesystem. Never overwrites; the caller picks a free name.</summary>
    void Copy(string source, string destination, bool isDirectory, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>
    /// Delete. <paramref name="permanent"/> false asks for a recoverable delete and **fails** if
    /// this filesystem has none — it never silently becomes an unrecoverable one.
    /// </summary>
    void Delete(string path, bool isDirectory, bool permanent) => throw new NotSupportedException();

    /// <summary>Whether <see cref="Delete"/> can honour <c>permanent: false</c>.</summary>
    bool CanRecoverDeletes => false;
}

internal sealed class SystemFileBrowserFileSystem : IFileBrowserFileSystem
{
    public static SystemFileBrowserFileSystem Instance { get; } = new(PlatformServices.Trash);

    private readonly IFileTrash _trash;

    internal SystemFileBrowserFileSystem(IFileTrash trash) => _trash = trash;

    public StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public string GetFullPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? GetParentDirectory(string path) => Directory.GetParent(path)?.FullName;

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public bool FileExists(string path) => File.Exists(path);

    public bool CanModify => true;

    public bool CanRecoverDeletes => _trash.IsAvailable;

    public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path)
    {
        var directory = new DirectoryInfo(path);

        foreach (var child in directory.EnumerateDirectories())
        {
            yield return new FileBrowserNavigationItem(child.Name, child.FullName, IsDirectory: true);
        }

        foreach (var child in directory.EnumerateFiles())
        {
            yield return new FileBrowserNavigationItem(child.Name, child.FullName, IsDirectory: false);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Move(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            // overwrite: false — the caller has already chosen a free name, and a silent overwrite
            // is the one outcome a file manager must never produce.
            File.Move(source, destination, overwrite: false);
        }
    }

    public void Copy(string source, string destination, bool isDirectory, CancellationToken cancellationToken)
    {
        if (!isDirectory)
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }

        CopyTree(new DirectoryInfo(source), destination, cancellationToken);
    }

    private static void CopyTree(DirectoryInfo source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);

        foreach (var file in source.EnumerateFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.CopyTo(Path.Combine(destination, file.Name), overwrite: false);
        }

        foreach (var child in source.EnumerateDirectories())
        {
            CopyTree(child, Path.Combine(destination, child.Name), cancellationToken);
        }
    }

    public void Delete(string path, bool isDirectory, bool permanent)
    {
        if (!permanent)
        {
            if (!_trash.TrySend(path, isDirectory, out var error))
            {
                // Deliberately not falling through to a permanent delete. The user asked for the
                // recoverable one; doing the unrecoverable one instead is the worst possible way to
                // handle the failure.
                throw new IOException(error ?? "the file could not be moved to the Recycle Bin");
            }

            return;
        }

        if (isDirectory)
        {
            Directory.Delete(path, recursive: true);
        }
        else
        {
            File.Delete(path);
        }
    }
}
