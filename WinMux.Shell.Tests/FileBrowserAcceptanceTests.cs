using WinMux.Core.Model;
using WinMux.Shell.FileBrowser;

namespace WinMux.Shell.Tests;

public sealed class FileBrowserAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "winmux-phase4-" + Guid.NewGuid().ToString("N"));

    public FileBrowserAcceptanceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Two_file_panes_navigate_and_persist_independently()
    {
        var leftDirectory = Directory.CreateDirectory(Path.Combine(_root, "left pane")).FullName;
        var rightDirectory = Directory.CreateDirectory(Path.Combine(_root, "右ペイン")).FullName;
        var leftPane = Pane.FileBrowser(_root, "left");
        var rightPane = Pane.FileBrowser(_root, "right");
        var left = new FileBrowserModel(leftPane, _root);
        var right = new FileBrowserModel(rightPane, _root);

        Assert.True(left.NavigateTo(leftDirectory));
        Assert.True(right.NavigateTo(rightDirectory));

        AssertPathEqual(leftDirectory, left.CurrentDirectory);
        AssertPathEqual(rightDirectory, right.CurrentDirectory);
        AssertPathEqual(leftDirectory, leftPane.Restore.Extras[FileBrowserModel.CurrentDirectoryExtra]);
        AssertPathEqual(rightDirectory, rightPane.Restore.Extras[FileBrowserModel.CurrentDirectoryExtra]);
    }

    [Fact]
    public void Unicode_and_spaces_survive_navigation_selection_and_restore()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "資料 with spaces")).FullName;
        var file = Path.Combine(directory, "mémo 日本語.txt");
        File.WriteAllText(file, "phase 4");
        var pane = Pane.FileBrowser(directory, "files", file);

        var restored = new FileBrowserModel(pane, _root);

        AssertPathEqual(directory, restored.CurrentDirectory);
        AssertPathEqual(file, restored.SelectedPath!);
        AssertPathEqual(directory, restored.TerminalHandoffDirectory);
    }

    [Fact]
    public void Cancellation_interrupts_enumeration_without_replacing_current_state()
    {
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new CancellingFileSystem(_root, () => cancellation.Cancel());
        var pane = Pane.FileBrowser(_root);
        var model = new FileBrowserModel(pane, _root, fileSystem);
        fileSystem.CancelDuringEnumeration = true;

        Assert.Throws<OperationCanceledException>(() => model.Refresh(cancellation.Token));

        AssertPathEqual(_root, model.CurrentDirectory);
        AssertPathEqual(_root, pane.Restore.Extras[FileBrowserModel.CurrentDirectoryExtra]);
    }

    private static void AssertPathEqual(string expected, string actual) =>
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            ignoreCase: OperatingSystem.IsWindows());

    private sealed class CancellingFileSystem(string root, Action cancel) : IFileBrowserFileSystem
    {
        public bool CancelDuringEnumeration { get; set; }
        public StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;
        public string GetFullPath(string path) => Path.GetFullPath(path);
        public bool DirectoryExists(string path) => true;
        public string? GetParentDirectory(string path) => null;

        public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path)
        {
            if (CancelDuringEnumeration) cancel();
            yield return new FileBrowserNavigationItem("entry", Path.Combine(root, "entry"), false);
        }
    }
}
