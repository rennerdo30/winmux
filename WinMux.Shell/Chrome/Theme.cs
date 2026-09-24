using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using WinMux.Core.Settings;

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

    /// <summary>True while the chrome is painted dark. Panes are dark either way; chrome is not.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>
    /// What the user asked for. <see cref="ThemePreference.System"/> follows Windows and changes
    /// with it; the other two pin the scheme and ignore the platform.
    /// </summary>
    public static ThemePreference Preference { get; set; } = ThemePreference.System;

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
    /// Fluent 2's type ramp, which is not a matter of taste.
    ///
    /// Windows 11 sets body text at 14px and captions at 12. The chrome was built at 12 and 11 —
    /// one step small throughout — and undersized dense text is the loudest signal that something
    /// is an older application wearing a dark theme, louder than any colour.
    /// </summary>
    public const double CaptionSize = 12;
    public const double BodySize = 14;
    public const double SubtitleSize = 20;

    /// <summary>
    /// WinUI's minimum control height. A 14px label in a 26px button is the other half of looking
    /// cramped: Fluent's controls are deliberately touch-sized even on a desktop.
    /// </summary>
    public const double ControlHeight = 32;

    /// <summary>Fluent's spacing rhythm. Gaps below 8 read as a dense tool, not as Windows.</summary>
    public const double GapSmall = 8;
    public const double GapMedium = 12;
    public const double GapLarge = 20;

    /// <summary>Standard button padding for a 14px label at <see cref="ControlHeight"/>.</summary>
    public static readonly Thickness ButtonPadding = new(12, 5, 12, 6);

    /// <summary>
    /// The caption row, at the height Windows 11 gives a title bar that also holds controls.
    /// A bare caption is 32; Explorer and Terminal both run taller because things live in it.
    /// </summary>
    public const double CaptionHeight = 40;

    /// <summary>A caption button's width. 46 is the system metric, and muscle memory depends on it.</summary>
    public const double CaptionButtonWidth = 46;

    /// <summary>
    /// Close-on-hover red. Not themed, and not ours to choose: this exact pair is what every
    /// Windows title bar does, and a close button that lights up any other colour is wrong in a
    /// way people notice without being able to say why.
    /// </summary>
    public static readonly SolidColorBrush CaptionCloseHoverBrush = new(Color.FromRgb(0xC4, 0x2B, 0x1C));
    public static readonly SolidColorBrush CaptionClosePressedBrush = new(Color.FromRgb(0xB1, 0x27, 0x19));
    public static readonly SolidColorBrush CaptionCloseTextBrush = new(Colors.White);

    /// <summary>
    /// Repaint every brush for the system's current theme and accent. Safe to call again whenever
    /// Windows reports a change.
    /// </summary>
    public static void Apply(PlatformColorValues values)
    {
        IsDark = Preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => values.ThemeVariant == PlatformThemeVariant.Dark,
        };
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
    public const string DialogButton = "dialog-button";

    /// <summary>A button that destroys something. Ordinary until the pointer is on it.</summary>
    public const string DangerButton = "danger-button";
    public const string CaptionButton = "caption-button";
    public const string CaptionClose = "caption-close";

    /// <summary>
    /// Hover on a caption button, driven by the window system rather than by the pointer.
    ///
    /// Once <see cref="WinMux.Platform.ISnapLayoutService"/> claims the maximise button's
    /// rectangle, Windows stops delivering ordinary mouse input over it, so Avalonia never sets
    /// <c>:pointerover</c> there. Avalonia also refuses to let anything but the control set that
    /// pseudo-class — it throws — so the system-driven state needs a name of its own.
    /// </summary>
    public const string CaptionHover = "caption-hover";

    public static Styles Build()
    {
        var styles = new Styles();

        AddButton(styles, ToolbarButton, Palette.TextBrush, Palette.ControlRadius, new Thickness(12, 6));
        AddButton(styles, IconButton, Palette.MutedTextBrush, Palette.ControlRadius, new Thickness(10, 6));
        AddButton(styles, Tab, Palette.MutedTextBrush, new CornerRadius(6, 6, 0, 0), new Thickness(14, 7));
        AddButton(styles, CloseButton, Palette.FaintTextBrush, Palette.ControlRadius, new Thickness(6, 3));

        // The caption buttons. Square, full-height and flush to the window edge, because that is
        // what the system's own are — rounding them or insetting them is the giveaway that an app
        // has drawn its own title bar rather than merged into one.
        AddButton(styles, CaptionButton, Palette.TextBrush, new CornerRadius(0), new Thickness(0));
        foreach (var selector in new Func<Style>[]
                 {
                     () => new Style(x => x.OfType<Button>().Class(CaptionButton)),
                     () => new Style(x => x.OfType<Button>().Class(CaptionButton).Template().OfType<ContentPresenter>()),
                 })
        {
            var style = selector();
            style.Setters.Add(new Setter(Layoutable.MinHeightProperty, 0.0));
            styles.Add(style);
        }
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionButton))
        {
            Setters =
            {
                new Setter(Layoutable.WidthProperty, Palette.CaptionButtonWidth),
                new Setter(Layoutable.HeightProperty, Palette.CaptionHeight),
            },
        });

        // The same appearance as a real hover, for the button the system is hovering for us.
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionButton).Class(CaptionHover)
                                   .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.HoverBrush) },
        });

        // Close is the exception to every hover rule in this file.
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionClose).Class(":pointerover"))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.CaptionCloseTextBrush) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionClose).Class(":pointerover")
                                   .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.CaptionCloseHoverBrush) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionClose).Class(":pressed"))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.CaptionCloseTextBrush) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(CaptionClose).Class(":pressed")
                                   .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.CaptionClosePressedBrush) },
        });

        // Every dialog button, without each one restating the metrics.
        styles.Add(new Style(x => x.OfType<Button>().Class(DialogButton))
        {
            Setters =
            {
                new Setter(Layoutable.MinHeightProperty, Palette.ControlHeight),
                new Setter(Layoutable.MinWidthProperty, 100.0),
                new Setter(TemplatedControl.PaddingProperty, Palette.ButtonPadding),
                new Setter(TemplatedControl.CornerRadiusProperty, Palette.ControlRadius),
                new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
                new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
            },
        });

        // Text entry and pickers are the controls that most obviously look wrong when short.
        foreach (var type in new[] { typeof(TextBox), typeof(ComboBox) })
        {
            styles.Add(new Style(x => x.Is(type))
            {
                Setters =
                {
                    new Setter(Layoutable.MinHeightProperty, Palette.ControlHeight),
                    new Setter(TemplatedControl.CornerRadiusProperty, Palette.ControlRadius),
                    new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
                    new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
                },
            });
        }

        styles.Add(new Style(x => x.OfType<CheckBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
                new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
                new Setter(Layoutable.MinHeightProperty, Palette.ControlHeight),
            },
        });

        styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, Palette.ControlRadius),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 7)),
                new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
            },
        });

        // Windows 11 marks the selected item with a filled card, not a heavier font.
        styles.Add(new Style(x => x.OfType<Button>().Class(ActiveTab).Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.ActiveTabBrush) },
        });
        styles.Add(new Style(x => x.OfType<Button>().Class(ActiveTab))
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.TextBrush) },
        });

        // A destructive button reads as ordinary until you are about to press it, which is the
        // moment the warning is worth anything. Colouring it red at rest would make Remove the
        // loudest thing on a settings page.
        styles.Add(new Style(x => x.OfType<Button>().Class(DangerButton).Class(":pointerover")
                                   .Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Palette.DangerBrush) },
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
                new Setter(TextBlock.FontSizeProperty, Palette.BodySize),
                // Fluent pairs 14px body with a 20px line box. Without it, wrapped paragraphs in
                // the dialogs sit too close together to read as Windows text.
                new Setter(TextBlock.LineHeightProperty, 20.0),
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
                new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
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
                new Setter(TemplatedControl.FontSizeProperty, Palette.BodySize),
                new Setter(Layoutable.MinHeightProperty, Palette.ControlHeight),
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
