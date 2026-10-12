using System.Runtime.InteropServices;
using WinMux.Platform;

namespace WinMux.Platform.Win32.Windows;

/// <summary>Windows' client-area animation accessibility preference.</summary>
public sealed class Win32MotionPreferences : IMotionPreferences
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int enabled, uint flags);

    // Failure falls back to no motion; the accessibility choice remains conservative.
    public bool ClientAreaAnimationEnabled =>
        SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0) && enabled != 0;
}
