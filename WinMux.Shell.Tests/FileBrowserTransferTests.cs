using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// Copying and moving between two filesystems: a local folder and a server.
///
/// The "server" is <see cref="MemoryFilesystem"/>, a POSIX-shaped filesystem held in a dictionary,
/// because what is under test is the model and the transfer — the path each filesystem is asked
/// about, what is created, what is removed and when — not SSH. The live SFTP/FTP tests cover the
/// wire. The local side is real files, as in <see cref="FileBrowserOperationTests"/>.
///
/// Before this existed, a path copied in a remote pane was pasted into a local one by handing
/// <c>/home/alice/notes.txt</c> to the local disk. The first test pins that it no longer is.
/// </summary>
public sealed class FileBrowserTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "winmux-transfer-" + Guid.NewGuid().ToString("N"));

    private readonly SystemFileBrowserFileSystem _local = new(new NoTrash());
    private readonly MemoryFilesystem _remote = new();
    private readonly FileBrowserClipboard _clipboard = new();

    public FileBrowserTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_remote_file_pasted_into_a_local_pane_arrives_with_its_contents()
    {
        _remote.AddFile("/home/alice/notes.txt", "from the server");
        var remote = RemoteModel("/home/alice");
        var local = LocalModel();

        Assert.True(remote.Select("/home/alice/notes.txt"));
        Assert.True(remote.HoldSelected(isMove: false));
        Assert.True(local.Paste(), local.StatusMessage);

        Assert.Equal("from the server", File.ReadAllText(Path.Combine(_root, "notes.txt")));
        Assert.True(_remote.Exists("/home/alice/notes.txt"), "a copy must leave the original");
        Assert.Equal(Path.Combine(_root, "notes.txt"), local.SelectedPath);
    }

    [Fact]
    public void A_local_folder_pasted_into_a_remote_pane_arrives_whole()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        File.WriteAllText(Path.Combine(folder, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(folder, "src", "deep"));
        File.WriteAllText(Path.Combine(folder, "src", "deep", "b.txt"), "beta");
        _remote.AddDirectory("/srv");

        var local = LocalModel();
        var remote = RemoteModel("/srv");

        Assert.True(local.Select(folder));
        Assert.True(local.HoldSelected(isMove: false));
        Assert.True(remote.Paste(), remote.StatusMessage);

        Assert.Equal("alpha", _remote.Read("/srv/project/a.txt"));
        Assert.Equal("beta", _remote.Read("/srv/project/src/deep/b.txt"));
        Assert.Contains("2 file(s)", remote.StatusMessage!);
        Assert.Equal("/srv/project", remote.SelectedPath);
    }

    [Fact]
    public void Pasting_across_never_overwrites_and_picks_a_free_name()
    {
        _remote.AddFile("/data/report.txt", "new");
        File.WriteAllText(Path.Combine(_root, "report.txt"), "original");

        var remote = RemoteModel("/data");
        var local = LocalModel();
        Assert.True(remote.Select("/data/report.txt"));
        Assert.True(remote.HoldSelected(isMove: false));
        Assert.True(local.Paste(), local.StatusMessage);

        Assert.Equal("original", File.ReadAllText(Path.Combine(_root, "report.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_root, "report (2).txt")));
    }

    [Fact]
    public void A_move_across_removes_the_original_only_after_the_copy()
    {
        _remote.AddFile("/data/move-me.txt", "payload");
        var remote = RemoteModel("/data");
        var local = LocalModel();

        Assert.True(remote.Select("/data/move-me.txt"));
        Assert.True(remote.HoldSelected(isMove: true));
        Assert.True(local.Paste(), local.StatusMessage);

        Assert.Equal("payload", File.ReadAllText(Path.Combine(_root, "move-me.txt")));
        Assert.False(_remote.Exists("/data/move-me.txt"));
        Assert.False(_clipboard.HasContent, "a cut is spent once it has been pasted");
        Assert.StartsWith("Moved", local.StatusMessage);
    }

    [Fact]
    public void A_move_that_fails_partway_leaves_the_original_untouched_and_no_partial_file()
    {
        // The rule that matters most here: an interrupted move must not cost the user the file.
        _remote.AddFile("/data/big.bin", new string('x', 300_000));
        _remote.FailReadAfterBytes = 100_000;

        var remote = RemoteModel("/data");
        var local = LocalModel();
        Assert.True(remote.Select("/data/big.bin"));
        Assert.True(remote.HoldSelected(isMove: true));

        Assert.False(local.Paste());

        Assert.True(_remote.Exists("/data/big.bin"), "the original must survive a failed move");
        Assert.False(File.Exists(Path.Combine(_root, "big.bin")), "a truncated copy must not be left looking real");
        Assert.True(_clipboard.HasContent, "a failed cut can be tried again");
        Assert.Contains("Cannot paste 'big.bin'", local.StatusMessage!);
    }

    [Fact]
    public void A_cancelled_copy_removes_the_file_it_was_writing()
    {
        _remote.AddFile("/data/big.bin", new string('x', 300_000));
        using var cancel = new CancellationTokenSource();
        _remote.OnRead = cancel.Cancel;

        var remote = RemoteModel("/data");
        var local = LocalModel();
        Assert.True(remote.Select("/data/big.bin"));
        Assert.True(remote.HoldSelected(isMove: false));

        Assert.False(local.Paste(cancel.Token));

        Assert.False(File.Exists(Path.Combine(_root, "big.bin")));
        Assert.Contains("cancelled", local.StatusMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_remote_name_that_windows_cannot_hold_is_refused_before_anything_moves()
    {
        _remote.AddFile("/data/a:b.txt", "colon");
        var remote = RemoteModel("/data");
        var local = LocalModel();

        Assert.True(remote.Select("/data/a:b.txt"));
        Assert.True(remote.HoldSelected(isMove: true));
        Assert.False(local.Paste());

        Assert.True(_remote.Exists("/data/a:b.txt"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public void A_failure_to_remove_the_original_is_reported_but_the_move_still_counts()
    {
        _remote.AddFile("/data/stuck.txt", "payload");
        _remote.FailDeletes = true;
        var remote = RemoteModel("/data");
        var local = LocalModel();

        Assert.True(remote.Select("/data/stuck.txt"));
        Assert.True(remote.HoldSelected(isMove: true));
        Assert.True(local.Paste());

        Assert.Equal("payload", File.ReadAllText(Path.Combine(_root, "stuck.txt")));
        Assert.True(_remote.Exists("/data/stuck.txt"));
        Assert.Contains("still there", local.StatusMessage!);
    }

    [Fact]
    public void Pasting_from_a_closed_pane_says_so_instead_of_throwing()
    {
        _remote.AddFile("/data/gone.txt", "payload");
        var remote = RemoteModel("/data");
        var local = LocalModel();
        Assert.True(remote.Select("/data/gone.txt"));
        Assert.True(remote.HoldSelected(isMove: false));

        _remote.Dispose();

        Assert.False(local.Paste());
        Assert.Contains("closed", local.StatusMessage!);
    }

    [Fact]
    public void Closing_the_source_pane_forgets_its_clipboard_entry()
    {
        _remote.AddFile("/data/x.txt", "x");
        var remote = RemoteModel("/data");
        Assert.True(remote.Select("/data/x.txt"));
        Assert.True(remote.HoldSelected(isMove: false));

        _clipboard.Forget(new MemoryFilesystem());
        Assert.True(_clipboard.HasContent, "only the entry from the closing filesystem goes");

        _clipboard.Forget(_remote);
        Assert.False(_clipboard.HasContent);
    }

    [Fact]
    public void A_link_that_leads_back_into_itself_stops_instead_of_filling_the_disk()
    {
        _remote.AddDirectory("/loop");
        _remote.SelfReferencingDirectory = "/loop";
        _remote.AddDirectory("/");
        var remote = RemoteModel("/");
        var local = LocalModel();

        Assert.True(remote.Select("/loop"));
        Assert.True(remote.HoldSelected(isMove: false));
        Assert.False(local.Paste());

        Assert.Contains("folders deep", local.StatusMessage!);
    }

    [Fact]
    public void Within_one_filesystem_the_filesystem_still_does_its_own_copy()
    {
        // Not a regression into streaming everything: a local-to-local paste stays File.Copy, and a
        // server-to-same-server one stays the server's own call.
        _remote.AddFile("/a/f.txt", "same");
        _remote.AddDirectory("/b");
        var left = RemoteModel("/a");
        var right = RemoteModel("/b");

        Assert.True(left.Select("/a/f.txt"));
        Assert.True(left.HoldSelected(isMove: true));
        Assert.True(right.Paste(), right.StatusMessage);

        Assert.Equal(1, _remote.Moves);
        Assert.Equal("same", _remote.Read("/b/f.txt"));
    }

    [Fact]
    public void A_move_across_tells_the_pane_it_came_from_to_refresh()
    {
        // Cut on the left, paste on the right: the left pane must not go on listing a file that has
        // gone. The announcement names the source directory on the source filesystem.
        _remote.AddFile("/data/leaving.txt", "payload");
        var remote = RemoteModel("/data");
        var local = LocalModel();
        var heard = new List<FileBrowserDirectoryChanged>();
        EventHandler<FileBrowserDirectoryChanged> listen = (_, change) =>
        {
            if (ReferenceEquals(change.FileSystem, _remote)) lock (heard) heard.Add(change);
        };

        FileBrowserChanges.DirectoryChanged += listen;
        try
        {
            Assert.True(remote.Select("/data/leaving.txt"));
            Assert.True(remote.HoldSelected(isMove: true));
            Assert.True(local.Paste(), local.StatusMessage);
        }
        finally
        {
            FileBrowserChanges.DirectoryChanged -= listen;
        }

        var change = Assert.Single(heard);
        Assert.True(remote.IsShowing(change.FileSystem, change.Directory));
        Assert.False(local.IsShowing(change.FileSystem, change.Directory), "a local pane is not showing a server directory");
    }

    [Fact]
    public void Several_items_selected_together_all_arrive_across()
    {
        _remote.AddFile("/data/a.txt", "alpha");
        _remote.AddFile("/data/b.txt", "beta");
        _remote.AddFile("/data/sub/c.txt", "gamma");
        var remote = RemoteModel("/data");
        var local = LocalModel();

        remote.SelectMany(["/data/a.txt", "/data/b.txt", "/data/sub"]);
        Assert.Equal(3, remote.SelectedItems.Count);
        Assert.True(remote.HoldSelected(isMove: false));
        Assert.True(local.Paste(), local.StatusMessage);

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_root, "b.txt")));
        Assert.Equal("gamma", File.ReadAllText(Path.Combine(_root, "sub", "c.txt")));
        Assert.Equal(3, local.SelectedItems.Count);
        Assert.StartsWith("Copied 3 items here", local.StatusMessage!);
    }

    [Fact]
    public void A_batch_that_fails_partway_keeps_only_the_rest_on_the_clipboard()
    {
        // Two small files arrive, the big one fails. Trying again must not move the first two twice.
        _remote.AddFile("/data/small-1.txt", "one");
        _remote.AddFile("/data/big.bin", new string('x', 300_000));
        _remote.AddFile("/data/small-2.txt", "two");
        _remote.FailReadAfterBytes = 100_000;
        var remote = RemoteModel("/data");
        var local = LocalModel();

        remote.SelectMany(["/data/small-1.txt", "/data/big.bin", "/data/small-2.txt"]);
        Assert.True(remote.HoldSelected(isMove: true));
        Assert.False(local.Paste());

        Assert.True(File.Exists(Path.Combine(_root, "small-1.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "small-2.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "big.bin")));
        Assert.True(_remote.Exists("/data/big.bin"), "the one that failed stays where it was");
        Assert.False(_remote.Exists("/data/small-1.txt"));

        var left = Assert.Single(_clipboard.Entries);
        Assert.Equal("/data/big.bin", left.Path);
        Assert.Contains("2 of 3", local.StatusMessage!);
        Assert.Contains("Cannot paste 'big.bin'", local.StatusMessage!);
    }

    [Fact]
    public void A_drop_onto_a_folder_puts_the_items_inside_it()
    {
        _remote.AddFile("/data/dropped.txt", "payload");
        var sub = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        var local = LocalModel();

        Assert.True(local.Transfer(
            _remote,
            [new FileBrowserClipboardEntry("/data/dropped.txt", false)],
            sub,
            isMove: false), local.StatusMessage);

        Assert.Equal("payload", File.ReadAllText(Path.Combine(sub, "dropped.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "dropped.txt")));
        Assert.Contains("into 'inbox'", local.StatusMessage!);
    }

    [Fact]
    public void A_drop_from_the_same_filesystem_moves_by_rename()
    {
        _remote.AddFile("/a/f.txt", "same");
        _remote.AddDirectory("/b");
        var right = RemoteModel("/b");

        Assert.True(right.Transfer(_remote, [new FileBrowserClipboardEntry("/a/f.txt", false)], null, isMove: true));

        Assert.Equal(1, _remote.Moves);
        Assert.False(_remote.Exists("/a/f.txt"));
    }

    // ---- helpers ---------------------------------------------------------------------

    private FileBrowserModel LocalModel() => new(Pane(_root), _root, _local, _clipboard);

    private FileBrowserModel RemoteModel(string directory) =>
        new(Pane(directory), RemotePath.Root, _remote, _clipboard);

    private static Pane Pane(string currentDirectory) =>
        new(PaneId.New(), PaneKind.FileBrowser, "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["current_directory"] = currentDirectory,
            },
        });

    private sealed class NoTrash : IFileTrash
    {
        public bool IsAvailable => false;

        public bool TrySend(string path, bool isDirectory, out string? error)
        {
            error = "no bin in tests";
            return false;
        }
    }

    /// <summary>A POSIX-shaped filesystem in memory, with the failures a real server produces.</summary>
    private sealed class MemoryFilesystem : IFileBrowserFileSystem, IDisposable
    {
        private readonly Dictionary<string, byte[]?> _entries = new(StringComparer.Ordinal) { ["/"] = null };
        private bool _disposed;

        public int? FailReadAfterBytes { get; set; }
        public bool FailDeletes { get; set; }
        public Action? OnRead { get; set; }
        public string? SelfReferencingDirectory { get; set; }
        public int Moves { get; private set; }

        public void AddDirectory(string path)
        {
            for (var walk = RemotePath.Normalize(path); walk is not null; walk = RemotePath.Parent(walk))
            {
                _entries.TryAdd(walk, null);
            }
        }

        public void AddFile(string path, string content)
        {
            AddDirectory(RemotePath.Parent(path)!);
            _entries[path] = System.Text.Encoding.UTF8.GetBytes(content);
        }

        public bool Exists(string path) => _entries.ContainsKey(path);
        public string Read(string path) => System.Text.Encoding.UTF8.GetString(_entries[path]!);

        public StringComparer PathComparer => StringComparer.Ordinal;
        public bool CanModify => true;
        public string GetFullPath(string path) => RemotePath.Normalize(path);
        public string Combine(string directory, string name) => RemotePath.Combine(directory, name);
        public string? GetParentDirectory(string path) => RemotePath.Parent(path);
        public bool DirectoryExists(string path) => Live(() => _entries.TryGetValue(path, out var v) && v is null);
        public bool FileExists(string path) => Live(() => _entries.TryGetValue(path, out var v) && v is not null);

        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path) => Live(() =>
        {
            var children = _entries
                .Where(e => e.Key != path && RemotePath.Parent(e.Key) == path)
                .Select(e => new FileBrowserNavigationItem(e.Key[(e.Key.LastIndexOf('/') + 1)..], e.Key, e.Value is null))
                .ToList();

            // A symlink to its own ancestor: every listing of the directory shows the directory again.
            if (path.StartsWith(SelfReferencingDirectory ?? "\0", StringComparison.Ordinal))
            {
                children.Add(new FileBrowserNavigationItem("again", RemotePath.Combine(SelfReferencingDirectory!, "again"), true));
            }

            return children;
        });

        public void CreateDirectory(string path) => Live(() => _entries[path] = null);

        public void Move(string source, string destination, bool isDirectory) => Live(() =>
        {
            Moves++;
            _entries[destination] = _entries[source];
            _entries.Remove(source);
            return true;
        });

        public void Delete(string path, bool isDirectory, bool permanent) => Live(() =>
        {
            if (FailDeletes) throw new IOException("permission denied");
            foreach (var key in _entries.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
            {
                _entries.Remove(key);
            }

            return true;
        });

        public void ReadFile(string path, Action<Stream> read) => Live(() =>
        {
            // A "self-referencing" path is synthesised by the listing and has no bytes; it is only
            // ever a directory.
            OnRead?.Invoke();
            using var stream = new FailingStream(_entries[path]!, FailReadAfterBytes);
            read(stream);
            return true;
        });

        public void WriteNewFile(string path, Action<Stream> write) => Live(() =>
        {
            if (_entries.ContainsKey(path)) throw new IOException("exists");
            using var buffer = new MemoryStream();
            _entries[path] = [];
            write(buffer);
            _entries[path] = buffer.ToArray();
            return true;
        });

        public void Dispose() => _disposed = true;

        private T Live<T>(Func<T> operation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return operation();
        }

        private void Live(Action operation) => Live(() => { operation(); return true; });
    }

    /// <summary>A read stream that drops the connection partway through, as a server does.</summary>
    private sealed class FailingStream(byte[] content, int? failAfter) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter is { } limit && Position >= limit) throw new IOException("the connection to the server failed");
            return base.Read(buffer, offset, Math.Min(count, 16_384));
        }
    }
}
