namespace WinMux.Shell.FileBrowser;

/// <summary>
/// What is waiting to be pasted.
///
/// Shared across panes on purpose: copying in one file-browser pane and pasting in another is the
/// gesture that makes a two-pane layout worth having, and a per-pane clipboard would quietly make
/// it impossible. It is WinMux's own, not the system clipboard — dragging Explorer's clipboard
/// format in would mean serialising `CFSTR_FILEDESCRIPTOR`, and the remote filesystems this seam
/// exists for have no shell identity to put on it anyway.
///
/// It remembers which filesystem the path belongs to. A path alone is ambiguous the moment two
/// panes browse different filesystems: <c>/home/alice/notes.txt</c> copied from an SFTP pane means
/// nothing to the local disk, and pasting it there used to hand that path to the wrong filesystem.
///
/// It holds one entry, not a list. Multi-select is not implemented in the browser, so a list would
/// be a shape with nothing to put in it.
/// </summary>
internal sealed class FileBrowserClipboard
{
    /// <summary>The instance every pane uses unless a test supplies its own.</summary>
    public static FileBrowserClipboard Shared { get; } = new();

    public string? Path { get; private set; }
    public bool IsDirectory { get; private set; }

    /// <summary>True when the source should disappear on paste; false for a copy.</summary>
    public bool IsMove { get; private set; }

    /// <summary>The filesystem <see cref="Path"/> is a path on.</summary>
    public IFileBrowserFileSystem? FileSystem { get; private set; }

    public bool HasContent => Path is not null;

    public void Set(string path, bool isDirectory, bool isMove, IFileBrowserFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileSystem);
        Path = path;
        IsDirectory = isDirectory;
        IsMove = isMove;
        FileSystem = fileSystem;
    }

    public void Clear()
    {
        Path = null;
        IsDirectory = false;
        IsMove = false;
        FileSystem = null;
    }

    /// <summary>
    /// Drop the entry if it came from <paramref name="fileSystem"/>, which is going away. Called when
    /// a remote pane closes: its connection closes with it, and an entry pointing at a closed
    /// connection could only fail when pasted.
    /// </summary>
    public void Forget(IFileBrowserFileSystem fileSystem)
    {
        if (ReferenceEquals(FileSystem, fileSystem)) Clear();
    }
}
