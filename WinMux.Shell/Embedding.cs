using System.Runtime.InteropServices;

namespace WinMux.Shell;

/// <summary>
/// Everything needed to put a reparented window back exactly as it was.
///
/// Spike 3 measured what happens when this is skipped: a host that dies while still owning an
/// embedded window **destroys that window**, leaving the application running with nothing on
/// screen. Detach is not a courtesy, it is the difference between closing WinMux and losing the
/// user's application.
/// </summary>
internal sealed record EmbedRecord(
    IntPtr Child,
    IntPtr OriginalParent,
    IntPtr OriginalStyle,
    IntPtr OriginalExStyle,
    Win32Interop.RECT OriginalRect);

internal static class Embedding
{
    /// <summary>
    /// Strip the window's frame so it can sit inside a pane without its own title bar.
    ///
    /// The reparenting itself is Avalonia's job via NativeControlHost — a child HWND parented in
    /// by hand is not composited into Avalonia's swapchain and paints nothing.
    /// </summary>
    public static void StripFrame(IntPtr child, EmbedRecord record)
    {
        if (!Win32Interop.IsWindow(child)) return;

        long style = record.OriginalStyle.ToInt64();
        style &= ~Win32Interop.WS_POPUP;
        style &= ~(long)(Win32Interop.WS_CAPTION | Win32Interop.WS_THICKFRAME | Win32Interop.WS_MINIMIZEBOX |
                         Win32Interop.WS_MAXIMIZEBOX | Win32Interop.WS_SYSMENU | Win32Interop.WS_BORDER |
                         Win32Interop.WS_DLGFRAME);
        Win32Interop.SetWindowLongPtrW(child, Win32Interop.GWL_STYLE, new IntPtr(style));

        Win32Interop.SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOMOVE |
            Win32Interop.SWP_NOSIZE | Win32Interop.SWP_FRAMECHANGED);
    }

    /// <summary>Undo an embed exactly: parent, style, ex-style and rect all restored.</summary>
    public static void Detach(EmbedRecord record)
    {
        if (!Win32Interop.IsWindow(record.Child)) return;

        Win32Interop.SetParent(record.Child, record.OriginalParent);
        Win32Interop.SetWindowLongPtrW(record.Child, Win32Interop.GWL_STYLE, record.OriginalStyle);
        Win32Interop.SetWindowLongPtrW(record.Child, Win32Interop.GWL_EXSTYLE, record.OriginalExStyle);

        var r = record.OriginalRect;
        Win32Interop.SetWindowPos(record.Child, IntPtr.Zero, r.left, r.top,
            r.right - r.left, r.bottom - r.top,
            Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE |
            Win32Interop.SWP_FRAMECHANGED | Win32Interop.SWP_SHOWWINDOW);
    }
}
