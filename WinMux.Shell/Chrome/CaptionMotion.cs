using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;

namespace WinMux.Shell.Chrome;

/// <summary>Respect Windows' client-area animation switch for tab motion.</summary>
internal static class CaptionMotion
{
    public static bool Enabled => PlatformServices.Motion.ClientAreaAnimationEnabled;

    public static Transitions IndicatorTransitions() => Enabled
        ? [
            new ThicknessTransition { Property = Layoutable.MarginProperty, Duration = TimeSpan.FromMilliseconds(167) },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(83) },
        ]
        : [];

    public static Transitions HoverTransitions(int milliseconds) => Enabled
        ? [new BrushTransition
        {
            Property = ContentPresenter.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        }]
        : [];
}
