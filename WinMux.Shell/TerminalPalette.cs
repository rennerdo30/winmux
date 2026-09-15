using Avalonia.Media;
using WinMux.Terminal;

namespace WinMux.Shell;

/// <summary>
/// What a <see cref="TerminalColor"/> actually looks like.
///
/// The renderer used to draw every row with one hardcoded foreground, so every colour and
/// attribute the VT engine had already parsed was thrown away on the way to the screen: a coloured
/// prompt, `git status`, `ls --color` and every TUI rendered monochrome. The engine was never the
/// problem — `TerminalCell` has carried `Foreground`, `Background` and `Attributes` since Phase 1.
///
/// The scheme is **Campbell**, which is what Windows Terminal ships as its default. The previous
/// colours were Nord, chosen by nobody for any reason, and a terminal that does not match the
/// system's own terminal is a terminal that looks like a foreign application.
/// </summary>
internal static class TerminalPalette
{
    /// <summary>Campbell's foreground and background.</summary>
    public static readonly Color DefaultForeground = Color.FromRgb(0xCC, 0xCC, 0xCC);

    public static readonly Color DefaultBackground = Color.FromRgb(0x0C, 0x0C, 0x0C);

    /// <summary>The sixteen ANSI colours: eight normal, then eight bright.</summary>
    private static readonly Color[] Ansi =
    [
        Color.FromRgb(0x0C, 0x0C, 0x0C), // black
        Color.FromRgb(0xC5, 0x0F, 0x1F), // red
        Color.FromRgb(0x13, 0xA1, 0x0E), // green
        Color.FromRgb(0xC1, 0x9C, 0x00), // yellow
        Color.FromRgb(0x00, 0x37, 0xDA), // blue
        Color.FromRgb(0x88, 0x17, 0x98), // magenta
        Color.FromRgb(0x3A, 0x96, 0xDD), // cyan
        Color.FromRgb(0xCC, 0xCC, 0xCC), // white
        Color.FromRgb(0x76, 0x76, 0x76), // bright black
        Color.FromRgb(0xE7, 0x48, 0x56), // bright red
        Color.FromRgb(0x16, 0xC6, 0x0C), // bright green
        Color.FromRgb(0xF9, 0xF1, 0xA5), // bright yellow
        Color.FromRgb(0x3B, 0x78, 0xFF), // bright blue
        Color.FromRgb(0xB4, 0x00, 0x9E), // bright magenta
        Color.FromRgb(0x61, 0xD6, 0xD6), // bright cyan
        Color.FromRgb(0xF2, 0xF2, 0xF2), // bright white
    ];

    private static readonly Color[] Table = BuildTable();

    /// <summary>
    /// The colour for one cell attribute, or the default when the cell asked for the theme's.
    /// </summary>
    public static Color Resolve(TerminalColor color, bool isBackground) => color.Kind switch
    {
        TerminalColorKind.Rgb => Color.FromRgb(color.Red, color.Green, color.Blue),
        TerminalColorKind.Palette => Table[color.PaletteIndex],
        _ => isBackground ? DefaultBackground : DefaultForeground,
    };

    /// <summary>
    /// Faint text, as a blend toward the background rather than as transparency.
    ///
    /// Alpha would composite against whatever is behind the pane — with Mica, the wallpaper — so
    /// faint text over a dark background would go light instead of dim.
    /// </summary>
    public static Color Faint(Color foreground, Color background) => Color.FromRgb(
        (byte)((foreground.R + background.R * 2) / 3),
        (byte)((foreground.G + background.G * 2) / 3),
        (byte)((foreground.B + background.B * 2) / 3));

    /// <summary>xterm's 256: sixteen ANSI, a 6×6×6 cube, then twenty-four greys.</summary>
    private static Color[] BuildTable()
    {
        var table = new Color[256];
        Ansi.CopyTo(table, 0);

        // The cube's levels are not evenly spaced: 0 then 95, and 40 apart after that.
        ReadOnlySpan<byte> levels = [0, 95, 135, 175, 215, 255];
        var index = 16;
        foreach (var r in levels)
        {
            foreach (var g in levels)
            {
                foreach (var b in levels)
                {
                    table[index++] = Color.FromRgb(r, g, b);
                }
            }
        }

        for (var step = 0; step < 24; step++)
        {
            var value = (byte)(8 + step * 10);
            table[index++] = Color.FromRgb(value, value, value);
        }

        return table;
    }
}
