using Avalonia.Controls;
using WinMux.Core.Layout;
using WinMux.Core.Model;

namespace WinMux.Panes;

public sealed record PaneProviderContext(PaneId PaneId, string Title, RestoreDescriptor Descriptor)
{
    public static PaneProviderContext FromPane(Pane pane)
    {
        ArgumentNullException.ThrowIfNull(pane);
        return new PaneProviderContext(pane.Id, pane.Title, pane.Restore);
    }
}

public readonly record struct PaneArrangement(Rect Bounds, bool IsVisible);

public enum PaneCloseReason
{
    PaneRemoved,
    ShellShutdown,
}

/// <summary>
/// Result of a close preparation. A successfully detached foreign runtime cannot continue if a
/// sibling blocks shutdown; ordinary in-process runtimes can.
/// </summary>
public sealed record PaneCloseResult(
    bool Succeeded,
    bool CanContinueIfWindowStaysOpen,
    string? Message = null)
{
    public static PaneCloseResult Success(string? message = null) => new(true, true, message);
    public static PaneCloseResult Detached(string? message = null) => new(true, false, message);
    public static PaneCloseResult Failure(string message) => new(false, true,
        string.IsNullOrWhiteSpace(message) ? "pane close failed" : message);
}

/// <summary>
/// One running pane. The shell owns layout; providers own their runtime, view, state capture, and
/// safe lifecycle. No platform handle crosses this public boundary.
/// </summary>
public interface IPaneRuntime : IAsyncDisposable
{
    PaneId PaneId { get; }
    PaneKind Kind { get; }
    Control View { get; }
    string? StatusMessage { get; }

    /// <summary>Raised after title or restore intent changes and should be autosaved.</summary>
    event EventHandler? StateChanged;

    bool Focus();
    void Arrange(PaneArrangement arrangement);
    void RefreshRestoreState();
    RestoreDescriptor CaptureRestoreDescriptor();
    ValueTask<PaneCloseResult> CloseAsync(
        PaneCloseReason reason,
        CancellationToken cancellationToken = default);
}

/// <summary>Public creation and restoration contract for built-in and third-party pane types.</summary>
public interface IPaneProvider
{
    PaneKind Kind { get; }
    ValueTask<IPaneRuntime> CreateAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default);
    ValueTask<IPaneRuntime> RestoreAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default);
}
