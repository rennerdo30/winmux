namespace WinMux.Shell.FileBrowser;

/// <summary>
/// "This directory just changed", announced to every file-browser pane.
///
/// A pane re-reads its own directory after its own operations. What it cannot see is another pane
/// changing the directory it is showing — and the common case is not exotic: cut on the left, paste
/// on the right, and the left pane goes on listing a file that has gone. The same happens when two
/// panes show one folder. Each pane listens and refreshes when the announcement is about its
/// filesystem and its current directory.
///
/// Matched on the filesystem instance as well as the path, because <c>/data</c> on one server and
/// <c>/data</c> on another are different directories.
/// </summary>
internal static class FileBrowserChanges
{
    public static event EventHandler<FileBrowserDirectoryChanged>? DirectoryChanged;

    public static void Announce(object source, IFileBrowserFileSystem fileSystem, string? directory)
    {
        if (directory is null) return;
        DirectoryChanged?.Invoke(source, new FileBrowserDirectoryChanged(fileSystem, directory));
    }
}

internal sealed record FileBrowserDirectoryChanged(IFileBrowserFileSystem FileSystem, string Directory);
