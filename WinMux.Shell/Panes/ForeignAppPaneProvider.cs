using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Panes;

internal interface IForeignHostStrategyRuntime
{
    HostStrategy? EffectiveStrategy { get; }
    ValueTask<ForeignAppSwitchResult> SwitchStrategyAsync(HostStrategy target, CancellationToken token = default);
}

internal sealed class ForeignAppPaneProvider(Func<IntPtr> ownerWindow) : IPaneProvider, IDisposable
{
    private readonly ForeignWindowTracker _tracker = new();
    private readonly HashSet<IntPtr> _claimedWindows = [];
    private readonly Func<IntPtr> _ownerWindow = ownerWindow ?? throw new ArgumentNullException(nameof(ownerWindow));

    public PaneKind Kind => PaneKind.ForeignApp;

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
        var runtime = new ForeignAppPaneRuntime(pane, _tracker, _claimedWindows, _ownerWindow());
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

internal sealed class ForeignAppPaneRuntime : IPaneRuntime, IForeignHostStrategyRuntime
{
    private readonly Pane _pane;
    private readonly ForeignAppPane _app;
    private readonly ForeignWindowTracker _tracker;
    private readonly HashSet<IntPtr> _claimedWindows;
    private readonly IntPtr _ownerWindow;
    private readonly TextBlock _message;
    private int _disposed;

    public ForeignAppPaneRuntime(
        Pane pane,
        ForeignWindowTracker tracker,
        HashSet<IntPtr> claimedWindows,
        IntPtr ownerWindow)
    {
        _pane = pane;
        _tracker = tracker;
        _claimedWindows = claimedWindows;
        _ownerWindow = ownerWindow;
        _app = new ForeignAppPane(pane);
        _message = new TextBlock
        {
            Margin = new Thickness(14),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
            Text = $"{pane.Title}\n\nstarting…",
        };
        View = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x3a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
            BorderThickness = new Thickness(1),
            Child = _message,
        };
    }

    public PaneId PaneId => _pane.Id;
    public PaneKind Kind => PaneKind.ForeignApp;
    public Control View { get; }
    public string? StatusMessage => _app.Notice ?? _app.Status;
    public HostStrategy? EffectiveStrategy => _app.EffectiveStrategy;
    public event EventHandler? StateChanged;

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
            var origin = View.PointToScreen(new Point(0, 0));
            var scale = TopLevel.GetTopLevel(View)?.RenderScaling ?? 1.0;
            _tracker.Place(
                PaneId,
                _app.Hwnd,
                origin.X,
                origin.Y,
                Math.Max(1, (int)Math.Round(arrangement.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(arrangement.Bounds.Height * scale)),
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

    private void UpdateMessage() => _message.Text = $"{_pane.Title}\n\n{_app.Status}";
}
