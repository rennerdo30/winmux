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

    /// <summary>
    /// What to call this in a menu, for the places that offer a pane kind to a user.
    ///
    /// Defaulted to the kind identifier so that existing providers keep compiling and still appear
    /// — an external pane kind that nobody can find is the failure CLAUDE.md section 5a is about,
    /// and "com.example.clock" in a list beats absence from it. Providers that want to read well
    /// override it.
    /// </summary>
    string DisplayName => Kind.Value;

    /// <summary>
    /// Whether a user may choose this kind directly, from an empty pane's launcher.
    ///
    /// False for the kinds that are reached another way: an empty pane cannot contain itself, a
    /// terminal comes from a profile so that it knows which shell to run, and a foreign application
    /// needs a window or a program chosen first.
    /// </summary>
    bool IsOfferedDirectly => true;
    ValueTask<IPaneRuntime> CreateAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default);
    ValueTask<IPaneRuntime> RestoreAsync(
        PaneProviderContext context,
        CancellationToken cancellationToken = default);
}
