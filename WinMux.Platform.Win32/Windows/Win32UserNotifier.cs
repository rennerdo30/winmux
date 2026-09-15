using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Windows implementation of <see cref="IUserNotifier"/>.
///
/// A plain message box on purpose: this runs when startup has already failed, so it must not depend
/// on Avalonia having initialised or on a window existing.
/// </summary>
public sealed class Win32UserNotifier : IUserNotifier
{
    public void ShowError(string title, string message) =>
        _ = NativeMethods.MessageBoxW(nint.Zero, message, title, NativeMethods.MB_OK | NativeMethods.MB_ICONERROR);

    private static class NativeMethods
    {
        internal const uint MB_OK = 0x00000000;
        internal const uint MB_ICONERROR = 0x00000010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
    }
}
