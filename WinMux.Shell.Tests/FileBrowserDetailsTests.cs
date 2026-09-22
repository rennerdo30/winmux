using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WinMux.Core.Model;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a file-browser pane says about its entries and about itself: the detail columns, their order,
/// and — for a server — whether it is connected and how to try again.
/// </summary>
public sealed class FileBrowserDetailsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winmux-details-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- columns and sorting --------------------------------------------------------------

    [Fact]
    public void Local_entries_carry_their_size_and_date()
    {
        File.WriteAllBytes(Path.Combine(_root, "three.bin"), new byte[3000]);
        var model = CreateModel();

        var entry = Assert.Single(model.Entries);
        Assert.Equal(3000, entry.Size);
        Assert.NotNull(entry.Modified);
    }

    [Fact]
    public void Sorting_by_size_puts_the_largest_first_and_keeps_folders_on_top()
    {
        File.WriteAllBytes(Path.Combine(_root, "small.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "large.bin"), new byte[10_000]);
        Directory.CreateDirectory(Path.Combine(_root, "zz-folder"));
        var model = CreateModel();

        model.SortBy(FileBrowserSortColumn.Size);

        Assert.True(model.SortDescending, "a new size sort starts largest first, as in Explorer");
        Assert.Equal(["zz-folder", "large.bin", "small.bin"], model.Entries.Select(e => e.Name));

        model.SortBy(FileBrowserSortColumn.Size);
        Assert.Equal(["zz-folder", "small.bin", "large.bin"], model.Entries.Select(e => e.Name));
    }

    [Fact]
    public void The_sort_survives_a_session_round_trip()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        var pane = CreatePane();
        var model = new FileBrowserModel(pane, _root, new SystemFileBrowserFileSystem(new NoTrash()));
        model.SortBy(FileBrowserSortColumn.Modified);

        var restored = new FileBrowserModel(
            new Pane(PaneId.New(), PaneKind.FileBrowser, "files", pane.Restore),
            _root,
            new SystemFileBrowserFileSystem(new NoTrash()));

        Assert.Equal(FileBrowserSortColumn.Modified, restored.SortColumn);
        Assert.True(restored.SortDescending);
    }

    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1025L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GB")]
    public void Sizes_read_the_way_Explorer_shows_them(long bytes, string expected)
    {
        var item = new FileBrowserNavigationItem("f", "f", IsDirectory: false, Size: bytes);

        Assert.Equal(expected, FileBrowserPaneRuntime.FormatSize(item).Replace(',', '.'));
    }

    [Fact]
    public void A_folder_and_an_unknown_size_show_no_size()
    {
        Assert.Equal("", FileBrowserPaneRuntime.FormatSize(new FileBrowserNavigationItem("d", "d", IsDirectory: true)));
        Assert.Equal("", FileBrowserPaneRuntime.FormatSize(new FileBrowserNavigationItem("f", "f", IsDirectory: false)));
    }

    // ---- a server pane that is not connected ----------------------------------------------

    [Fact]
    public Task A_refused_connection_is_one_sentence_and_Try_again_reconnects() => Headless.RunAsync(async () =>
    {
        var target = new RemoteFileBrowserTarget(RemoteFileBrowserTarget.Sftp, "build.example.com", 22, "alice");
        var working = new OneFileServer();
        var asked = false;
        var runtime = new FileBrowserPaneRuntime(
            RemotePanes.At("/"),
            RemotePath.Root,
            new RefusingServer(),
            target,
            askAgain =>
            {
                asked = askAgain;
                return Task.FromResult<IFileBrowserFileSystem?>(working);
            });
        await runtime.InitializeAsync(CancellationToken.None);

        var window = new Window { Width = 900, Height = 600, Content = runtime.View };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = runtime.View.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        var banner = Assert.Single(texts, t => t.StartsWith("Not connected to", StringComparison.Ordinal));
        Assert.Contains("refused the password", banner);
        Assert.Contains(texts, t => t.Contains("SFTP · alice@build.example.com", StringComparison.Ordinal));

        var retry = runtime.View.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Try again"));
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var list = runtime.View.GetVisualDescendants().OfType<ListBox>().Single();
        for (var waited = 0; waited < 5000 && list.Items.Count == 0; waited += 25)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.True(asked, "trying again after a refusal must ask for the password, not reuse the refused one");
        Assert.Single(list.Items);
        Assert.False(retry.IsEffectivelyVisible, "the banner goes once connected");
        window.Close();
        await runtime.DisposeAsync();
    });

    // ---- helpers ---------------------------------------------------------------------

    private FileBrowserModel CreateModel() =>
        new(CreatePane(), _root, new SystemFileBrowserFileSystem(new NoTrash()));

    private Pane CreatePane() =>
        new(PaneId.New(), PaneKind.FileBrowser, "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal) { ["current_directory"] = _root },
        });

    private sealed class NoTrash : WinMux.Platform.IFileTrash
    {
        public bool IsAvailable => false;

        public bool TrySend(string path, bool isDirectory, out string? error)
        {
            error = "no bin in tests";
            return false;
        }
    }

    /// <summary>A server that turns away every call, as one does after a wrong password.</summary>
    private sealed class RefusingServer : IFileBrowserFileSystem
    {
        public StringComparer PathComparer => StringComparer.Ordinal;
        public string GetFullPath(string path) => RemotePath.Normalize(path);
        public string? GetParentDirectory(string path) => RemotePath.Parent(path);
        public string Combine(string directory, string name) => RemotePath.Combine(directory, name);
        public bool DirectoryExists(string path) => throw Refused();
        public bool FileExists(string path) => throw Refused();
        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path) => throw Refused();

        private static IOException Refused() => new("the server refused the password");
    }

    /// <summary>A server with one file in its root.</summary>
    private sealed class OneFileServer : IFileBrowserFileSystem
    {
        public StringComparer PathComparer => StringComparer.Ordinal;
        public string GetFullPath(string path) => RemotePath.Normalize(path);
        public string? GetParentDirectory(string path) => RemotePath.Parent(path);
        public string Combine(string directory, string name) => RemotePath.Combine(directory, name);
        public bool DirectoryExists(string path) => path == "/";
        public bool FileExists(string path) => path == "/hello.txt";

        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path) =>
            [new FileBrowserNavigationItem("hello.txt", "/hello.txt", IsDirectory: false, Size: 5)];
    }
}
