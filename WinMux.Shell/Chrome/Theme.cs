using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace WinMux.Shell.Chrome;

/// <summary>
/// One place for every colour, radius and font the shell chrome uses.
///
/// Scattered literals were how this started, and they drift: three different greys for the same
/// surface, an accent that exists twice with different blues. A named token is also the only way a
/// light theme, or a user theme, becomes possible later without hunting through control code.
/// </summary>
internal static class Palette
{
    // Surfaces, darkest first. The window sits below the panes so splits read as separated cards.
    public static readonly Color Window = Color.FromRgb(0x11, 0x12, 0x1b);
    public static readonly Color Surface = Color.FromRgb(0x18, 0x19, 0x25);
    public static readonly Color Raised = Color.FromRgb(0x1e, 0x20, 0x2e);
    public static readonly Color Hover = Color.FromRgb(0x2a, 0x2d, 0x3e);
    public static readonly Color Pressed = Color.FromRgb(0x33, 0x36, 0x49);
    public static readonly Color ActiveTab = Color.FromRgb(0x2b, 0x2e, 0x41);

    public static readonly Color Edge = Color.FromRgb(0x2c, 0x2f, 0x40);
    public static readonly Color Accent = Color.FromRgb(0x82, 0xaa, 0xff);
    public static readonly Color Danger = Color.FromRgb(0xf3, 0x8b, 0xa8);

    public static readonly Color Text = Color.FromRgb(0xe4, 0xe7, 0xf2);
    public static readonly Color MutedText = Color.FromRgb(0x96, 0x9d, 0xb5);
    public static readonly Color FaintText = Color.FromRgb(0x6b, 0x71, 0x87);

    public static readonly IBrush WindowBrush = New(Window);
    public static readonly IBrush SurfaceBrush = New(Surface);
    public static readonly IBrush RaisedBrush = New(Raised);
    public static readonly IBrush HoverBrush = New(Hover);
    public static readonly IBrush PressedBrush = New(Pressed);
    public static readonly IBrush ActiveTabBrush = New(ActiveTab);
    public static readonly IBrush EdgeBrush = New(Edge);
    public static readonly IBrush AccentBrush = New(Accent);
    public static readonly IBrush DangerBrush = New(Danger);
    public static readonly IBrush TextBrush = New(Text);
    public static readonly IBrush MutedTextBrush = New(MutedText);
    public static readonly IBrush FaintTextBrush = New(FaintText);

    /// <summary>Windows 11's UI face, with fallbacks for anything older or non-Windows.</summary>
    public static readonly FontFamily UiFont =
        new("Segoe UI Variable Text, Segoe UI, Inter, system-ui, sans-serif");

    public static readonly CornerRadius ControlRadius = new(6);
    public static readonly CornerRadius PillRadius = new(999);

    /// <summary>Frozen so the same brush can be shared across every control without copying.</summary>
    private static IBrush New(Color color) => new SolidColorBrush(color).ToImmutable();
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

        // Toolbar and strip buttons: flat until the pointer arrives.
        AddButton(styles, ToolbarButton, Palette.TextBrush, Palette.ControlRadius, new Thickness(10, 5));
        AddButton(styles, IconButton, Palette.MutedTextBrush, Palette.ControlRadius, new Thickness(9, 5));
        AddButton(styles, Tab, Palette.MutedTextBrush, new CornerRadius(6, 6, 0, 0), new Thickness(10, 4));
        AddButton(styles, CloseButton, Palette.FaintTextBrush, Palette.PillRadius, new Thickness(5, 1));

        // The active tab is filled and reads in full-strength text.
        styles.Add(new Style(x => x.OfType<Button>().Class(ActiveTab).Template().OfType<ContentPresenter>())
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, Palette.ActiveTabBrush),
            },
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
            Setters = { new Setter(TextBlock.FontFamilyProperty, Palette.UiFont) },
        });

        // Fluent's default menu is light; a bright popup out of a dark toolbar is jarring.
        styles.Add(new Style(x => x.OfType<ContextMenu>())
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Palette.RaisedBrush),
                new Setter(TemplatedControl.BorderBrushProperty, Palette.EdgeBrush),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)),
                new Setter(TemplatedControl.CornerRadiusProperty, Palette.ControlRadius),
                new Setter(TemplatedControl.FontFamilyProperty, Palette.UiFont),
            },
        });
        styles.Add(new Style(x => x.OfType<MenuItem>())
        {
            Setters = { new Setter(TemplatedControl.ForegroundProperty, Palette.TextBrush) },
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
                new Setter(TemplatedControl.FontSizeProperty, 12.5),
            },
        });

        // Fluent paints the button's fill on the templated ContentPresenter, so that is where a
        // hover override has to land — a setter on the Button itself is overwritten by the template.
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
        styles.Add(new Style(x => x.OfType<Button>().Class(className).Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.CornerRadiusProperty, radius) },
        });
    }
}
