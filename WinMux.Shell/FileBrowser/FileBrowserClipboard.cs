namespace WinMux.Shell.FileBrowser;

/// <summary>One thing waiting to be pasted: a path and whether it is a folder.</summary>
internal sealed record FileBrowserClipboardEntry(string Path, bool IsDirectory);

/// <summary>
/// What is waiting to be pasted.
///
/// Shared across panes on purpose: copying in one file-browser pane and pasting in another is the
/// gesture that makes a two-pane layout worth having, and a per-pane clipboard would quietly make
/// it impossible. It is WinMux's own, not the system clipboard — dragging Explorer's clipboard
/// format in would mean serialising `CFSTR_FILEDESCRIPTOR`, and the remote filesystems this seam
/// exists for have no shell identity to put on it anyway.
///
/// It remembers which filesystem the paths belong to. A path alone is ambiguous the moment two
/// panes browse different filesystems: <c>/home/alice/notes.txt</c> copied from an SFTP pane means
/// nothing to the local disk, and pasting it there used to hand that path to the wrong filesystem.
/// Every entry is on that one filesystem, because a selection is always taken from one pane.
/// </summary>
internal sealed class FileBrowserClipboard
{
    /// <summary>The instance every pane uses unless a test supplies its own.</summary>
    public static FileBrowserClipboard Shared { get; } = new();

    public IReadOnlyList<FileBrowserClipboardEntry> Entries { get; private set; } = [];

    /// <summary>True when the sources should disappear on paste; false for a copy.</summary>
    public bool IsMove { get; private set; }

    /// <summary>The filesystem every entry is a path on.</summary>
    public IFileBrowserFileSystem? FileSystem { get; private set; }

    public bool HasContent => Entries.Count > 0;

    public void Set(IReadOnlyList<FileBrowserClipboardEntry> entries, bool isMove, IFileBrowserFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (entries.Count == 0) throw new ArgumentException("There must be something to hold.", nameof(entries));

        Entries = entries.ToArray();
        IsMove = isMove;
        FileSystem = fileSystem;
    }

    /// <summary>
    /// After a partial paste: keep only what did not arrive, so trying again does not copy the rest
    /// twice — or, for a cut, try to move files that have already gone.
    /// </summary>
    public void Keep(IReadOnlyList<FileBrowserClipboardEntry> remaining)
    {
        if (remaining.Count == 0) Clear();
        else Entries = remaining.ToArray();
    }

    public void Clear()
    {
        Entries = [];
        IsMove = false;
        FileSystem = null;
    }

    /// <summary>
    /// Drop the entries if they came from <paramref name="fileSystem"/>, which is going away. Called
    /// when a remote pane closes: its connection closes with it, and an entry pointing at a closed
    /// connection could only fail when pasted.
    /// </summary>
    public void Forget(IFileBrowserFileSystem fileSystem)
    {
        if (ReferenceEquals(FileSystem, fileSystem)) Clear();
    }
}
