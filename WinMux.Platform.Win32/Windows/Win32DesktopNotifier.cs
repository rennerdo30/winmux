using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// Desktop notifications through the notification area — <c>Shell_NotifyIcon</c> with a balloon,
/// which Windows 10 and 11 show as an ordinary toast and keep in the notification centre.
///
/// <para>
/// The modern route is <c>Windows.UI.Notifications</c>. It needs the Windows SDK projection, and
/// therefore an SDK-versioned target framework on this project and everything that references it,
/// and for an unpackaged application a registered AppUserModelID and a COM activator before a click
/// can be delivered. The balloon needs none of that, respects Focus Assist through
/// <c>NIIF_RESPECT_QUIET_TIME</c>, and reports a click as a window message. Its one cost is an icon
/// in the notification area while a notification is outstanding, which <see cref="Clear"/> removes.
/// </para>
///
/// <para>
/// It runs on a thread of its own with a hidden window and a message loop, so it depends on no UI
/// framework and a slow shell call can never stall the application's UI thread.
/// </para>
/// </summary>
public sealed class Win32DesktopNotifier : IDesktopNotifier
{
    private const uint CallbackMessage = NativeMethods.WmApp + 1;
    private const uint WorkMessage = NativeMethods.WmApp + 2;
    private const uint IconId = 1;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly NativeMethods.WndProc _procedure;
    private nint _window;
    private nint _smallIcon;
    private nint _largeIcon;
    private bool _iconAdded;
    private Action? _activated;
    private int _disposed;

    public Win32DesktopNotifier()
    {
        _procedure = WindowProcedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "WinMux notifications" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    public bool IsAvailable => OperatingSystem.IsWindows() && _window != 0;

    /// <summary>
    /// The system-wide switch in Settings › System › Notifications. With it off Windows shows no
    /// toast from anyone, and nothing tells the application — found when a notification that was
    /// demonstrably sent never appeared. Read each time, because the user can flip it at any moment.
    /// </summary>
    public string? BlockedReason
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\PushNotifications");
                return key?.GetValue("ToastEnabled") is int enabled && enabled == 0
                    ? "Windows notifications are turned off. Turn them on in Settings › System › Notifications."
                    : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The notification page of the Windows settings app. <c>UseShellExecute</c> is what makes the
    /// string a request to the shell rather than a program to run, which is what resolves the
    /// <c>ms-settings:</c> scheme.
    /// </summary>
    public void OpenSystemSettings()
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // A settings page that will not open is not worth interrupting anyone over.
        }
    }

    public void Show(string title, string message, Action? activated)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsAvailable || Volatile.Read(ref _disposed) != 0) return;

        Post(() =>
        {
            _activated = activated;
            ShowBalloon(title, message);
        });
    }

    public void Clear()
    {
        if (!IsAvailable || Volatile.Read(ref _disposed) != 0) return;
        Post(RemoveIcon);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _window == 0) return;

        // Remove the icon before the window goes: an icon whose window has gone stays in the
        // notification area until the mouse passes over it, a ghost of a program that has exited.
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() =>
        {
            RemoveIcon();
            NativeMethods.DestroyWindow(_window);
            NativeMethods.PostQuitMessage(0);
            done.Set();
        });
        NativeMethods.PostMessage(_window, WorkMessage, 0, 0);
        done.Wait(TimeSpan.FromSeconds(2));
    }

    private void Post(Action action)
    {
        _work.Enqueue(action);
        NativeMethods.PostMessage(_window, WorkMessage, 0, 0);
    }

    private void Run()
    {
        try
        {
            var instance = NativeMethods.GetModuleHandle(null);
            var className = "WinMux.Notifications." + Environment.ProcessId;
            var windowClass = new NativeMethods.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WndClassEx>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                Instance = instance,
                ClassName = className,
            };

            if (NativeMethods.RegisterClassEx(ref windowClass) == 0) return;

            // A hidden top-level window rather than a message-only one: the notification area
            // expects a real window to own its icons.
            _window = NativeMethods.CreateWindowEx(0, className, "WinMux notifications", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (_window == 0) return;

            LoadIcons();
        }
        finally
        {
            _ready.Set();
        }

        while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }
    }

    private void LoadIcons()
    {
        var large = new nint[1];
        var small = new nint[1];
        if (Environment.ProcessPath is { } path && NativeMethods.ExtractIconEx(path, 0, large, small, 1) > 0)
        {
            _largeIcon = large[0];
            _smallIcon = small[0];
        }
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WorkMessage)
        {
            while (_work.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
                {
                    // A notification that could not be shown is not worth taking the thread down.
                }
            }

            return 0;
        }

        if (message == CallbackMessage)
        {
            // NOTIFYICON_VERSION_4: the event is in the low word of lParam.
            var notification = (uint)(lParam & 0xFFFF);
            if (notification is NativeMethods.NinBalloonUserClick or NativeMethods.NinSelect
                or NativeMethods.NinKeySelect or NativeMethods.WmLButtonUp)
            {
                var activated = _activated;
                RemoveIcon();
                activated?.Invoke();
            }

            return 0;
        }

        return NativeMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowBalloon(string title, string message)
    {
        var data = NewData();
        data.Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = _smallIcon;
        data.Tip = "WinMux";

        if (!_iconAdded)
        {
            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref data)) return;
            data.TimeoutOrVersion = NativeMethods.NotifyIconVersion4;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NimSetVersion, ref data);
            _iconAdded = true;
        }

        var balloon = NewData();
        balloon.Flags = NativeMethods.NifInfo;
        balloon.InfoTitle = Truncate(title, 63);
        balloon.Info = Truncate(message.Length == 0 ? " " : message, 255);
        balloon.InfoFlags = NativeMethods.NiifUser | NativeMethods.NiifLargeIcon | NativeMethods.NiifRespectQuietTime;
        balloon.BalloonIcon = _largeIcon;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref balloon);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded) return;
        var data = NewData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref data);
        _iconAdded = false;
        _activated = null;
    }

    private NativeMethods.NotifyIconData NewData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private static string Truncate(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";

    private static class NativeMethods
    {
        internal const uint WmApp = 0x8000;
        internal const uint WmLButtonUp = 0x0202;
        internal const uint NinSelect = 0x0400;
        internal const uint NinKeySelect = 0x0401;
        internal const uint NinBalloonUserClick = 0x0405;

        internal const uint NimAdd = 0;
        internal const uint NimModify = 1;
        internal const uint NimDelete = 2;
        internal const uint NimSetVersion = 4;
        internal const uint NotifyIconVersion4 = 4;

        internal const uint NifMessage = 0x01;
        internal const uint NifIcon = 0x02;
        internal const uint NifTip = 0x04;
        internal const uint NifInfo = 0x10;
        internal const uint NifShowTip = 0x80;

        internal const uint NiifUser = 0x04;
        internal const uint NiifLargeIcon = 0x20;
        internal const uint NiifRespectQuietTime = 0x80;

        internal delegate nint WndProc(nint window, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            public uint Size;
            public nint Window;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public nint Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
            public uint State;
            public uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
            public uint TimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
            public uint InfoFlags;
            public Guid Item;
            public nint BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public nint Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public string? MenuName;
            public string ClassName;
            public nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Msg
        {
            public nint Window;
            public uint Message;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconEx(string file, int index, nint[]? large, nint[]? small, uint count);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WndClassEx windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static extern int GetMessage(out Msg message, nint window, uint min, uint max);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(ref Msg message);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandle(string? module);
    }
}
