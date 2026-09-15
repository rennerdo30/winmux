using Microsoft.VisualBasic.FileIO;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// The Windows Recycle Bin.
///
/// The underlying call is `SHFileOperationW`, and this deliberately does **not** make it directly.
/// `SHFILEOPSTRUCT` is a hand-marshalled structure with a double-null-terminated path list and a
/// packing rule that differs between architectures; getting it wrong on a *delete* does not produce
/// a compile error, it produces a delete of something else. `Microsoft.VisualBasic.FileIO` is a
/// tested wrapper over exactly that call, ships with .NET on Windows, and needs no package
/// reference — the name is unfortunate and the code is the right code.
///
/// <c>UIOption.OnlyErrorDialogs</c> keeps the confirmation ours: WinMux asks before it calls this,
/// so a second system prompt would be the same question twice. Errors still surface Windows' own
/// dialog, which is right — "the file is open in another program" is a conversation Windows is
/// better at having than a status line is.
/// </summary>
public sealed class Win32FileTrash : IFileTrash
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool TrySend(string path, bool isDirectory, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            // The user answered Windows' own error dialog with Cancel. Not a failure worth wording.
            error = "cancelled";
            return false;
        }
    }
}
