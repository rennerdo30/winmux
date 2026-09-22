using System.Security;
using WinMux.Core.Model;

namespace WinMux.Shell.FileBrowser;

/// <summary>The detail columns a listing can be ordered by.</summary>
internal enum FileBrowserSortColumn
{
    Name,
    Modified,
    Type,
    Size,
}

/// <summary>
/// Platform-neutral navigation and persistence state for a built-in file-browser pane.
/// Presentation, commands, and provider lifecycle are intentionally outside this slice.
/// </summary>
internal sealed class FileBrowserModel
{
    internal const string CurrentDirectoryExtra = "current_directory";
    internal const string SelectedPathExtra = "selected_path";
    internal const string SortExtra = "sort";

    /// <summary>The column the listing is ordered by. Folders come first whichever it is, as in Explorer.</summary>
    public FileBrowserSortColumn SortColumn { get; private set; } = FileBrowserSortColumn.Name;

    public bool SortDescending { get; private set; }

    /// <summary>
    /// Order by <paramref name="column"/>: the same column again reverses it. A new column starts the
    /// way Explorer starts it — names A to Z, dates and sizes newest and largest first, because that
    /// is what someone clicking "Date modified" is looking for.
    /// </summary>
    public void SortBy(FileBrowserSortColumn column)
    {
        if (column == SortColumn)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = column is FileBrowserSortColumn.Modified or FileBrowserSortColumn.Size;
        }

        _entries = Sorted(_entries);
        PersistState();
    }

    private IReadOnlyList<FileBrowserNavigationItem> Sorted(IEnumerable<FileBrowserNavigationItem> entries)
    {
        var folders = entries.OrderByDescending(entry => entry.IsDirectory);
        var ordered = SortColumn switch
        {
            FileBrowserSortColumn.Modified => SortDescending
                ? folders.ThenByDescending(entry => entry.Modified)
                : folders.ThenBy(entry => entry.Modified),
            FileBrowserSortColumn.Size => SortDescending
                ? folders.ThenByDescending(entry => entry.Size)
                : folders.ThenBy(entry => entry.Size),
            // By extension: the type *name* is the platform's to give, and the extension orders the
            // same files together, which is what a type sort is for.
            FileBrowserSortColumn.Type => SortDescending
                ? folders.ThenByDescending(entry => entry.IsDirectory ? "" : System.IO.Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)
                : folders.ThenBy(entry => entry.IsDirectory ? "" : System.IO.Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase),
            _ => SortDescending
                ? folders.ThenByDescending(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                : folders.ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Ties by name, then exactly, so the order never depends on what the filesystem returned first.
        return ordered
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Path, _fileSystem.PathComparer)
            .ToArray();
    }

    private void ReadSort(string text)
    {
        var parts = text.Split(':');
        if (Enum.TryParse<FileBrowserSortColumn>(parts[0], ignoreCase: true, out var column)) SortColumn = column;
        SortDescending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
    }

    private readonly Pane _pane;
    private readonly IFileBrowserFileSystem _fileSystem;
    private readonly FileBrowserClipboard _clipboard;
    private readonly string _fallbackDirectory;
    private IReadOnlyList<FileBrowserNavigationItem> _entries = [];
    private string? _unavailableRestoreDirectory;

    public string CurrentDirectory { get; private set; } = string.Empty;
    public IReadOnlyList<FileBrowserNavigationItem> Entries => _entries;
    private IReadOnlyList<FileBrowserNavigationItem> _selection = [];

    /// <summary>Everything selected, in the order it was selected. Empty when nothing is.</summary>
    public IReadOnlyList<FileBrowserNavigationItem> SelectedItems => _selection;

    /// <summary>
    /// The first of <see cref="SelectedItems"/>: what rename acts on, what is saved with the session,
    /// and what the terminal hand-off opens.
    /// </summary>
    public FileBrowserNavigationItem? SelectedItem
    {
        get => _selection.Count > 0 ? _selection[0] : null;
        private set => _selection = value is null ? [] : [value];
    }
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

    /// <summary>
    /// Select several entries at once — the list's own multiple selection. Paths not in the current
    /// listing are ignored rather than reported: the list only offers what the model gave it, and a
    /// row that vanished mid-click has already been dealt with by the refresh that removed it.
    /// </summary>
    public void SelectMany(IEnumerable<string> paths)
    {
        SetSelection(paths
            .Select(path => _entries.FirstOrDefault(entry => _fileSystem.PathComparer.Equals(entry.Path, path)))
            .OfType<FileBrowserNavigationItem>()
            .Distinct()
            .ToArray());
        StatusMessage = null;
        PersistState();
    }

    private void SetSelection(IReadOnlyList<FileBrowserNavigationItem> items) => _selection = items;

    /// <param name="reportLostSelection">
    /// False when the refresh follows a change another pane made — a move out of this directory is
    /// expected to take the selection with it, and warning about it reads as if something went wrong.
    /// </param>
    public void Refresh(CancellationToken cancellationToken = default, bool reportLostSelection = true)
    {
        if (TryReadDirectory(CurrentDirectory, cancellationToken, out var normalized, out var entries, out var error))
        {
            var previousSelection = SelectedPath;
            var previous = _selection;
            CurrentDirectory = normalized;
            _entries = entries;
            SetSelection(previous
                .Select(old => entries.FirstOrDefault(entry => _fileSystem.PathComparer.Equals(entry.Path, old.Path)))
                .OfType<FileBrowserNavigationItem>()
                .ToArray());
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
        var items = _selection;
        if (items.Count == 0)
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

        var what = Describe(items);
        var deleted = 0;
        return Perform(
            () =>
            {
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        _fileSystem.Delete(item.Path, item.IsDirectory, permanent);
                        deleted++;
                    }
                    catch (Exception ex) when (IsFileSystemFailure(ex) && deleted > 0)
                    {
                        // Some went. Say which did not, rather than reporting the batch as failed
                        // when most of it happened.
                        throw new IOException(
                            $"{deleted} of {items.Count} were deleted; '{item.Name}' was not: {ex.Message}", ex);
                    }
                }

                return null;
            },
            cancellationToken,
            _ => permanent ? $"Deleted {what} permanently." : $"Moved {what} to the Recycle Bin.",
            $"Cannot delete {what}.",
            refreshOnFailure: true);
    }

    /// <summary>Put the selection on the clipboard. <paramref name="isMove"/> true is a cut.</summary>
    public bool HoldSelected(bool isMove)
    {
        var items = _selection;
        if (items.Count == 0)
        {
            StatusMessage = "Select something first.";
            return false;
        }

        if (isMove && !Guard(out var refusal))
        {
            StatusMessage = refusal;
            return false;
        }

        _clipboard.Set(EntriesOf(items), isMove, _fileSystem);
        StatusMessage = $"{(isMove ? "Cut" : "Copied")} {Describe(items)}.";
        return true;
    }

    /// <summary>The selection as transferable entries, for the clipboard or a drag.</summary>
    public IReadOnlyList<FileBrowserClipboardEntry> SelectedEntries => EntriesOf(_selection);

    /// <summary>The filesystem this pane browses, so a drag can say where its paths live.</summary>
    internal IFileBrowserFileSystem FileSystem => _fileSystem;

    public bool Paste(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (!_clipboard.HasContent)
        {
            StatusMessage = "There is nothing to paste.";
            return false;
        }

        var entries = _clipboard.Entries;
        var isMove = _clipboard.IsMove;
        var from = _clipboard.FileSystem ?? _fileSystem;

        var outcome = TransferCore(from, entries, CurrentDirectory, isMove, cancellationToken, progress);

        // A cut is spent by what arrived. Keeping the rest means trying again finishes the job
        // rather than repeating the part that worked; a copy stays on the clipboard to be pasted
        // again, as everywhere else.
        if (isMove && outcome.Attempted) _clipboard.Keep(outcome.Remaining);
        return outcome.Succeeded;
    }

    /// <summary>
    /// Copy or move <paramref name="entries"/> from <paramref name="from"/> into
    /// <paramref name="targetDirectory"/> here — a drop. Null means the directory being shown.
    /// </summary>
    public bool Transfer(
        IFileBrowserFileSystem from,
        IReadOnlyList<FileBrowserClipboardEntry> entries,
        string? targetDirectory,
        bool isMove,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null) =>
        TransferCore(from, entries, targetDirectory ?? CurrentDirectory, isMove, cancellationToken, progress).Succeeded;

    private readonly record struct TransferOutcome(
        bool Succeeded, bool Attempted, IReadOnlyList<FileBrowserClipboardEntry> Remaining);

    /// <summary>
    /// The one implementation behind paste and drop.
    ///
    /// Within one filesystem the filesystem moves or copies for itself. Across two, the bytes are
    /// streamed through <see cref="FileBrowserTransfer"/>, and a move is a complete copy followed by
    /// removing the original — only once the copy finished without error, so a move interrupted
    /// halfway leaves the source whole.
    ///
    /// Each entry stands alone: one that fails is reported and the rest still go, the way Explorer
    /// carries on past a file it cannot copy. Cancelling stops the batch.
    /// </summary>
    private TransferOutcome TransferCore(
        IFileBrowserFileSystem from,
        IReadOnlyList<FileBrowserClipboardEntry> entries,
        string targetDirectory,
        bool isMove,
        CancellationToken cancellationToken,
        IProgress<string>? progress)
    {
        if (!Guard(out var refusal))
        {
            StatusMessage = refusal;
            return new(false, false, entries);
        }

        var across = !ReferenceEquals(from, _fileSystem);
        var produced = new List<string>();
        var remaining = new List<FileBrowserClipboardEntry>();
        var problems = new List<string>();
        var notes = new List<string>();
        var movedFrom = new HashSet<string>(from.PathComparer);
        var alreadyHere = 0;
        var files = 0;
        var cancelled = false;

        foreach (var entry in entries)
        {
            var name = NameOf(entry.Path);

            if (cancelled)
            {
                remaining.Add(entry);
                continue;
            }

            if (across && !ValidName(name, out var invalid))
            {
                // "a:b" is a fine name on a Unix server and not on Windows. Said before any bytes
                // move rather than left to the OS.
                problems.Add($"Cannot paste '{name}' here. {invalid}");
                remaining.Add(entry);
                continue;
            }

            if (!across && entry.IsDirectory && IsSelfOrDescendant(entry.Path, targetDirectory))
            {
                // Moving a directory inside itself destroys it, and the OS error for it is unhelpful.
                problems.Add($"Cannot paste '{name}' into itself.");
                remaining.Add(entry);
                continue;
            }

            if (!across && isMove &&
                _fileSystem.PathComparer.Equals(_fileSystem.GetParentDirectory(entry.Path) ?? "", targetDirectory))
            {
                alreadyHere++;
                continue;
            }

            try
            {
                var target = Unique(targetDirectory, name, entry.IsDirectory);
                if (!across)
                {
                    if (isMove) _fileSystem.Move(entry.Path, target, entry.IsDirectory);
                    else _fileSystem.Copy(entry.Path, target, entry.IsDirectory, cancellationToken);
                    files += entry.IsDirectory ? 0 : 1;
                }
                else
                {
                    files += FileBrowserTransfer.Copy(
                        from, entry.Path, _fileSystem, target, entry.IsDirectory, cancellationToken, progress);

                    if (isMove && RemoveMovedOriginal(from, entry.Path, entry.IsDirectory) is { } note)
                    {
                        // The copy succeeded and is what matters most; the user still needs to know
                        // the original is where it was, or they will believe it gone.
                        notes.Add(note);
                    }
                }

                produced.Add(target);
                if (isMove && from.GetParentDirectory(entry.Path) is { } parent) movedFrom.Add(parent);
                if (entries.Count > 1)
                {
                    progress?.Report($"{(isMove ? "Moved" : "Copied")} {produced.Count} of {entries.Count}…");
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                remaining.Add(entry);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex) || ex is ObjectDisposedException)
            {
                problems.Add($"Cannot paste '{name}'. {ex.Message}");
                remaining.Add(entry);
            }
        }

        // Always re-read: even a batch that failed partway changed the directory.
        Refresh(CancellationToken.None, reportLostSelection: false);
        var intoView = _fileSystem.PathComparer.Equals(targetDirectory, CurrentDirectory);
        if (produced.Count > 0 && intoView)
        {
            SetSelection(produced
                .Select(path => _entries.FirstOrDefault(e => _fileSystem.PathComparer.Equals(e.Path, path)))
                .OfType<FileBrowserNavigationItem>()
                .ToArray());
        }

        PersistState();

        var verb = isMove ? "Moved" : "Copied";
        var where = intoView ? "here" : $"into '{NameOf(targetDirectory)}'";
        var parts = new List<string>();
        if (produced.Count == 1 && entries.Count == 1)
        {
            parts.Add($"{verb} '{NameOf(produced[0])}' {where}" +
                      (across && entries[0].IsDirectory ? $" ({files} file(s))." : "."));
        }
        else if (produced.Count > 0)
        {
            // "2 of 3" only when something is missing; a complete batch just says how many. The file
            // count is worth adding only when folders made it differ from the item count.
            var count = produced.Count == entries.Count
                ? $"{produced.Count} items"
                : $"{produced.Count} of {entries.Count} items";
            parts.Add($"{verb} {count} {where}" +
                      (across && files != produced.Count ? $" ({files} file(s))." : "."));
        }

        if (alreadyHere > 0)
        {
            parts.Add(entries.Count == 1
                ? $"'{NameOf(entries[0].Path)}' is already here."
                : $"{alreadyHere} already here.");
        }

        if (cancelled) parts.Add("The rest was not pasted: it was cancelled.");
        parts.AddRange(problems);
        parts.AddRange(notes);
        StatusMessage = string.Join(" ", parts);

        if (produced.Count > 0)
        {
            FileBrowserChanges.Announce(this, _fileSystem, targetDirectory);
            foreach (var parent in movedFrom) FileBrowserChanges.Announce(this, from, parent);
        }

        return new(!cancelled && problems.Count == 0, true, remaining);
    }

    /// <summary>
    /// The second half of a move between filesystems. Returns a sentence for the status line when
    /// the original could not be removed, and null when it was.
    /// </summary>
    private string? RemoveMovedOriginal(IFileBrowserFileSystem from, string source, bool isDirectory)
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
            return $"The original '{NameOf(source)}' could not be removed, so it is still there: {ex.Message}";
        }
    }

    private static FileBrowserClipboardEntry[] EntriesOf(IReadOnlyList<FileBrowserNavigationItem> items) =>
        items.Select(item => new FileBrowserClipboardEntry(item.Path, item.IsDirectory)).ToArray();

    private static string Describe(IReadOnlyList<FileBrowserNavigationItem> items) =>
        items.Count == 1 ? $"'{items[0].Name}'" : $"{items.Count} items";

    /// <summary>
    /// Run a modifying operation, then reload the directory so the result is visible, selecting
    /// whatever the operation produced. Failures leave the view untouched and say why.
    /// </summary>
    private bool Perform(
        Func<string?> operation,
        CancellationToken cancellationToken,
        Func<string?, string> success,
        string failurePrefix,
        bool refreshOnFailure = false)
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
            // A batch that failed partway has still changed the directory.
            if (refreshOnFailure) Refresh(CancellationToken.None, reportLostSelection: false);
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
        if (extras.TryGetValue(SortExtra, out var sort)) ReadSort(sort);

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
                .ToArray();
            entries = Sorted(entries);
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
            [SortExtra] = $"{SortColumn.ToString().ToLowerInvariant()}:{(SortDescending ? "desc" : "asc")}",
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
