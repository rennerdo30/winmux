using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Platform;

// Avalonia has a Rect of its own, in device-independent doubles. The platform layer speaks in
// whole physical pixels, so the two must never be confused silently.
using LayoutRect = WinMux.Core.Layout.Rect;

namespace WinMux.Shell.Panes;

internal interface IForeignHostStrategyRuntime
{
    HostStrategy? EffectiveStrategy { get; }
    ValueTask<ForeignAppSwitchResult> SwitchStrategyAsync(HostStrategy target, CancellationToken token = default);
}

internal sealed class ForeignAppPaneProvider(Func<WindowHandle> ownerWindow, IHostWindowService windows)
    : IPaneProvider, IDisposable
{
    private readonly IHostWindowService _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    private readonly ForeignWindowTracker _tracker = new(windows);
    private readonly HashSet<WindowHandle> _claimedWindows = [];
    private readonly Func<WindowHandle> _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));

    public PaneKind Kind => PaneKind.ForeignApp;

    /// <summary>Needs a program or a window chosen first; the launcher has its own buttons for that.</summary>
    public bool IsOfferedDirectly => false;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    public void Refresh() => _tracker.Refresh();
    public void Dispose() => _tracker.Dispose();

    private async ValueTask<IPaneRuntime> BuildAsync(PaneProviderContext context, CancellationToken token)
    {
        var pane = new Pane(context.PaneId, PaneKind.ForeignApp, context.Title, context.Descriptor);
        NormalizeProgram(pane);
        var runtime = new ForeignAppPaneRuntime(pane, _tracker, _claimedWindows, _ownerWindow(), _windows);
        await runtime.StartAsync(token);
        return runtime;
    }

    private static void NormalizeProgram(Pane pane)
    {
        if (string.IsNullOrWhiteSpace(pane.Restore.Program)) return;
        var resolution = ExecutablePathResolver.Resolve(pane.Restore.Program);
        if (resolution.Succeeded) pane.Restore = pane.Restore with { Program = resolution.AbsolutePath };
    }
}

/// <summary>
/// A pane that stands in for a window it does not own, and can recognise it again.
///
/// The shell needs this to reconcile focus: when Windows says some window just became active, the
/// only way to know whether that is one of our panes is to ask the panes.
/// </summary>
internal interface IHostedWindowPane
{
    bool OwnsWindow(WindowHandle window);
}

internal interface IPaneCloseRequest
{
    event EventHandler? CloseRequested;
}

internal sealed class ForeignAppPaneRuntime : IPaneRuntime, IForeignHostStrategyRuntime, IHostedWindowPane, IPaneCloseRequest
{
    private readonly Pane _pane;
    private readonly ForeignAppPane _app;
    private readonly ForeignWindowTracker _tracker;
    private readonly HashSet<WindowHandle> _claimedWindows;
    private readonly WindowHandle _ownerWindow;
    private readonly TextBlock _message;
    private readonly StackPanel _actions;
    private int _disposed;

    public ForeignAppPaneRuntime(
        Pane pane,
        ForeignWindowTracker tracker,
        HashSet<WindowHandle> claimedWindows,
        WindowHandle ownerWindow,
        IHostWindowService windows)
    {
        _pane = pane;
        _tracker = tracker;
        _claimedWindows = claimedWindows;
        _ownerWindow = ownerWindow;
        _app = new ForeignAppPane(pane, windows);
        _app.Exited += child =>
        {
            lock (_claimedWindows) _claimedWindows.Remove(child);
            _tracker.Forget(PaneId);
        };
        _app.StateChanged += () =>
        {
            UpdateMessage();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _message = new TextBlock
        {
            Margin = new Thickness(14),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Chrome.Palette.TextBrush,
            Text = $"{pane.Title}\n\nstarting…",
        };
        var restart = new Button { Content = "Restart", Margin = new Thickness(0, 0, 8, 0) };
        restart.Click += async (_, _) =>
        {
            restart.IsEnabled = false;
            try { await _app.RestartAsync(_claimedWindows, _ownerWindow, CancellationToken.None); }
            finally { restart.IsEnabled = true; UpdateMessage(); }
        };
        var close = new Button { Content = "Close pane" };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Margin = new Thickness(14, 0, 14, 14),
            Children = { restart, close },
        };
        View = new Border
        {
            Background = Chrome.Palette.SurfaceBrush,
            BorderBrush = Chrome.Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { _message, _actions } },
        };
        UpdateMessage();
    }

    public PaneId PaneId => _pane.Id;

    /// <summary>
    /// Whether <paramref name="window"/> is the window this pane is standing in for.
    ///
    /// Both handles count. In embed mode the application is a child of the pane host, so the root
    /// window Windows reports is the host; in attach mode the application window is top-level and
    /// is the one reported. A pane that only recognised one of them would reconcile focus in one
    /// hosting strategy and not the other.
    /// </summary>
    public bool OwnsWindow(WindowHandle window) =>
        !window.IsNone && (window == _app.Hwnd || window == _app.ChildHwnd);
    public PaneKind Kind => PaneKind.ForeignApp;
    public Control View { get; }
    public string? StatusMessage => _app.Notice ?? _app.Status;
    public HostStrategy? EffectiveStrategy => _app.EffectiveStrategy;
    public event EventHandler? StateChanged;
    public event EventHandler? CloseRequested;

    public async Task StartAsync(CancellationToken token)
    {
        await _app.LaunchAsync(_claimedWindows, _ownerWindow, token);
        UpdateMessage();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Focus() => View.Focus();

    public void Arrange(PaneArrangement arrangement)
    {
        if (_app.Located)
        {
            // Arrange can run before Avalonia has laid out this View at its canvas offset.
            // The arrangement is already in canvas coordinates and is the authoritative origin.
            if (View.Parent is not Canvas canvas) return;
            var origin = canvas.PointToScreen(new Point(arrangement.Bounds.X, arrangement.Bounds.Y));
            var scale = TopLevel.GetTopLevel(View)?.RenderScaling ?? 1.0;
            _tracker.Place(
                PaneId,
                _app.Hwnd,
                new LayoutRect(
                    origin.X,
                    origin.Y,
                    Math.Max(1, (int)Math.Round(arrangement.Bounds.Width * scale)),
                    Math.Max(1, (int)Math.Round(arrangement.Bounds.Height * scale))),
                arrangement.IsVisible,
                child: false);
        }
        else
        {
            UpdateMessage();
        }
    }

    public void RefreshRestoreState() { }
    public RestoreDescriptor CaptureRestoreDescriptor() => _pane.Restore;

    public async ValueTask<ForeignAppSwitchResult> SwitchStrategyAsync(HostStrategy target, CancellationToken token = default)
    {
        var result = await _app.SwitchStrategyAsync(target, token);
        UpdateMessage();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public async ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken token = default)
    {
        var child = _app.ChildHwnd;
        var result = reason == PaneCloseReason.ShellShutdown
            ? await _app.DetachAsync(token)
            : await _app.CloseAsync(token);
        if (!result.Succeeded) return PaneCloseResult.Failure(result.Message);

        lock (_claimedWindows) _claimedWindows.Remove(child);
        _tracker.Forget(PaneId);
        return reason == PaneCloseReason.ShellShutdown
            ? PaneCloseResult.Detached(result.Message)
            : PaneCloseResult.Success(result.Message);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _tracker.Forget(PaneId);
        return ValueTask.CompletedTask;
    }

    private void UpdateMessage()
    {
        _message.Text = $"{_pane.Title}\n\n{_app.Status}";
        _actions.IsVisible = !_app.IsReady && _app.Status != "starting…";
    }
}
