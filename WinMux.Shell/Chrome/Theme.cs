using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The shell's colours, taken from Windows rather than invented.
///
/// An app looks foreign on Windows 11 for three reasons before anyone examines a single control:
/// it ignores the light/dark setting, it picks its own accent instead of the user's, and it paints
/// a flat opaque background where the system would show Mica. All three are read from
/// <see cref="PlatformColorValues"/> here.
///
/// The brushes are deliberately **mutable singletons**. Styles and controls capture a brush
/// reference once, so swapping the objects on a theme change would leave every existing control
/// painted in the old scheme; mutating <see cref="SolidColorBrush.Color"/> in place updates
/// everything already on screen. That is why nothing in this file is frozen.
/// </summary>
internal static class Palette
{
    public static readonly SolidColorBrush WindowBrush = new();
    public static readonly SolidColorBrush SurfaceBrush = new();
    public static readonly SolidColorBrush RaisedBrush = new();
    public static readonly SolidColorBrush HoverBrush = new();
    public static readonly SolidColorBrush PressedBrush = new();
    public static readonly SolidColorBrush ActiveTabBrush = new();
    public static readonly SolidColorBrush EdgeBrush = new();
    public static readonly SolidColorBrush AccentBrush = new();
    public static readonly SolidColorBrush AccentMutedBrush = new();
    public static readonly SolidColorBrush DangerBrush = new();
    public static readonly SolidColorBrush TextBrush = new();
    public static readonly SolidColorBrush MutedTextBrush = new();
    public static readonly SolidColorBrush FaintTextBrush = new();

    /// <summary>True while the system is in dark mode. Panes are dark either way; chrome is not.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>
    /// Windows 11's UI face. "Variable Text" is the optical size meant for body copy; falling back
    /// through plain Segoe UI covers Windows 10 and anything non-Windows.
    /// </summary>
    public static readonly FontFamily UiFont =
        new("Segoe UI Variable Text, Segoe UI, Inter, system-ui, sans-serif");

    /// <summary>Windows 11 rounds controls at 4 and surfaces at 8.</summary>
    public static readonly CornerRadius ControlRadius = new(4);
    public static readonly CornerRadius SurfaceRadius = new(8);
    public static readonly CornerRadius PillRadius = new(999);

    /// <summary>
    /// Repaint every brush for the system's current theme and accent. Safe to call again whenever
    /// Windows reports a change.
    /// </summary>
    public static void Apply(PlatformColorValues values)
    {
        IsDark = values.ThemeVariant == PlatformThemeVariant.Dark;
        var accent = values.AccentColor1;

        if (IsDark)
        {
            // Alpha below 0xFF on the window and surfaces is what lets Mica through. Windows 11's
            // own dark chrome is a near-black wash over the desktop backdrop, not a solid grey.
            Set(WindowBrush, 0xD8, 0x1C, 0x1C, 0x1C);
            Set(SurfaceBrush, 0xC4, 0x24, 0x24, 0x24);
            Set(RaisedBrush, 0xCC, 0x2B, 0x2B, 0x2B);
            Set(HoverBrush, 0x1A, 0xFF, 0xFF, 0xFF);
            Set(PressedBrush, 0x0F, 0xFF, 0xFF, 0xFF);
            Set(ActiveTabBrush, 0xFF, 0x3A, 0x3A, 0x3A);
            Set(EdgeBrush, 0x24, 0xFF, 0xFF, 0xFF);
            Set(TextBrush, 0xFF, 0xF2, 0xF2, 0xF2);
            Set(MutedTextBrush, 0xC8, 0xF2, 0xF2, 0xF2);
            Set(FaintTextBrush, 0x8A, 0xF2, 0xF2, 0xF2);
            Set(DangerBrush, 0xFF, 0xFF, 0x99, 0xA4);
            AccentBrush.Color = Lighten(accent, 0.35);
        }
        else
        {
            Set(WindowBrush, 0xD8, 0xF3, 0xF3, 0xF3);
            Set(SurfaceBrush, 0xC4, 0xFB, 0xFB, 0xFB);
            Set(RaisedBrush, 0xCC, 0xFF, 0xFF, 0xFF);
            Set(HoverBrush, 0x0F, 0x00, 0x00, 0x00);
            Set(PressedBrush, 0x1A, 0x00, 0x00, 0x00);
            Set(ActiveTabBrush, 0xFF, 0xFF, 0xFF, 0xFF);
            Set(EdgeBrush, 0x18, 0x00, 0x00, 0x00);
            Set(TextBrush, 0xFF, 0x1A, 0x1A, 0x1A);
            Set(MutedTextBrush, 0xC8, 0x1A, 0x1A, 0x1A);
            Set(FaintTextBrush, 0x96, 0x1A, 0x1A, 0x1A);
            Set(DangerBrush, 0xFF, 0xC4, 0x2B, 0x1A);
            AccentBrush.Color = Darken(accent, 0.18);
        }

        AccentMutedBrush.Color = Color.FromArgb(0x66, AccentBrush.Color.R, AccentBrush.Color.G, AccentBrush.Color.B);

        DialogBrush.Color = IsDark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Color.FromRgb(0xF9, 0xF9, 0xF9);
        DialogFooterBrush.Color = IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF0, 0xF0, 0xF0);
    }

    /// <summary>The fallback Windows shows behind Mica while it is unavailable or being set up.</summary>
    public static Color OpaqueWindowFallback => IsDark
        ? Color.FromRgb(0x1C, 0x1C, 0x1C)
        : Color.FromRgb(0xF3, 0xF3, 0xF3);

    /// <summary>
    /// Opaque surfaces, for dialogs. The chrome brushes carry alpha so Mica shows through the main
    /// window; a dialog has no backdrop of its own, so the same brush would let the panes behind it
    /// read straight through the text.
    /// </summary>
    public static readonly SolidColorBrush DialogBrush = new();
    public static readonly SolidColorBrush DialogFooterBrush = new();

    private static void Set(SolidColorBrush brush, byte a, byte r, byte g, byte b) =>
        brush.Color = Color.FromArgb(a, r, g, b);

    private static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount),
        (byte)(c.G + (255 - c.G) * amount),
        (byte)(c.B + (255 - c.B) * amount));

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)),
        (byte)(c.G * (1 - amount)),
        (byte)(c.B * (1 - amount)));
}

/// <summary>
/// The chrome's styles, applied once at startup on top of Fluent.
///
/// Written as styles rather than properties set on each control for a reason that is not tidiness:
/// setting <c>Background</c> directly on a Button *replaces* the value Fluent's template animates,
/// so the control silently loses its hover and pressed feedback. A toolbar whose buttons do not
/// react to the pointer reads as broken no matter how good the colours are.
/// </summary>
internal static class Theme
{
    public const string ToolbarButton = "toolbar-button";
    public const string IconButton = "icon-button";
    public const string Tab = "tab";
    public const string ActiveTab = "active-tab";
    public const string CloseButton = "close-button";

    public static Styles Build()
    {
        var styles = new Styles();

        AddButton(styles, ToolbarButton, Palette.TextBrush, Palette.ControlRadius, new Thickness(10, 6));
        AddButton(styles, IconButton, Palette.MutedTextBrush, Palette.ControlRadius, new Thickness(8, 6));
        AddButton(styles, Tab, Palette.MutedTextBrush, Palette.ControlRadius, new Thickness(11, 5));
        AddButton(styles, CloseButton, Palette.FaintTextBrush, Palette.ControlRadius, new Thickness(5, 2));

        // Windows 11 marks the selected item with a filled card, not a heavier font.
        styles.Add(new Style(x => x.OfType<Button>().Class(ActiveTab).Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.ActiveTabBrush) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(ActiveTab))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.TextBrush) },
        });

        // Closing is the one destructive thing on a tab, so it says so on hover.
        styles.Add(new Style(x => x.OfType<Button>().Class(CloseButton).Class(":pointerover"))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.DangerBrush) },
        });

        styles.Add(new Style(x => x.OfType<TextBlock>())
        {
            Setters =
            {
                new Setter(TextBlock.FontFamilyProperty, Palette.UiFont),
                new Setter(TextBlock.FontSizeProperty, 12.0),
            },
        });

        // Fluent's menu is a system-styled popup already; what it needs from us is the rounding and
        // the type face, so it matches the toolbar it drops out of.
        styles.Add(new Style(x => x.OfType<ContextMenu>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, Palette.SurfaceRadius),
                new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
                new Setter(TemplatedControl.FontSizeProperty, 12.0),
            },
        });

        return styles;
    }

    private static void AddButton(
        Styles styles,
        string className,
        IBrush foreground,
        CornerRadius radius,
        Thickness padding)
    {
        styles.Add(new Style(x => x.OfType<Button>().Class(className))
        {
            Setters =
            {
                new Setter(TemplatedControl.ForegroundProperty, foreground),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, radius),
                new Setter(TemplatedControl.PaddingProperty, padding),
                new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
                new Setter(TemplatedControl.FontSizeProperty, 12.0),
            },
        });

        // Fluent paints the button's fill on the templated ContentPresenter, so that is where a
        // hover override has to land — a setter on the Button itself is overwritten by the template.
        styles.Add(new Style(x => x.OfType<Button>().Class(className).Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.CornerRadiusProperty, radius) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(className).Class(":pointerover")
                                   .Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Palette.HoverBrush),
                new Setter(ContentPresenter.CornerRadiusProperty, radius),
            },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(className).Class(":pressed")
                                   .Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Palette.PressedBrush),
                new Setter(ContentPresenter.CornerRadiusProperty, radius),
            },
        });
    }
}
