namespace WinMux.Platform;

/// <summary>
/// The picture the system shows for a file or a folder — what Explorer draws beside a name.
///
/// Separate from <see cref="IAppIconSource"/> because the question is different. That one asks
/// "what does this program look like", which needs the executable. This one asks "what does a
/// <c>.pdf</c> look like here", which needs only a name — and that is what makes it work for a
/// file on an SFTP server, where there is no local file to ask about.
///
/// PNG bytes for the same reason as <see cref="IAppIconSource"/>: <c>WinMux.Platform</c> carries no
/// UI framework, and a byte array is something every toolkit can turn into its own bitmap.
/// </summary>
public interface IFileIconSource
{
    /// <summary>Whether this system can produce icons at all. False means every call returns null.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The icon for an entry named <paramref name="name"/>, as PNG bytes, or null.
    /// </summary>
    /// <param name="name">The file or folder name. Only its extension matters for a file.</param>
    /// <param name="isDirectory">True for a folder.</param>
    /// <param name="localPath">
    /// The entry's path on this machine's own disks, or null for anything remote. Some files carry
    /// their own icon — a program, a shortcut, an <c>.ico</c> — and only the file itself can answer
    /// for those. A network path should be passed as null: reading a file's icon over SMB costs a
    /// round trip per row, and the type icon is a fine answer.
    /// </param>
    /// <param name="size">The wanted edge length in pixels; the result may differ.</param>
    byte[]? GetIconPng(string name, bool isDirectory, string? localPath, int size = 32);

    /// <summary>
    /// What the system calls this kind of entry — "Text Document", "File folder" — for the browser's
    /// Type column, or null when it has no name for it. Answered from the name alone, like the icon.
    /// </summary>
    string? GetTypeName(string name, bool isDirectory);
}
