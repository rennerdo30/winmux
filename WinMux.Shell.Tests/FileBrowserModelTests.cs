using WinMux.Core.Model;
using WinMux.Shell.FileBrowser;

namespace WinMux.Shell.Tests;

public sealed class FileBrowserModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "winmux-file-browser-" + Guid.NewGuid().ToString("N"));

    public FileBrowserModelTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Entries_are_directories_first_then_sorted_deterministically()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zebra"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        File.WriteAllText(Path.Combine(_root, "charlie.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "beta.txt"), string.Empty);

        var model = CreateModel();

        Assert.Collection(
            model.Entries,
            item => AssertEntry(item, "Alpha", isDirectory: true),
            item => AssertEntry(item, "zebra", isDirectory: true),
            item => AssertEntry(item, "beta.txt", isDirectory: false),
            item => AssertEntry(item, "charlie.txt", isDirectory: false));
    }

    [Fact]
    public void Parent_navigation_moves_up_and_clears_selection()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(child, "nested")).FullName;
        var pane = CreatePane(child);
        var model = new FileBrowserModel(pane, _root);
        Assert.True(model.Select(nested));

        Assert.True(model.NavigateParent());

        AssertPathEqual(_root, model.CurrentDirectory);
        Assert.Null(model.SelectedItem);
        Assert.Equal(string.Empty, pane.Restore.Extras["selected_path"]);
    }

    [Fact]
    public void Unavailable_restored_directory_falls_back_and_explains_it()
    {
        var unavailable = Path.Combine(_root, "gone");
        var pane = CreatePane(unavailable);

        var model = new FileBrowserModel(pane, _root);

        AssertPathEqual(_root, model.CurrentDirectory);
        Assert.NotNull(model.StatusMessage);
        Assert.Contains("unavailable", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        AssertPathEqual(unavailable, pane.Restore.Extras["current_directory"]);
    }

    [Fact]
    public void Failed_navigation_keeps_current_directory_and_surfaces_the_failure()
    {
        var model = CreateModel();

        Assert.False(model.NavigateTo(Path.Combine(_root, "missing")));

        AssertPathEqual(_root, model.CurrentDirectory);
        Assert.NotNull(model.StatusMessage);
        Assert.Contains("Cannot open", model.StatusMessage);
        Assert.Contains("Staying", model.StatusMessage);
    }

    [Fact]
    public void Terminal_handoff_uses_selected_directory_but_not_selected_file()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var file = Path.Combine(_root, "notes.txt");
        File.WriteAllText(file, string.Empty);
        var model = CreateModel();

        Assert.True(model.Select(directory));
        AssertPathEqual(directory, model.TerminalHandoffDirectory);

        Assert.True(model.Select(file));
        AssertPathEqual(_root, model.TerminalHandoffDirectory);
    }

    [Fact]
    public void Current_directory_and_selection_update_descriptor_without_losing_other_extras()
    {
        var selected = Directory.CreateDirectory(Path.Combine(_root, "selected")).FullName;
        var pane = CreatePane(_root, extras: new Dictionary<string, string>
        {
            ["provider_option"] = "preserved",
        });
        var model = new FileBrowserModel(pane, _root);

        Assert.True(model.Select(selected));

        AssertPathEqual(_root, pane.Restore.Extras["current_directory"]);
        AssertPathEqual(selected, pane.Restore.Extras["selected_path"]);
        Assert.Equal("preserved", pane.Restore.Extras["provider_option"]);
    }

    [Fact]
    public void Descriptor_fields_restore_current_directory_and_selection()
    {
        var current = Directory.CreateDirectory(Path.Combine(_root, "current")).FullName;
        var selected = Directory.CreateDirectory(Path.Combine(current, "selected")).FullName;
        var pane = CreatePane(current, selected);

        var model = new FileBrowserModel(pane, _root);

        AssertPathEqual(current, model.CurrentDirectory);
        Assert.NotNull(model.SelectedItem);
        AssertPathEqual(selected, model.SelectedItem.Path);
        AssertPathEqual(selected, model.TerminalHandoffDirectory);
    }

    [Fact]
    public void Missing_restored_selection_is_cleared_and_reported()
    {
        var missing = Path.Combine(_root, "missing.txt");
        var pane = CreatePane(_root, missing);

        var model = new FileBrowserModel(pane, _root);

        Assert.Null(model.SelectedItem);
        Assert.NotNull(model.StatusMessage);
        Assert.Contains("restored selection", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, pane.Restore.Extras["selected_path"]);
    }

    private FileBrowserModel CreateModel(string? currentDirectory = null) =>
        new(CreatePane(currentDirectory ?? _root), _root);

    private static Pane CreatePane(
        string currentDirectory,
        string? selectedPath = null,
        IReadOnlyDictionary<string, string>? extras = null)
    {
        var descriptorExtras = new Dictionary<string, string>(
            extras ?? new Dictionary<string, string>(),
            StringComparer.Ordinal)
        {
            ["current_directory"] = currentDirectory,
        };
        if (selectedPath is not null)
        {
            descriptorExtras["selected_path"] = selectedPath;
        }

        return new Pane(PaneId.New(), PaneKind.FileBrowser, "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = descriptorExtras,
        });
    }

    private static void AssertEntry(
        FileBrowserNavigationItem entry,
        string name,
        bool isDirectory)
    {
        Assert.Equal(name, entry.Name);
        Assert.Equal(isDirectory, entry.IsDirectory);
    }

    private static void AssertPathEqual(string expected, string actual) =>
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(actual)),
            ignoreCase: OperatingSystem.IsWindows());
}
