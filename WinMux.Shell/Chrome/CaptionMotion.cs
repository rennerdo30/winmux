using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;

namespace WinMux.Shell.Chrome;

/// <summary>Respect Windows' client-area animation switch for tab motion.</summary>
internal static class CaptionMotion
{
    private const uint GetClientAreaAnimation = 0x1042;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int enabled, uint flags);

    public static bool Enabled => !OperatingSystem.IsWindows() ||
        SystemParametersInfo(GetClientAreaAnimation, 0, out var enabled, 0) && enabled != 0;

    public static Transitions IndicatorTransitions() => Enabled
        ? [
            new ThicknessTransition { Property = Layoutable.MarginProperty, Duration = TimeSpan.FromMilliseconds(167) },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(83) },
        ]
        : [];

    public static Transitions HoverTransitions() => Enabled
        ? [new BrushTransition
        {
            Property = ContentPresenter.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(83),
        }]
        : [];
}
