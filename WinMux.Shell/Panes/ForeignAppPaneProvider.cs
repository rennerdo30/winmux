using System.Diagnostics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
        BuildAsync(context, restoring: false, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, restoring: true, token);

    public void Refresh() => _tracker.Refresh();
    public void Dispose() => _tracker.Dispose();

    private async ValueTask<IPaneRuntime> BuildAsync(PaneProviderContext context, bool restoring, CancellationToken token)
    {
        var pane = new Pane(context.PaneId, PaneKind.ForeignApp, context.Title, context.Descriptor);
        NormalizeProgram(pane);
        var runtime = new ForeignAppPaneRuntime(pane, _tracker, _claimedWindows, _ownerWindow(), _windows, restoring);
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
    private readonly TextBlock _title;
    private readonly Image _icon;
    private readonly Button _restartButton;
    private readonly Button _outsideButton;
    private readonly WrapPanel _actions;
    private Bitmap? _iconBitmap;
    private int _disposed;

    public ForeignAppPaneRuntime(
        Pane pane,
        ForeignWindowTracker tracker,
        HashSet<WindowHandle> claimedWindows,
        WindowHandle ownerWindow,
        IHostWindowService windows,
        bool restoring = false)
    {
        _pane = pane;
        _tracker = tracker;
        _claimedWindows = claimedWindows;
        _ownerWindow = ownerWindow;
        _app = new ForeignAppPane(pane, windows, restoring);
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
        _icon = new Image { Width = 20, Height = 20 };
        _title = new TextBlock
        {
            Text = pane.Title,
            FontFamily = Chrome.Palette.UiFont,
            FontSize = Chrome.Palette.BodySize,
            Foreground = Chrome.Palette.TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Chrome.Palette.UiFont,
            FontSize = Chrome.Palette.CaptionSize,
            Foreground = Chrome.Palette.MutedTextBrush,
            Text = "starting…",
        };
        _restartButton = new Button { Content = "Restart", Margin = new Thickness(0, 0, 8, 8) };
        _restartButton.Click += async (_, _) =>
        {
            _restartButton.IsEnabled = false;
            try { await _app.RestartAsync(_claimedWindows, _ownerWindow, CancellationToken.None); }
            finally { _restartButton.IsEnabled = true; UpdateMessage(); }
        };
        var close = new Button { Content = "Close pane", Margin = new Thickness(0, 0, 8, 8) };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        _outsideButton = new Button { Content = "Open outside WinMux", Margin = new Thickness(0, 0, 8, 8) };
        _outsideButton.Click += (_, _) => OpenOutside();
        _actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _restartButton, _outsideButton, close },
        };
        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _icon, _title },
        };
        View = new Border
        {
            Background = Chrome.Palette.DialogBrush,
            BorderBrush = Chrome.Palette.EdgeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Chrome.Palette.SurfaceRadius,
            Child = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { identity, _message, _actions },
            },
        };
        _ = LoadIconAsync();
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
    public RestoreDescriptor CaptureRestoreDescriptor() => _app.CaptureRestoreDescriptor();

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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _tracker.Forget(PaneId);
            _iconBitmap?.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private void UpdateMessage()
    {
        _title.Text = _pane.Title;
        _message.Text = _app.Status;
        _restartButton.Content = _pane.Restore.Extras.ContainsKey("adopt_window") ? "Launch new" : "Restart";
        _actions.IsVisible = !_app.IsReady && _app.Status != "starting…";
        _outsideButton.IsVisible = !_app.IsReady && !string.IsNullOrWhiteSpace(_pane.Restore.Program);
    }

    private async Task LoadIconAsync()
    {
        if (string.IsNullOrWhiteSpace(_pane.Restore.Program) || !PlatformServices.AppIcons.IsAvailable) return;
        try
        {
            var png = await Task.Run(() => PlatformServices.AppIcons.GetIconPng(_pane.Restore.Program, 32));
            if (png is null) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed != 0) return;
                using var stream = new MemoryStream(png);
                _iconBitmap = new Bitmap(stream);
                _icon.Source = _iconBitmap;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The title and program path are still visible when Windows has no usable icon.
        }
    }

    private void OpenOutside()
    {
        try
        {
            var start = new ProcessStartInfo(_pane.Restore.Program!) { UseShellExecute = true };
            foreach (var argument in _pane.Restore.Args) start.ArgumentList.Add(argument);
            if (_pane.Restore.Cwd.IsKnown && Directory.Exists(_pane.Restore.Cwd.Path))
                start.WorkingDirectory = _pane.Restore.Cwd.Path;
            using var launched = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the application");
            _message.Text = "Opened outside WinMux. This pane is still available to retry or close.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            _message.Text = "Could not open outside WinMux: " + ex.Message;
        }
    }
}
