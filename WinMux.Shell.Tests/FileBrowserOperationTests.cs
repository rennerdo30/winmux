using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.FileBrowser;

namespace WinMux.Shell.Tests;

/// <summary>
/// Creating, renaming, copying, moving and deleting.
///
/// These run against real files in a temporary directory, because the whole point of the feature is
/// what happens on disk and a fake filesystem would be testing the fake. The one thing that is
/// faked is the Recycle Bin: a test must not put anything in the real one, and
/// <see cref="IFileTrash"/> exists precisely so that it does not have to.
///
/// The rules being pinned here are the destructive ones. A file manager that overwrites on a name
/// collision, or that lets a folder be pasted into itself, destroys data — so those have tests
/// before the happy paths do.
/// </summary>
public sealed class FileBrowserOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "winmux-file-ops-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingTrash _trash = new();

    public FileBrowserOperationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- the rules that protect data -------------------------------------------------

    [Fact]
    public void Pasting_a_file_into_its_own_directory_does_not_overwrite_it()
    {
        // The collision case. Explorer produces "notes (2).txt"; silently replacing the original
        // would be the single most expensive bug this pane could have.
        var file = WriteFile("notes.txt", "original");
        var model = CreateModel();

        Assert.True(model.Select(file));
        Assert.True(model.HoldSelected(isMove: false));
        Assert.True(model.Paste());

        Assert.Equal("original", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(_root, "notes (2).txt")));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "notes (2).txt")));
    }

    [Fact]
    public void Repeated_pastes_keep_finding_free_names()
    {
        var file = WriteFile("notes.txt", "original");
        var model = CreateModel();
        Assert.True(model.Select(file));
        Assert.True(model.HoldSelected(isMove: false));

        Assert.True(model.Paste());
        Assert.True(model.Paste());

        Assert.True(File.Exists(Path.Combine(_root, "notes (2).txt")));
        Assert.True(File.Exists(Path.Combine(_root, "notes (3).txt")));
    }

    [Fact]
    public void A_folder_cannot_be_pasted_into_itself()
    {
        // Moving a directory inside its own subtree destroys it, and the OS error for it does not
        // say so. Refused here, by name.
        var outer = Directory.CreateDirectory(Path.Combine(_root, "outer")).FullName;
        var inner = Directory.CreateDirectory(Path.Combine(outer, "inner")).FullName;

        var model = CreateModel();
        Assert.True(model.Select(outer));
        Assert.True(model.HoldSelected(isMove: true));
        Assert.True(model.NavigateTo(inner));

        Assert.False(model.Paste());
        Assert.Contains("into itself", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outer));
        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public void A_name_with_a_path_separator_is_refused()
    {
        // "..\\elsewhere" would put the result outside the directory being looked at.
        var model = CreateModel();

        Assert.False(model.CreateDirectory(@"..\escaped"));
        Assert.Contains("separator", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "escaped")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void A_name_that_is_not_a_name_is_refused(string name)
    {
        var model = CreateModel();

        Assert.False(model.CreateDirectory(name));
        Assert.NotNull(model.StatusMessage);
    }

    [Fact]
    public void Renaming_onto_an_existing_name_does_not_clobber_it()
    {
        var keep = WriteFile("keep.txt", "keep me");
        var other = WriteFile("other.txt", "other");
        var model = CreateModel();

        Assert.True(model.Select(other));
        Assert.True(model.RenameSelected("keep.txt"));

        Assert.Equal("keep me", File.ReadAllText(keep));
        Assert.Equal("other", File.ReadAllText(Path.Combine(_root, "keep (2).txt")));
    }

    // ---- deleting --------------------------------------------------------------------

    [Fact]
    public void An_ordinary_delete_goes_to_the_recycle_bin_not_the_filesystem()
    {
        var file = WriteFile("bin-me.txt", "x");
        var model = CreateModel();
        Assert.True(model.Select(file));

        Assert.True(model.DeleteSelected(permanent: false));

        Assert.Equal([(file, false)], _trash.Sent);
        Assert.Contains("Recycle Bin", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_permanent_delete_really_removes_it_and_never_touches_the_bin()
    {
        var file = WriteFile("gone.txt", "x");
        var model = CreateModel();
        Assert.True(model.Select(file));

        Assert.True(model.DeleteSelected(permanent: true));

        Assert.False(File.Exists(file));
        Assert.Empty(_trash.Sent);
    }

    [Fact]
    public void A_failed_recycle_is_never_quietly_upgraded_to_a_permanent_delete()
    {
        // The worst available failure mode: the user asked for the delete they could undo, so a
        // broken Recycle Bin must leave the file alone rather than destroy it.
        _trash.FailWith = "the Recycle Bin is full";
        var file = WriteFile("survivor.txt", "x");
        var model = CreateModel();
        Assert.True(model.Select(file));

        Assert.False(model.DeleteSelected(permanent: false));

        Assert.True(File.Exists(file));
        Assert.Contains("Recycle Bin is full", model.StatusMessage!);
    }

    [Fact]
    public void Without_a_recycle_bin_delete_refuses_and_points_at_the_permanent_one()
    {
        _trash.Available = false;
        var file = WriteFile("x.txt", "x");
        var model = CreateModel();
        Assert.True(model.Select(file));

        Assert.False(model.DeleteSelected(permanent: false));

        Assert.True(File.Exists(file));
        Assert.Contains("Shift+Delete", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deleting_a_directory_says_it_is_a_directory()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        var model = CreateModel();
        Assert.True(model.Select(directory));

        Assert.True(model.DeleteSelected(permanent: false));

        Assert.Equal([(directory, true)], _trash.Sent);
    }

    // ---- the ordinary paths ----------------------------------------------------------

    [Fact]
    public void A_new_folder_appears_and_is_selected()
    {
        var model = CreateModel();

        Assert.True(model.CreateDirectory("Reports"));

        Assert.True(Directory.Exists(Path.Combine(_root, "Reports")));
        Assert.Equal("Reports", model.SelectedItem?.Name);
    }

    [Fact]
    public void Cutting_and_pasting_moves_a_file_between_directories()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        var file = WriteFile("moving.txt", "payload");
        var model = CreateModel();

        Assert.True(model.Select(file));
        Assert.True(model.HoldSelected(isMove: true));
        Assert.True(model.NavigateTo(destination));
        Assert.True(model.Paste());

        Assert.False(File.Exists(file));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(destination, "moving.txt")));
    }

    [Fact]
    public void Copying_a_folder_copies_what_is_inside_it()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "tree")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "deep.txt"), "deep");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;

        var model = CreateModel();
        Assert.True(model.Select(source));
        Assert.True(model.HoldSelected(isMove: false));
        Assert.True(model.NavigateTo(destination));
        Assert.True(model.Paste());

        Assert.Equal("deep", File.ReadAllText(Path.Combine(destination, "tree", "nested", "deep.txt")));
        Assert.True(Directory.Exists(source), "a copy must leave the original alone");
    }

    [Fact]
    public void A_cut_is_spent_once_and_a_copy_is_not()
    {
        // Pasting a cut twice would otherwise fail confusingly on the second go: the source is gone.
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        var file = WriteFile("once.txt", "x");
        var model = CreateModel();

        Assert.True(model.Select(file));
        Assert.True(model.HoldSelected(isMove: true));
        Assert.True(model.NavigateTo(destination));
        Assert.True(model.Paste());

        Assert.False(model.CanPaste);
        Assert.False(model.Paste());
    }

    [Fact]
    public void Cutting_and_pasting_into_the_same_directory_is_a_no_op_that_says_so()
    {
        var file = WriteFile("stay.txt", "x");
        var model = CreateModel();

        Assert.True(model.Select(file));
        Assert.True(model.HoldSelected(isMove: true));
        Assert.True(model.Paste());

        Assert.True(File.Exists(file));
        Assert.False(File.Exists(Path.Combine(_root, "stay (2).txt")));
        Assert.Contains("already here", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_clipboard_is_shared_so_two_panes_can_exchange_a_file()
    {
        // The reason the clipboard is not per-pane: copy on the left, paste on the right.
        var destination = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        var file = WriteFile("shared.txt", "payload");
        var clipboard = new FileBrowserClipboard();

        var left = CreateModel(clipboard: clipboard);
        var right = CreateModel(currentDirectory: destination, clipboard: clipboard);

        Assert.True(left.Select(file));
        Assert.True(left.HoldSelected(isMove: false));
        Assert.True(right.Paste());

        Assert.Equal("payload", File.ReadAllText(Path.Combine(destination, "shared.txt")));
    }

    [Fact]
    public void Several_selected_items_are_deleted_together()
    {
        var a = WriteFile("a.txt", "a");
        var b = WriteFile("b.txt", "b");
        var keep = WriteFile("keep.txt", "k");
        var model = CreateModel();

        model.SelectMany([a, b]);
        Assert.True(model.DeleteSelected(permanent: true), model.StatusMessage);

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(keep));
        Assert.Contains("2 items", model.StatusMessage!);
    }

    [Fact]
    public void A_refresh_keeps_every_selected_item_that_is_still_there()
    {
        var a = WriteFile("a.txt", "a");
        var b = WriteFile("b.txt", "b");
        var model = CreateModel();
        model.SelectMany([a, b]);

        File.Delete(b);
        model.Refresh();

        Assert.Equal(a, Assert.Single(model.SelectedItems).Path);
    }

    [Fact]
    public void Operating_on_nothing_asks_for_a_selection_rather_than_failing()
    {
        var model = CreateModel();

        Assert.False(model.DeleteSelected(permanent: false));
        Assert.Contains("Select something", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);

        Assert.False(model.RenameSelected("new"));
        Assert.Contains("Select something", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_read_only_filesystem_refuses_and_reports_itself_as_read_only()
    {
        var model = new FileBrowserModel(CreatePane(_root), _root, new ReadOnlyFilesystem(_root));

        Assert.False(model.CanModify);
        Assert.False(model.CreateDirectory("nope"));
        Assert.Contains("read-only", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------------

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private FileBrowserModel CreateModel(
        string? currentDirectory = null,
        FileBrowserClipboard? clipboard = null) =>
        new(CreatePane(currentDirectory ?? _root),
            _root,
            new SystemFileBrowserFileSystem(_trash),
            clipboard ?? new FileBrowserClipboard());

    private static Pane CreatePane(string currentDirectory) =>
        new(PaneId.New(), PaneKind.FileBrowser, "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["current_directory"] = currentDirectory,
            },
        });

    /// <summary>A Recycle Bin that records rather than recycles, so no test touches the real one.</summary>
    private sealed class RecordingTrash : IFileTrash
    {
        public List<(string Path, bool IsDirectory)> Sent { get; } = [];
        public bool Available { get; set; } = true;
        public string? FailWith { get; set; }

        public bool IsAvailable => Available;

        public bool TrySend(string path, bool isDirectory, out string? error)
        {
            if (FailWith is not null)
            {
                error = FailWith;
                return false;
            }

            Sent.Add((path, isDirectory));
            error = null;
            return true;
        }
    }

    /// <summary>Browsable but unchangeable, like an FTP account with no write permission.</summary>
    private sealed class ReadOnlyFilesystem(string root) : IFileBrowserFileSystem
    {
        private readonly SystemFileBrowserFileSystem _inner = new(new RecordingTrash());

        public StringComparer PathComparer => _inner.PathComparer;
        public string GetFullPath(string path) => _inner.GetFullPath(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public string? GetParentDirectory(string path) => _inner.GetParentDirectory(path);
        public string Combine(string directory, string name) => _inner.Combine(directory, name);
        public bool FileExists(string path) => _inner.FileExists(path);
        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path) =>
            _inner.EnumerateEntries(path);

        // CanModify and CanRecoverDeletes take the interface's refusing defaults; that is the point.
        public string Root => root;
    }
}
