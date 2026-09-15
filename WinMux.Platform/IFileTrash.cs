namespace WinMux.Platform;

/// <summary>
/// Deleting a file the way the user can undo.
///
/// This is a platform contract rather than a call to <see cref="System.IO.File.Delete(string)"/>
/// because "delete" means something different from what a file manager should do. `File.Delete`
/// is unrecoverable; every desktop has a holding area that is not, and a user pressing Delete in a
/// file browser means the recoverable one. Windows calls it the Recycle Bin, and the API that puts
/// something there is a shell call, not a filesystem call — so it belongs here, behind intent.
///
/// A system with no such facility answers <see cref="IsAvailable"/> with false, and the caller
/// either offers a permanent delete with a clear warning or does not offer deletion at all. It must
/// never quietly fall back to an unrecoverable delete: the user asked for the one they could undo.
/// </summary>
public interface IFileTrash
{
    /// <summary>
    /// Whether this system has somewhere recoverable to put a deleted file. False means
    /// <see cref="TrySend"/> always fails, and the caller must say so rather than deleting anyway.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Move <paramref name="path"/> somewhere the user can retrieve it from.
    /// </summary>
    /// <param name="path">The file or directory to remove.</param>
    /// <param name="isDirectory">
    /// Whether the path is a directory. Passed rather than probed because the caller already knows,
    /// and a probe is a second chance for the answer to change underneath.
    /// </param>
    /// <param name="error">Why it failed, in words a user can act on, or null on success.</param>
    bool TrySend(string path, bool isDirectory, out string? error);
}
