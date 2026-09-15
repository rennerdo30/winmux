using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// The adoptable windows on this desktop.
///
/// The filtering is the feature. Spike 2 measured Notepad alone owning **twelve** top-level windows
/// — `GDI+ Hook Window Class`, three `IME`, two `MSCTFIME UI`, `tooltips_class32` and friends — and
/// exactly one of them is the window a person means (ADR 0003). So a window is offered only when it
/// is visible, top-level, un-owned, not a tool window, and has a real title. Everything else is
/// noise that would make the picker useless.
///
/// <c>EnumWindows</c> returns windows in z-order, which is the order a person expects: whatever
/// they were last looking at is near the top of the list.
/// </summary>
public sealed class Win32WindowCatalog : IWindowCatalog
{
    public IReadOnlyList<AdoptableWindow> List(IReadOnlyCollection<WindowHandle> exclude, out string? error)
    {
        ArgumentNullException.ThrowIfNull(exclude);
        error = null;

        var excluded = new HashSet<nint>(exclude.Select(handle => handle.ToPlatformValue()));
        var shell = NativeMethods.GetShellWindow();
        var found = new List<AdoptableWindow>();
        var ours = Environment.ProcessId;

        try
        {
            NativeMethods.EnumWindows((window, _) =>
            {
                if (window == shell || excluded.Contains(window)) return true;
                if (!NativeMethods.IsWindowVisible(window)) return true;

                // A window with an owner is a dialog or a palette belonging to something else.
                if (NativeMethods.GetWindow(window, NativeMethods.GwOwner) != nint.Zero) return true;

                var exStyle = NativeMethods.GetWindowLongPtrW(window, NativeMethods.GwlExStyle).ToInt64();
                if ((exStyle & NativeMethods.WsExToolWindow) != 0) return true;

                var title = Text(window);
                if (title.Length == 0) return true;

                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                if (processId == ours) return true;   // never offer WinMux its own windows

                var (name, path) = Describe(processId);
                found.Add(new AdoptableWindow(
                    WindowHandle.FromPlatformValue(window),
                    title,
                    name,
                    processId,
                    path));
                return true;
            }, nint.Zero);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return found;
    }

    private static string Text(nint window)
    {
        var length = NativeMethods.GetWindowTextLengthW(window);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(window, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static (string Name, string Path) Describe(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var name = process.ProcessName + ".exe";
            try
            {
                // Reading the module path needs the same elevation; an elevated app simply will not
                // say. The name is still useful, and an adopted pane without a path restores empty
                // rather than restoring something wrong.
                return (name, process.MainModule?.FileName ?? string.Empty);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return (name, string.Empty);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The process may have exited between the enumeration and here.
            return (string.Empty, string.Empty);
        }
    }

    private static class NativeMethods
    {
        private const string User32 = "user32.dll";

        internal const uint GwOwner = 4;
        internal const int GwlExStyle = -20;
        internal const long WsExToolWindow = 0x00000080;

        internal delegate bool EnumWindowsProc(nint window, nint parameter);

        [DllImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

        [DllImport(User32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint window);

        [DllImport(User32)]
        internal static extern nint GetWindow(nint window, uint command);

        [DllImport(User32)]
        internal static extern nint GetShellWindow();

        [DllImport(User32, EntryPoint = "GetWindowLongPtrW")]
        internal static extern nint GetWindowLongPtrW(nint window, int index);

        [DllImport(User32, CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLengthW(nint window);

        [DllImport(User32, CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextW(nint window, StringBuilder text, int maxCount);

        [DllImport(User32)]
        internal static extern uint GetWindowThreadProcessId(nint window, out int processId);
    }
}
