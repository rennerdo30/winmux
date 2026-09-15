using WinMux.Core.Layout;

namespace WinMux.Platform;

/// <summary>How a hosted window relates to the shell window it is standing in.</summary>
public enum WindowPlacementMode
{
    /// <summary>
    /// A child of the shell window, positioned in the parent's client coordinates and clipped by it.
    /// </summary>
    EmbeddedChild,

    /// <summary>
    /// A separate top-level window positioned in screen coordinates to follow the pane. It does not
    /// ride the shell's z-order for free, so an implementation must keep it above its owner.
    /// </summary>
    FloatingTopLevel,
}

/// <summary>
/// What the layout wants a hosted window to look like. Intent only — never the steps to get there.
///
/// The distinction matters: Windows needs a documented pile of workarounds to make a reparented
/// window actually paint (an off-by-one resize, a hide/show cycle, SWP_NOCOPYBITS, an explicit
/// redraw). None of that belongs in a contract another windowing system has to satisfy. It states
/// the destination; the implementation owns whatever its platform demands to arrive there.
/// </summary>
/// <param name="Bounds">
/// Client coordinates for <see cref="WindowPlacementMode.EmbeddedChild"/>, screen coordinates for
/// <see cref="WindowPlacementMode.FloatingTopLevel"/>.
/// </param>
/// <param name="Mode">How the window relates to the shell window.</param>
/// <param name="Visible">False hides the window without destroying it — an inactive tab.</param>
/// <param name="IsFirstPlacement">
/// True the first time a given window is placed. Some platforms only finish adopting a window when
/// it is told about a geometry it has not seen before; this is the hint that licenses that work.
/// </param>
public sealed record WindowPlacement(
    Rect Bounds,
    WindowPlacementMode Mode,
    bool Visible,
    bool IsFirstPlacement);

/// <summary>
/// Positioning and lifetime for windows the shell hosts but does not own.
///
/// **Every method here may block for as long as the target application is wedged.** ADR 0001
/// measured a synchronous cross-process window call freezing the shell for a full six seconds, and
/// it hits floating and embedded placement alike. Callers must therefore treat this interface as
/// hostile to the UI thread: drive it from a dedicated thread, as
/// <c>WinMux.Shell.ForeignWindowTracker</c> does.
/// </summary>
public interface IHostWindowService
{
    /// <summary>False once the window is gone. Cheap, and safe to call on a dead handle.</summary>
    bool IsAlive(WindowHandle window);

    /// <summary>
    /// Make the window match <paramref name="placement"/>. Implementations should be idempotent:
    /// the caller deduplicates, but a repeat must not misbehave.
    /// </summary>
    void Apply(WindowHandle window, WindowPlacement placement);

    // Deliberately no Close/Destroy. ADR 0008 routes closing through the PaneHost protocol, because
    // the shell must never call a foreign window's HWND at all — and an unused lifetime method here
    // would be the eighteenth dead import this phase deleted, wearing a nicer name.
}
