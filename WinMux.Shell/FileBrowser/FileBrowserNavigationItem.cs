namespace WinMux.Shell.FileBrowser;

/// <summary>
/// A directory or file in the navigation model's deterministic current listing.
///
/// <see cref="Size"/> and <see cref="Modified"/> are what the detail columns show. Both are optional
/// because not every filesystem can say: an FTP server that answers only the bare <c>NLST</c> gives
/// names and nothing else, and a column should show a blank rather than a made-up zero.
/// </summary>
/// <param name="Size">Bytes, for a file. Null for a folder, and when the filesystem did not say.</param>
/// <param name="Modified">When it last changed, or null when the filesystem did not say.</param>
internal sealed record FileBrowserNavigationItem(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size = null,
    DateTimeOffset? Modified = null);
