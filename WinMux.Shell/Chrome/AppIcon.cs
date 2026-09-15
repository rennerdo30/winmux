using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace WinMux.Shell.Chrome;

/// <summary>
/// The application icon, for windows that want one in their title bar.
///
/// The executable carries the same .ico through <c>ApplicationIcon</c>, so the taskbar is covered
/// by Windows itself; this is what a Window shows in its own caption, which Avalonia will not
/// infer. Loaded once and shared, because every dialog wants it and decoding it per window would
/// be silly.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<WindowIcon?> Loaded = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://WinMux/Assets/winmux.ico"));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            // A missing icon is a cosmetic problem. It must never stop a window opening.
            return null;
        }
    });

    private static readonly Lazy<Bitmap?> Mark = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://WinMux/Assets/winmux-256.png"));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>
    /// The same mark as a bitmap, for the title bar, which draws an Image rather than setting a
    /// window icon. Null if it could not be decoded — the caption still works without it.
    /// </summary>
    public static Bitmap? Bitmap => Mark.Value;

    /// <summary>Give a window the app icon, if it could be loaded.</summary>
    public static void Apply(Window window)
    {
        if (Loaded.Value is { } icon) window.Icon = icon;
    }
}
