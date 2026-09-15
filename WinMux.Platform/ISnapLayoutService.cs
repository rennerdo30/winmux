namespace WinMux.Platform;

/// <summary>A rectangle inside a window's client area, in physical pixels.</summary>
public readonly record struct ClientRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>What the caption button does when the system, rather than the app, is driving it.</summary>
/// <param name="Bounds">Where the button is now, or null while there is nothing to claim.</param>
/// <param name="Invoke">Called when the system reports a click on it.</param>
/// <param name="HoverChanged">Called when the pointer enters or leaves it, so the app can draw the state.</param>
public sealed record MaximizeButton(
    Func<ClientRect?> Bounds,
    Action Invoke,
    Action<bool> HoverChanged);

/// <summary>
/// Telling the window system that part of a window is its maximise button.
///
/// This exists for one feature: Windows 11 shows its snap-layout flyout when the pointer rests on a
/// window's maximise button, and it decides where that button is by asking the window. An app that
/// draws its own caption — which WinMux does, see
/// [ADR 0016](../docs/adr/0016-windows-11-chrome.md) — never gets asked, so it loses the flyout.
/// It is arguably the most recognisable Windows 11 affordance there is, more so than Mica.
///
/// Stated as intent, like everything else here: the app says "this rectangle is the maximise
/// button", not "return HTMAXBUTTON from WM_NCHITTEST". A system with no such concept implements
/// this by returning null, and the app keeps working with one affordance fewer.
///
/// **Claiming the rectangle has a cost the caller must know about.** Once the system believes that
/// area is a caption button, it stops delivering ordinary mouse input there and drives the button
/// itself — which is why <see cref="MaximizeButton.Invoke"/> and
/// <see cref="MaximizeButton.HoverChanged"/> exist. A caller that ignores them ends up with a
/// button that shows the flyout and no longer responds to being clicked.
/// </summary>
public interface ISnapLayoutService
{
    /// <summary>
    /// Claim <paramref name="button"/>'s rectangle within <paramref name="window"/>.
    /// Dispose to release it. Null when this system cannot offer it at all.
    /// </summary>
    IDisposable? Track(WindowHandle window, MaximizeButton button);
}
