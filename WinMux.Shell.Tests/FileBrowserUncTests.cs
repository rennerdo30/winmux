using WinMux.Core.Model;
using WinMux.Shell.FileBrowser;

namespace WinMux.Shell.Tests;

/// <summary>
/// Browsing a network share.
///
/// WinMux speaks no network filesystem protocol itself and deliberately never will: Windows mounts
/// SMB shares as UNC paths and the file browser walks <c>Directory</c>/<c>DirectoryInfo</c>, which
/// handle <c>\\server\share</c> natively. That claim was asserted for a while before anyone checked
/// it, so these tests pin the parts of it that are actually our code's problem.
///
/// The filesystem fake supplies only what touches the network — whether a directory exists and what
/// is in it. Path normalisation and parent resolution use the **real** <c>Path</c> and
/// <c>Directory</c> APIs, because those are pure string logic for UNC and resolve a server that does
/// not exist without so much as a DNS lookup. So this measures .NET's genuine UNC behaviour against
/// a fabricated tree, rather than measuring a model of it that could agree with the code and still
/// both be wrong.
///
/// Verified against a live share on 2026-09-16 (<c>\\localhost\C$</c>): normalisation is stable,
/// enumeration returns UNC <c>FullName</c>s, and those round-trip through normalisation unchanged,
/// which is what selection matching depends on.
/// </summary>
public sealed class FileBrowserUncTests
{
    private const string Share = @"\\nas.example.com\media";

    [Fact]
    public void A_share_is_browsable_and_keeps_its_unc_form()
    {
        // Not rewritten to a drive letter, not stripped to one backslash: the path a user typed is
        // the path that gets persisted into the restore descriptor.
        var pane = CreatePane(Share);
        var model = new FileBrowserModel(pane, Share, Filesystem());

        Assert.Equal(Share, model.CurrentDirectory);
        Assert.Equal(Share, pane.Restore.Extras["current_directory"]);
    }

    [Fact]
    public void A_trailing_separator_does_not_make_a_second_directory()
    {
        // '\\nas\media' and '\\nas\media\' must be one location, or the restore descriptor and the
        // live state disagree and a selection silently stops matching.
        var model = new FileBrowserModel(CreatePane(Share + @"\"), Share, Filesystem());

        Assert.Equal(Share, model.CurrentDirectory);
    }

    [Fact]
    public void Entries_under_a_share_are_addressed_by_unc_path()
    {
        var model = new FileBrowserModel(CreatePane(Share), Share, Filesystem());

        Assert.Collection(
            model.Entries,
            item => Assert.Equal(Share + @"\music", item.Path),
            item => Assert.Equal(Share + @"\photos", item.Path),
            item => Assert.Equal(Share + @"\readme.txt", item.Path));
    }

    [Fact]
    public void Selecting_something_on_a_share_works()
    {
        // This is the round-trip the live check confirmed: an entry's own path, normalised again,
        // still matches the entry.
        var model = new FileBrowserModel(CreatePane(Share), Share, Filesystem());

        Assert.True(model.Select(Share + @"\photos"));
        Assert.Equal(Share + @"\photos", model.SelectedPath);
        Assert.Equal(Share + @"\photos", model.TerminalHandoffDirectory);
    }

    [Fact]
    public void Navigating_into_and_back_out_of_a_folder_on_a_share()
    {
        var model = new FileBrowserModel(CreatePane(Share), Share, Filesystem());

        Assert.True(model.NavigateTo(Share + @"\photos"));
        Assert.Equal(Share + @"\photos", model.CurrentDirectory);

        Assert.True(model.NavigateParent());
        Assert.Equal(Share, model.CurrentDirectory);
    }

    [Fact]
    public void The_share_root_is_a_root_and_says_so_instead_of_failing()
    {
        // Directory.GetParent(@"\\server\share") returns null — a share root has no parent, the way
        // C:\ has none. Measured, not assumed. The model already treats a null parent as a root, so
        // this is the one place UNC differs from a local path and it needed checking.
        var model = new FileBrowserModel(CreatePane(Share), Share, Filesystem());

        Assert.False(model.NavigateParent());
        Assert.Equal(Share, model.CurrentDirectory);
        Assert.Contains("root", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreachable_share_falls_back_and_explains_itself()
    {
        // A disconnected share is the ordinary case for a laptop, not an exception. It must not
        // look like a crash, and it must not quietly discard where the user was.
        var offline = @"\\offline.example.com\backup";
        var pane = CreatePane(offline);

        var model = new FileBrowserModel(pane, Share, Filesystem());

        Assert.Equal(Share, model.CurrentDirectory);
        Assert.NotNull(model.StatusMessage);
        Assert.Contains("unavailable", model.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // The saved location survives the outage, so reconnecting and reopening the session returns
        // to the share rather than to the fallback.
        Assert.Equal(offline, pane.Restore.Extras["current_directory"]);
    }

    [Fact]
    public void A_share_that_drops_while_it_is_open_is_reported_not_thrown()
    {
        var filesystem = Filesystem();
        var model = new FileBrowserModel(CreatePane(Share), LocalFallback, filesystem);

        filesystem.Disconnect(Share);
        model.Refresh();

        Assert.NotNull(model.StatusMessage);
        Assert.Contains("unavailable", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private const string LocalFallback = @"C:\fallback";

    private static FakeNetworkFilesystem Filesystem()
    {
        var filesystem = new FakeNetworkFilesystem();
        filesystem.AddDirectory(Share, "music", "photos");
        filesystem.AddFile(Share, "readme.txt");
        filesystem.AddDirectory(Share + @"\photos", "2026");
        filesystem.AddDirectory(Share + @"\music");
        filesystem.AddDirectory(LocalFallback);
        return filesystem;
    }

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

    /// <summary>
    /// A filesystem that exists only in memory, so a test needs no share and no network — but whose
    /// path handling is the real thing, because that is the half under test.
    /// </summary>
    private sealed class FakeNetworkFilesystem : IFileBrowserFileSystem
    {
        private readonly Dictionary<string, List<FileBrowserNavigationItem>> _directories =
            new(StringComparer.OrdinalIgnoreCase);

        public StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;

        // The real implementations. Path.GetFullPath and Directory.GetParent are string operations
        // for a UNC path and never touch the network, so the fake has no reason to reimplement them
        // and every reason not to.
        public string GetFullPath(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        public string? GetParentDirectory(string path) => Directory.GetParent(path)?.FullName;

        public string Combine(string directory, string name) => Path.Combine(directory, name);

        public bool DirectoryExists(string path) => _directories.ContainsKey(GetFullPath(path));

        public bool FileExists(string path)
        {
            var parent = GetParentDirectory(path);
            return parent is not null &&
                   _directories.TryGetValue(GetFullPath(parent), out var entries) &&
                   entries.Any(entry => !entry.IsDirectory && PathComparer.Equals(entry.Path, GetFullPath(path)));
        }

        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path) =>
            _directories.TryGetValue(GetFullPath(path), out var entries)
                ? entries
                // What Windows raises for a share it cannot reach, and what the model is written to
                // survive.
                : throw new IOException($"The network path '{path}' was not found.");

        public void AddDirectory(string path, params string[] children)
        {
            var directory = Entries(path);
            foreach (var child in children)
            {
                directory.Add(new FileBrowserNavigationItem(child, Path.Combine(path, child), IsDirectory: true));
            }
        }

        public void AddFile(string path, string name) =>
            Entries(path).Add(new FileBrowserNavigationItem(name, Path.Combine(path, name), IsDirectory: false));

        /// <summary>The share goes away, as one does when the laptop leaves the building.</summary>
        public void Disconnect(string path) => _directories.Remove(GetFullPath(path));

        private List<FileBrowserNavigationItem> Entries(string path)
        {
            var key = GetFullPath(path);
            if (!_directories.TryGetValue(key, out var entries))
            {
                _directories[key] = entries = [];
            }

            return entries;
        }
    }
}
