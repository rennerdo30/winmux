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
        IFileBrowserFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDirectory);

        if (pane.Kind != PaneKind.FileBrowser || pane.Restore.Kind != PaneKind.FileBrowser)
        {
            throw new ArgumentException("A file-browser model requires a file-browser pane.", nameof(pane));
        }

        _pane = pane;
        _fileSystem = fileSystem ?? SystemFileBrowserFileSystem.Instance;
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

    public void Refresh(CancellationToken cancellationToken = default)
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
            StatusMessage = previousSelection is not null && SelectedItem is null
                ? $"The selected item '{previousSelection}' is no longer available."
                : null;
            PersistState();
            return;
        }

        var unavailable = CurrentDirectory;
        _unavailableRestoreDirectory ??= unavailable;
        LoadFallback($"Directory '{unavailable}' is unavailable. {error}");
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
