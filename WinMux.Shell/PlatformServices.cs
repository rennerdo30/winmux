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
}
