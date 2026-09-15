using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Claims a rectangle of the window as its maximise button, so Windows 11 offers snap layouts.
///
/// The flyout is driven entirely by <c>WM_NCHITTEST</c> returning <c>HTMAXBUTTON</c>. There is no
/// API to ask for it; the only way to be offered it is to answer that question the way a window
/// with a system caption would. So this subclasses the window procedure and answers.
///
/// Three consequences follow from claiming it, and all three have to be handled or the caption
/// button stops working:
///
/// * Windows stops sending ordinary mouse messages over that area and sends non-client ones
///   instead, so the app's own click handler never fires. <c>WM_NCLBUTTONUP</c> is where the click
///   now arrives.
/// * <c>WM_NCLBUTTONDOWN</c> must be swallowed. Left to the default handler it begins a caption
///   drag, and the window moves when you press the maximise button.
/// * Hover is now the system's to report, through <c>WM_NCMOUSEMOVE</c> and
///   <c>WM_NCMOUSELEAVE</c>, so the app is told and draws its own hover state.
///
/// Subclassing is additive and reversible: the previous procedure is kept and restored on dispose,
/// and every message this does not claim is passed straight to it.
/// </summary>
public sealed class Win32SnapLayoutService : ISnapLayoutService
{
    public IDisposable? Track(WindowHandle window, MaximizeButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (window.IsNone) return null;

        var handle = window.ToPlatformValue();
        return NativeMethods.IsWindow(handle) ? new Subclass(handle, button) : null;
    }

    private sealed class Subclass : IDisposable
    {
        private const int GwlpWndProc = -4;

        private const uint WmNcHitTest = 0x0084;
        private const uint WmNcMouseMove = 0x00A0;
        private const uint WmNcMouseLeave = 0x02A2;
        private const uint WmNcLButtonDown = 0x00A1;
        private const uint WmNcLButtonUp = 0x00A2;
        private const uint WmDestroy = 0x0002;

        private const nint HtMaxButton = 9;

        private readonly nint _window;
        private readonly MaximizeButton _button;
        private readonly NativeMethods.WndProc _proc;
        private readonly nint _previous;

        private bool _hovered;
        private bool _released;

        public Subclass(nint window, MaximizeButton button)
        {
            _window = window;
            _button = button;
            _proc = Handle;
            _previous = NativeMethods.SetWindowLongPtr(
                window, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        }

        private nint Handle(nint window, uint message, nint wParam, nint lParam)
        {
            switch (message)
            {
                case WmNcHitTest when IsOverButton(lParam):
                    SetHover(true);
                    return HtMaxButton;

                case WmNcMouseMove when wParam != HtMaxButton:
                case WmNcMouseLeave:
                    SetHover(false);
                    break;

                // Swallowed: the default handler would start a caption drag, and the window would
                // move when the maximise button is pressed.
                case WmNcLButtonDown when wParam == HtMaxButton:
                    return 0;

                case WmNcLButtonUp when wParam == HtMaxButton:
                    SetHover(false);
                    _button.Invoke();
                    return 0;

                case WmDestroy:
                    Restore();
                    break;
            }

            return NativeMethods.CallWindowProc(_previous, window, message, wParam, lParam);
        }

        /// <summary>
        /// Is the pointer over the button? <paramref name="lParam"/> carries screen coordinates,
        /// and the button's rectangle is in client ones.
        /// </summary>
        private bool IsOverButton(nint lParam)
        {
            if (_button.Bounds() is not { } bounds) return false;

            var point = new NativeMethods.Point
            {
                X = unchecked((short)(lParam & 0xFFFF)),
                Y = unchecked((short)((lParam >> 16) & 0xFFFF)),
            };

            return NativeMethods.ScreenToClient(_window, ref point) && bounds.Contains(point.X, point.Y);
        }

        private void SetHover(bool hovered)
        {
            if (_hovered == hovered) return;
            _hovered = hovered;
            _button.HoverChanged(hovered);
        }

        public void Dispose() => Restore();

        private void Restore()
        {
            if (_released) return;
            _released = true;

            if (NativeMethods.IsWindow(_window))
                NativeMethods.SetWindowLongPtr(_window, GwlpWndProc, _previous);

            // The delegate must outlive the last message the window will send through it.
            GC.KeepAlive(_proc);
        }
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

        internal delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport(User32)] internal static extern bool IsWindow(nint hwnd);

        [DllImport(User32, EntryPoint = "SetWindowLongPtrW")]
        internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

        [DllImport(User32, EntryPoint = "CallWindowProcW")]
        internal static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);

        [DllImport(User32)] internal static extern bool ScreenToClient(nint hwnd, ref Point point);
    }
}
