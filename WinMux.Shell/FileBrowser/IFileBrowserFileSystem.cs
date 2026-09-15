namespace WinMux.Shell.FileBrowser;

/// <summary>
/// Narrow filesystem boundary for the navigation model. It keeps directory policy testable and
/// leaves platform-specific presentation concerns outside the model.
/// </summary>
internal interface IFileBrowserFileSystem
{
    StringComparer PathComparer { get; }
    string GetFullPath(string path);
    bool DirectoryExists(string path);
    string? GetParentDirectory(string path);
    IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path);
}

internal sealed class SystemFileBrowserFileSystem : IFileBrowserFileSystem
{
    public static SystemFileBrowserFileSystem Instance { get; } = new();

    private SystemFileBrowserFileSystem()
    {
    }

    public StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public string GetFullPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? GetParentDirectory(string path) => Directory.GetParent(path)?.FullName;

    public IEnumerable<FileBrowserNavigationItem> EnumerateEntries(string path)
    {
        var directory = new DirectoryInfo(path);

        foreach (var child in directory.EnumerateDirectories())
        {
            yield return new FileBrowserNavigationItem(child.Name, child.FullName, IsDirectory: true);
        }

        foreach (var child in directory.EnumerateFiles())
        {
            yield return new FileBrowserNavigationItem(child.Name, child.FullName, IsDirectory: false);
        }
    }
}
