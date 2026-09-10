using Avalonia.Controls;
using Avalonia.Platform;

namespace WinMux.Shell;

/// <summary>
/// Hosts an already-running application's window inside the layout.
///
/// This must go through <see cref="NativeControlHost"/> rather than a hand-rolled `SetParent` into
/// the shell window's HWND. Avalonia renders through a composition swapchain, and a child HWND
/// parented in by hand is *not composited into it*: the window is genuinely a child, correctly
/// sized and reported visible by Win32, and it paints nothing at all. That was measured, not
/// assumed — Character Map embedded, laid out its controls, and stayed invisible.
///
/// CLAUDE.md section 2 already said this ("Avalonia's NativeControlHost is purpose-built for
/// embedding native handles") and it is the reason Tauri/WebView2 was rejected: native child
/// windows composited over a different rendering surface fight forever.
///
/// Avalonia does the reparenting and the positioning; this class only supplies the handle and,
/// crucially, refuses to destroy it.
/// </summary>
internal sealed class ForeignHost : NativeControlHost
{
    private readonly IntPtr _hwnd;

    public ForeignHost(IntPtr hwnd) => _hwnd = hwnd;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        => _hwnd != IntPtr.Zero
            ? new PlatformHandle(_hwnd, "HWND")
            : base.CreateNativeControlCore(parent);

    /// <summary>
    /// Deliberately does nothing. The default would destroy the handle — and this handle belongs
    /// to somebody else's application. Spike 3 measured what that costs: the process survives with
    /// no window, which from the user's side is the application being lost. Detaching is
    /// <see cref="ForeignAppPane.Detach"/>'s job, and it restores the original parent and styles.
    /// </summary>
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // intentionally empty
    }
}
