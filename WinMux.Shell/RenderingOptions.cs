using Avalonia;
using WinMux.Core.Settings;

namespace WinMux.Shell;

/// <summary>Use Avalonia's GPU backend without making hardware a startup requirement.</summary>
internal static class RenderingOptions
{
    public static Win32PlatformOptions Create(RenderingPreference preference) => new()
    {
        RenderingMode = preference == RenderingPreference.Software
            ? [Win32RenderingMode.Software]
            : [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
    };
}
