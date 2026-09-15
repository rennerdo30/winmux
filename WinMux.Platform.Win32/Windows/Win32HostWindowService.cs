using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Windows implementation of <see cref="IHostWindowService"/>.
///
/// Everything Windows demands in order to make a hosted window actually appear lives here, behind
/// an interface that only states intent. Each of the workarounds below was measured, and each looks
/// like superstition until it is missing:
///
/// - `SWP_NOCOPYBITS` stops Windows preserving the old client bits, which is what leaves a
///   reparented window showing whatever happened to be on screen behind it.
/// - A deliberate off-by-one resize on first placement. An app hosting XAML content re-lays-out on
///   `WM_SIZE` and otherwise never repaints after being reparented; it has to be given a size it
///   has not already seen.
/// - A hide/show cycle on first placement. Content drawn through DirectComposition — Windows 11
///   Explorer, anything XAML — keeps a visual tree bound to its old composition target across a
///   reparent, and no repaint request reaches it. Re-showing rebuilds that binding.
/// - An explicit `RedrawWindow` with `RDW_ALLCHILDREN`, because modern apps put their real content
///   in child windows.
///
/// All of it is synchronous against a foreign process and will block for as long as that process is
/// wedged. See the warning on <see cref="IHostWindowService"/>.
/// </summary>
public sealed class Win32HostWindowService : IHostWindowService
{
    /// <summary>Smallest placement worth nudging; below this the off-by-one would invert.</summary>
    private const int MinimumNudgeExtent = 2;

    public bool IsAlive(WindowHandle window) =>
        !window.IsNone && NativeMethods.IsWindow(window.ToPlatformValue());

    public void Apply(WindowHandle window, WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (!IsAlive(window)) return;

        var hwnd = window.ToPlatformValue();

        if (!placement.Visible)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            return;
        }

        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

        var embedded = placement.Mode == WindowPlacementMode.EmbeddedChild;
        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                    (embedded ? NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOCOPYBITS : 0);

        // A floating window is kept above the shell it stands in for; an embedded child is clipped
        // by its parent and needs no z-order games.
        var insertAfter = embedded ? nint.Zero : NativeMethods.HWND_TOP;
        var bounds = placement.Bounds;

        if (embedded && placement.IsFirstPlacement &&
            bounds.Width > MinimumNudgeExtent && bounds.Height > MinimumNudgeExtent)
        {
            NativeMethods.SetWindowPos(hwnd, insertAfter, bounds.X, bounds.Y,
                bounds.Width - 1, bounds.Height - 1, flags);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNA);
        }

        NativeMethods.SetWindowPos(hwnd, insertAfter, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
        NativeMethods.RedrawWindow(hwnd, nint.Zero, nint.Zero, NativeMethods.RDW_FULL);
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

        internal const int SW_HIDE = 0;
        internal const int SW_SHOWNA = 8;
        internal const int SW_RESTORE = 9;

        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint SWP_NOCOPYBITS = 0x0100;

        internal static readonly nint HWND_TOP = nint.Zero;

        private const uint RDW_INVALIDATE = 0x0001;
        private const uint RDW_ERASE = 0x0004;
        private const uint RDW_ALLCHILDREN = 0x0080;
        private const uint RDW_UPDATENOW = 0x0100;
        private const uint RDW_FRAME = 0x0400;
        internal const uint RDW_FULL = RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW;

        [DllImport(User32)] internal static extern bool IsWindow(nint hwnd);
        [DllImport(User32)] internal static extern bool IsIconic(nint hwnd);
        [DllImport(User32)] internal static extern bool ShowWindow(nint hwnd, int command);
        [DllImport(User32)] internal static extern bool RedrawWindow(nint hwnd, nint rect, nint region, uint flags);

        [DllImport(User32, SetLastError = true)]
        internal static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
