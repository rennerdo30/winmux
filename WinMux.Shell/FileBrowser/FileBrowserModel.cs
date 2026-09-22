using System.Security;
using WinMux.Core.Model;

namespace WinMux.Shell.FileBrowser;

/// <summary>
/// Platform-neutral navigation and persistence state for a built-in file-browser pane.
/// Presentation, commands, and provider lifecycle are intentionally outside this slice.
/// </summary>
internal sealed class FileBrowserModel
{
    internal const string CurrentDirectoryExtra = "current_directory";
    internal const string SelectedPathExtra = "selected_path";

    private readonly Pane _pane;
    private readonly IFileBrowserFileSystem _fileSystem;
    private readonly FileBrowserClipboard _clipboard;
    private readonly string _fallbackDirectory;
    private IReadOnlyList<FileBrowserNavigationItem> _entries = [];
    private string? _unavailableRestoreDirectory;

    public string CurrentDirectory { get; private set; } = string.Empty;
    public IReadOnlyList<FileBrowserNavigationItem> Entries => _entries;
    public FileBrowserNavigationItem? SelectedItem { get; private set; }
    public string? SelectedPath => SelectedItem?.Path;
    public string? StatusMessage { get; private set; }

    /// <summary>A selected directory is handed off directly; files hand off their containing view.</summary>
    public string TerminalHandoffDirectory => SelectedItem?.IsDirectory == true
        ? SelectedItem.Path
        : CurrentDirectory;

    public FileBrowserModel(
        Pane pane,
        string fallbackDirectory,
        IFileBrowserFileSystem? fileSystem = null,
        FileBrowserClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);

        if (pane.Kind != PaneKind.FileBrowser || pane.Restore.Kind != PaneKind.FileBrowser)
        {
            throw new ArgumentException("A file-browser model requires a file-browser pane.", nameof(pane));
        }

        _pane = pane;
        _fileSystem = fileSystem ?? SystemFileBrowserFileSystem.Instance;
        _clipboard = clipboard ?? FileBrowserClipboard.Shared;
        _fallbackDirectory = NormalizeForDisplay(fallbackDirectory);

        Restore();
    }

    public bool NavigateTo(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!TryReadDirectory(directory, cancellationToken, out var normalized, out var entries, out var error))
        {
            StatusMessage = $"Cannot open directory '{directory}'. Staying in '{CurrentDirectory}'. {error}";
            return false;
        }

        CurrentDirectory = normalized;
        _entries = entries;
        SelectedItem = null;
        _unavailableRestoreDirectory = null;
        StatusMessage = null;
        PersistState();
        return true;
    }

    public bool NavigateParent(CancellationToken cancellationToken = default)
    {
        string? parent;
        try
        {
            parent = _fileSystem.GetParentDirectory(CurrentDirectory);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            StatusMessage = $"Cannot find the parent of '{CurrentDirectory}'. {ex.Message}";
            return false;
        }

        if (parent is null)
        {
            StatusMessage = $"'{CurrentDirectory}' is already a filesystem root.";
            return false;
        }

        return NavigateTo(parent, cancellationToken);
    }

    public bool Select(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SelectedItem = null;
            StatusMessage = null;
            PersistState();
            return true;
        }

        string normalized;
        try
        {
            normalized = _fileSystem.GetFullPath(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            SelectedItem = null;
            StatusMessage = $"Cannot select '{path}'. {ex.Message}";
            PersistState();
            return false;
        }

        var item = _entries.FirstOrDefault(entry =>
            _fileSystem.PathComparer.Equals(entry.Path, normalized));

        if (item is null)
        {
            SelectedItem = null;
            StatusMessage = $"The selected item '{path}' is unavailable in '{CurrentDirectory}'.";
            PersistState();
            return false;
        }

        SelectedItem = item;
        StatusMessage = null;
        PersistState();
        return true;
    }

    /// <param name="reportLostSelection">
    /// False when the refresh follows a change another pane made — a move out of this directory is
    /// expected to take the selection with it, and warning about it reads as if something went wrong.
    /// </param>
    public void Refresh(CancellationToken cancellationToken = default, bool reportLostSelection = true)
    {
        if (TryReadDirectory(CurrentDirectory, cancellationToken, out var normalized, out var entries, out var error))
        {
            var previousSelection = SelectedPath;
            CurrentDirectory = normalized;
            _entries = entries;
            SelectedItem = previousSelection is null
                ? null
                : entries.FirstOrDefault(entry =>
                    _fileSystem.PathComparer.Equals(entry.Path, previousSelection));
            StatusMessage = reportLostSelection && previousSelection is not null && SelectedItem is null
                ? $"The selected item '{previousSelection}' is no longer available."
                : null;
            PersistState();
            return;
        }

        var unavailable = CurrentDirectory;
        _unavailableRestoreDirectory ??= unavailable;
        LoadFallback($"Directory '{unavailable}' is unavailable. {error}");
    }

    /// <summary>Whether this pane is showing <paramref name="directory"/> on <paramref name="fileSystem"/>.</summary>
    public bool IsShowing(IFileBrowserFileSystem fileSystem, string directory)
    {
        if (!ReferenceEquals(fileSystem, _fileSystem)) return false;
        try
        {
            return _fileSystem.PathComparer.Equals(_fileSystem.GetFullPath(directory), CurrentDirectory);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return false;
        }
    }

    /// <summary>Whether this filesystem can be changed at all; false hides every modifying command.</summary>
    public bool CanModify => _fileSystem.CanModify;

    /// <summary>Whether a delete can be undone here. False means Delete must warn, not pretend.</summary>
    public bool CanRecoverDeletes => _fileSystem.CanRecoverDeletes;

    /// <summary>True when there is something to paste into this directory.</summary>
    public bool CanPaste => CanModify && _clipboard.HasContent;

    public bool CreateDirectory(string name, CancellationToken cancellationToken = default)
    {
        if (!Guard(out var refusal) || !ValidName(name, out refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        return Perform(
            () =>
            {
                var target = Unique(CurrentDirectory, name.Trim(), isDirectory: true);
                _fileSystem.CreateDirectory(target);
                return target;
            },
            cancellationToken,
            created => $"Created '{NameOf(created)}'.",
            "Cannot create the folder.");
    }

    public bool RenameSelected(string newName, CancellationToken cancellationToken = default)
    {
        if (SelectedItem is not { } item)
        {
            StatusMessage = "Select something to rename first.";
            return false;
        }

        if (!Guard(out var refusal) || !ValidName(newName, out refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        var trimmed = newName.Trim();
        if (_fileSystem.PathComparer.Equals(item.Name, trimmed))
        {
            StatusMessage = null;
            return true;
        }

        return Perform(
            () =>
            {
                var target = Unique(CurrentDirectory, trimmed, item.IsDirectory);
                _fileSystem.Move(item.Path, target, item.IsDirectory);
                return target;
            },
            cancellationToken,
            renamed => $"Renamed to '{NameOf(renamed)}'.",
            $"Cannot rename '{item.Name}'.");
    }

    public bool DeleteSelected(bool permanent, CancellationToken cancellationToken = default)
    {
        if (SelectedItem is not { } item)
        {
            StatusMessage = "Select something to delete first.";
            return false;
        }

        if (!Guard(out var refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        if (!permanent && !CanRecoverDeletes)
        {
            // Never quietly upgrade a recoverable delete into an unrecoverable one.
            StatusMessage = "This location has no Recycle Bin. Use Shift+Delete to delete permanently.";
            return false;
        }

        return Perform(
            () =>
            {
                _fileSystem.Delete(item.Path, item.IsDirectory, permanent);
                return null;
            },
            cancellationToken,
            _ => permanent ? $"Deleted '{item.Name}' permanently." : $"Moved '{item.Name}' to the Recycle Bin.",
            $"Cannot delete '{item.Name}'.");
    }

    /// <summary>Put the selection on the clipboard. <paramref name="isMove"/> true is a cut.</summary>
    public bool HoldSelected(bool isMove)
    {
        if (SelectedItem is not { } item)
        {
            StatusMessage = "Select something first.";
            return false;
        }

        if (isMove && !Guard(out var refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        _clipboard.Set(item.Path, item.IsDirectory, isMove, _fileSystem);
        StatusMessage = $"{(isMove ? "Cut" : "Copied")} '{item.Name}'.";
        return true;
    }

    public bool Paste(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (!_clipboard.HasContent)
        {
            StatusMessage = "There is nothing to paste.";
            return false;
        }

        if (!Guard(out var refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        var source = _clipboard.Path!;
        var isDirectory = _clipboard.IsDirectory;
        var isMove = _clipboard.IsMove;
        var from = _clipboard.FileSystem ?? _fileSystem;
        var name = NameOf(source);

        if (!ReferenceEquals(from, _fileSystem))
        {
            return PasteAcross(from, source, name, isDirectory, isMove, cancellationToken, progress);
        }

        // Moving a directory inside itself destroys it, and the OS error for it is unhelpful.
        // Checked here so the message names the actual problem.
        if (isDirectory && IsSelfOrDescendant(source, CurrentDirectory))
        {
            StatusMessage = $"Cannot paste '{name}' into itself.";
            return false;
        }

        if (isMove && _fileSystem.PathComparer.Equals(_fileSystem.GetParentDirectory(source) ?? "", CurrentDirectory))
        {
            _clipboard.Clear();
            StatusMessage = $"'{name}' is already here.";
            return true;
        }

        return Perform(
            () =>
            {
                var target = Unique(CurrentDirectory, name, isDirectory);
                if (isMove)
                {
                    _fileSystem.Move(source, target, isDirectory);
                }
                else
                {
                    _fileSystem.Copy(source, target, isDirectory, cancellationToken);
                }

                if (isMove)
                {
                    _clipboard.Clear();
                    FileBrowserChanges.Announce(this, _fileSystem, _fileSystem.GetParentDirectory(source));
                }

                return target;
            },
            cancellationToken,
            pasted => $"{(isMove ? "Moved" : "Copied")} '{NameOf(pasted)}' here.",
            $"Cannot paste '{name}'.");
    }

    /// <summary>
    /// Paste something from a different filesystem — an SFTP file into a local folder, say.
    ///
    /// The bytes are streamed through <see cref="FileBrowserTransfer"/>. A move is a complete copy
    /// followed by removing the original, and the original is only touched once the copy has
    /// finished without error: a move interrupted halfway must leave the source whole, even if that
    /// means a partial copy at the destination.
    /// </summary>
    private bool PasteAcross(
        IFileBrowserFileSystem from,
        string source,
        string name,
        bool isDirectory,
        bool isMove,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        // The source's own name may not be a legal name here: "a:b" is a fine file on a Unix server
        // and not on Windows. Say so before any bytes move rather than letting the OS explain.
        if (!ValidName(name, out var refusal))
        {
            StatusMessage = $"Cannot paste '{name}' here. {refusal}";
            return false;
        }

        var files = 0;
        string? cleanup = null;
        var pasted = Perform(
            () =>
            {
                var target = Unique(CurrentDirectory, name, isDirectory);
                files = FileBrowserTransfer.Copy(from, source, _fileSystem, target, isDirectory, cancellationToken, progress);

                if (isMove)
                {
                    _clipboard.Clear();
                    cleanup = RemoveMovedOriginal(from, source, isDirectory);
                    if (cleanup is null) FileBrowserChanges.Announce(this, from, from.GetParentDirectory(source));
                }

                return target;
            },
            cancellationToken,
            target => (isMove ? $"Moved '{NameOf(target)}' here" : $"Copied '{NameOf(target)}' here") +
                      (isDirectory ? $" ({files} file(s))." : "."),
            $"Cannot paste '{name}'.");

        if (pasted && cleanup is not null)
        {
            // The copy succeeded and is what matters most; the user still needs to know the original
            // is where it was, or they will believe it gone.
            StatusMessage = $"{StatusMessage} {cleanup}";
        }

        return pasted;
    }

    /// <summary>
    /// The second half of a move between filesystems. Returns a sentence for the status line when
    /// the original could not be removed, and null when it was.
    /// </summary>
    private static string? RemoveMovedOriginal(IFileBrowserFileSystem from, string source, bool isDirectory)
    {
        try
        {
            // To the Recycle Bin where there is one: the copy is verified only in the sense that no
            // call failed, and a recoverable original costs nothing. Where there is none, a move has
            // always meant the original goes, and the complete copy now exists.
            from.Delete(source, isDirectory, permanent: !from.CanRecoverDeletes);
            return null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex) || ex is ObjectDisposedException)
        {
            return $"The original could not be removed, so it is still there: {ex.Message}";
        }
    }

    /// <summary>
    /// Run a modifying operation, then reload the directory so the result is visible, selecting
    /// whatever the operation produced. Failures leave the view untouched and say why.
    /// </summary>
    private bool Perform(
        Func<string?> operation,
        CancellationToken cancellationToken,
        Func<string?, string> success,
        string failurePrefix)
    {
        string? produced;
        try
        {
            produced = operation();
        }
        catch (OperationCanceledException)
        {
            // Refresh first: it resets the status line, and this message is the one that must stay.
            Refresh(CancellationToken.None);
            StatusMessage = $"{failurePrefix} It was cancelled.";
            return false;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            StatusMessage = $"{failurePrefix} {ex.Message}";
            return false;
        }

        Refresh(cancellationToken);
        if (produced is not null)
        {
            SelectQuietly(produced);
        }

        StatusMessage = success(produced);
        PersistState();

        // Any other pane showing this directory is now out of date.
        FileBrowserChanges.Announce(this, _fileSystem, CurrentDirectory);
        return true;
    }

    /// <summary>Select a path if it is present, without turning its absence into a complaint.</summary>
    private void SelectQuietly(string path)
    {
        SelectedItem = _entries.FirstOrDefault(entry => _fileSystem.PathComparer.Equals(entry.Path, path));
    }

    private bool Guard(out string? refusal)
    {
        refusal = CanModify ? null : "This location is read-only.";
        return refusal is null;
    }

    /// <summary>
    /// A name must be a single component. Rejecting separators is not fussiness: a name of
    /// <c>..\\elsewhere</c> would place the result outside the directory the user is looking at.
    /// </summary>
    private static bool ValidName(string? name, out string? refusal)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            refusal = "A name cannot be empty.";
            return false;
        }

        if (trimmed is "." or "..")
        {
            refusal = $"'{trimmed}' is not a usable name.";
            return false;
        }

        if (trimmed.AsSpan().IndexOfAny('/', '\\', ':') >= 0)
        {
            refusal = "A name cannot contain a path separator.";
            return false;
        }

        if (trimmed.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            refusal = "That name contains characters a file cannot have.";
            return false;
        }

        refusal = null;
        return true;
    }

    /// <summary>
    /// A destination that does not exist yet: "report.txt" becomes "report (2).txt".
    ///
    /// This is what keeps every operation non-destructive. Overwriting on a name collision is the
    /// single most expensive mistake a file manager can make, and asking the user is a dialog that
    /// still ends in someone clicking through it.
    /// </summary>
    private string Unique(string directory, string name, bool isDirectory)
    {
        var candidate = _fileSystem.Combine(directory, name);
        if (!Exists(candidate)) return candidate;

        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);

        for (var index = 2; index < int.MaxValue; index++)
        {
            candidate = _fileSystem.Combine(directory, $"{stem} ({index}){extension}");
            if (!Exists(candidate)) return candidate;
        }

        throw new IOException("no free name is available in this directory");
    }

    private bool Exists(string path) => _fileSystem.DirectoryExists(path) || _fileSystem.FileExists(path);

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> or lives inside it.</summary>
    private bool IsSelfOrDescendant(string root, string candidate)
    {
        for (var walk = candidate; walk is not null; walk = _fileSystem.GetParentDirectory(walk))
        {
            if (_fileSystem.PathComparer.Equals(walk, root)) return true;
        }

        return false;
    }

    private string NameOf(string? path) =>
        path is null ? string.Empty : path[(LastSeparator(path) + 1)..];

    private static int LastSeparator(string path)
    {
        for (var index = path.Length - 1; index >= 0; index--)
        {
            if (path[index] is '/' or '\\') return index;
        }

        return -1;
    }

    private void Restore()
    {
        var extras = _pane.Restore.Extras;
        var requestedDirectory = extras.TryGetValue(CurrentDirectoryExtra, out var savedDirectory) &&
                                 !string.IsNullOrWhiteSpace(savedDirectory)
            ? savedDirectory
            : _fallbackDirectory;

        if (TryReadDirectory(requestedDirectory, CancellationToken.None, out var normalized, out var entries, out var error))
        {
            CurrentDirectory = normalized;
            _entries = entries;
        }
        else
        {
            _unavailableRestoreDirectory = requestedDirectory;
            LoadFallback($"Directory '{requestedDirectory}' is unavailable. {error}");
        }

        var restoreWarning = StatusMessage;
        if (extras.TryGetValue(SelectedPathExtra, out var savedSelection) &&
            !string.IsNullOrWhiteSpace(savedSelection) &&
            !SelectRestoredPath(savedSelection))
        {
            StatusMessage = JoinWarnings(
                restoreWarning,
                $"The restored selection '{savedSelection}' is unavailable in '{CurrentDirectory}'.");
        }

        PersistState();
    }

    private bool SelectRestoredPath(string path)
    {
        try
        {
            var normalized = _fileSystem.GetFullPath(path);
            SelectedItem = _entries.FirstOrDefault(entry =>
                _fileSystem.PathComparer.Equals(entry.Path, normalized));
            return SelectedItem is not null;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            SelectedItem = null;
            return false;
        }
    }

    private void LoadFallback(string warning)
    {
        if (TryReadDirectory(_fallbackDirectory, CancellationToken.None, out var normalized, out var entries, out var fallbackError))
        {
            CurrentDirectory = normalized;
            _entries = entries;
            SelectedItem = null;
            StatusMessage = $"{warning} Using fallback directory '{normalized}'.";
            PersistState();
            return;
        }

        CurrentDirectory = _fallbackDirectory;
        _entries = [];
        SelectedItem = null;
        StatusMessage = $"{warning} Fallback directory '{_fallbackDirectory}' is also unavailable. {fallbackError}";
        PersistState();
    }

    private bool TryReadDirectory(
        string directory,
        CancellationToken cancellationToken,
        out string normalized,
        out IReadOnlyList<FileBrowserNavigationItem> entries,
        out string error)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            normalized = _fileSystem.GetFullPath(directory);
            if (!_fileSystem.DirectoryExists(normalized))
            {
                entries = [];
                error = "The directory does not exist.";
                return false;
            }

            entries = _fileSystem.EnumerateEntries(normalized)
                .Select(entry =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return entry;
                })
                .OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.Path, _fileSystem.PathComparer)
                .ToArray();
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            normalized = NormalizeForDisplay(directory);
            entries = [];
            error = ex.Message;
            return false;
        }
    }

    private void PersistState()
    {
        var extras = new Dictionary<string, string>(_pane.Restore.Extras, StringComparer.Ordinal)
        {
            // A failed restore is not user intent to replace the saved location. Keep that path
            // until the user deliberately navigates somewhere else, while the UI remains usable
            // in the visible fallback directory.
            [CurrentDirectoryExtra] = _unavailableRestoreDirectory ?? CurrentDirectory,
            [SelectedPathExtra] = SelectedPath ?? string.Empty,
        };

        _pane.Restore = _pane.Restore with { Extras = extras };
    }

    private string NormalizeForDisplay(string path)
    {
        try
        {
            return _fileSystem.GetFullPath(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return path;
        }
    }

    private static string JoinWarnings(string? first, string second) =>
        string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";

    private static bool IsFileSystemFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        ArgumentException or
        NotSupportedException;
}
