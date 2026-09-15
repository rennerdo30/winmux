namespace WinMux.Shell.FileBrowser;

/// <summary>A directory or file in the navigation model's deterministic current listing.</summary>
internal sealed record FileBrowserNavigationItem(string Name, string Path, bool IsDirectory);
