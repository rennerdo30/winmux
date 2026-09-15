using Avalonia.Controls;
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

    /// <summary>Give a window the app icon, if it could be loaded.</summary>
    public static void Apply(Window window)
    {
        if (Loaded.Value is { } icon) window.Icon = icon;
    }
}
