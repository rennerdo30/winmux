using WinMux.Platform;
using WinMux.Platform.Win32.Windows;

namespace WinMux.Shell;

/// <summary>
/// The composition root for the platform layer: the only place in <c>WinMux.Shell</c> that names a
/// concrete operating system.
///
/// Phase 5 moved every Win32 call the shell used to make behind <c>WinMux.Platform</c>. The point of
/// that is not tidiness — it is that the shell's own code can now be read, and tested, without
/// knowing what Windows demands. That property is only true while this file stays the single seam.
/// A second `using WinMux.Platform.Win32.Windows` anywhere in the shell is the boundary rotting, and
/// <c>PlatformBoundaryTests</c> says so out loud. (The shell does still read the quirks *data* in
/// <c>WinMux.Platform.Win32.ForeignApps</c>; that is a rules table, not an OS call, and ADR 0011
/// already put the shell in charge of resolving it.)
/// </summary>
internal static class PlatformServices
{
    /// <summary>Positioning and lifetime for windows the shell hosts but does not own.</summary>
    public static IHostWindowService HostWindows { get; } = new Win32HostWindowService();

    /// <summary>Process facts the working-directory strategy needs (CLAUDE.md section 4).</summary>
    public static IProcessInspector Processes { get; } = new Win32ProcessInspector();

    /// <summary>Saying something when startup fails before a window exists.</summary>
    public static IUserNotifier Notifier { get; } = new Win32UserNotifier();

    /// <summary>The applications this machine has, for the profile and pane launchers.</summary>
    public static IAppCatalog Apps { get; } = new Win32AppCatalog();

    /// <summary>The windows already open, for attaching one into a pane.</summary>
    public static IWindowCatalog Windows { get; } = new Win32WindowCatalog();

    /// <summary>Being told when the user focuses something the shell did not focus itself.</summary>
    public static IForegroundWindowMonitor Foreground { get; } = new Win32ForegroundWindowMonitor();

    /// <summary>Claiming our own maximise button, so Windows 11 still offers snap layouts.</summary>
    public static ISnapLayoutService SnapLayouts { get; } = new Win32SnapLayoutService();

    /// <summary>Somewhere to keep a password that is not a file WinMux owns.</summary>
    public static ICredentialStore Credentials { get; } = new Win32CredentialStore();

    /// <summary>Deleting a file the way the user can undo it.</summary>
    public static IFileTrash Trash { get; } = new Win32FileTrash();

    /// <summary>The picture an application shows for itself, for the pickers that list them.</summary>
    public static IAppIconSource AppIcons { get; } = new Win32AppIconSource();

    /// <summary>File and folder icons for the file browser, local or remote.</summary>
    public static IFileIconSource FileIcons { get; } = new Win32FileIconSource();

    /// <summary>Where PuTTY and WinSCP keep their saved sessions (ADR 0025).</summary>
    public static WinMux.Connections.IRegistryStore Registry { get; } = new Win32RegistryStore();

    /// <summary>Undoing DPAPI, which is how Remote Desktop Connection Manager stores a password.</summary>
    public static WinMux.Connections.Unprotect Unprotect { get; } = Win32SecretUnprotector.Unprotect;

    private static readonly Lazy<IDesktopNotifier> LazyNotifications = new(() => new Win32DesktopNotifier());

    /// <summary>
    /// Desktop notifications, for a terminal program asking for attention. Created on first use —
    /// it runs a thread of its own — so a session that never notifies never starts one.
    /// </summary>
    public static IDesktopNotifier Notifications => LazyNotifications.Value;

    /// <summary>Withdraw any outstanding notification, without starting the notifier to do it.</summary>
    public static void ClearNotifications()
    {
        if (LazyNotifications.IsValueCreated) LazyNotifications.Value.Clear();
    }

    /// <summary>Withdraw the notification icon on exit, if one was ever needed.</summary>
    public static void ShutDownNotifications()
    {
        if (LazyNotifications.IsValueCreated) LazyNotifications.Value.Dispose();
    }
}
