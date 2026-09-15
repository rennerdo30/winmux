using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Panes;
using WinMux.Shell.Actions;
using WinMux.Shell.Cwd;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.Keymap;
using WinMux.Shell.Panes;
using CoreRect = WinMux.Core.Layout.Rect;

namespace WinMux.Shell;

/// <summary>
/// The shell window: one OS window holding the layout tree.
///
/// Terminal panes are Avalonia controls. Each foreign app is embedded by a separate PaneHost
/// process whose top-level window follows its pane rectangle. The shell never reparents a pane
/// host and never calls a foreign application's HWND; see ADR 0001.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly StackPanel _tabBar = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly Dictionary<PaneId, IPaneRuntime> _runtimes = [];
    private readonly PaneProviderRegistry _providers;
    private readonly ForeignAppPaneProvider _foreignProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ActionDispatcher _actions = new();
    private readonly KeymapRouter _keymap;
    private readonly SessionController _session;
    private readonly DispatcherTimer _cwdCaptureTimer;

    private LayoutTree _tree;
    private IntPtr _shellHwnd;
    private string _message = "";
    private Divider? _dragDivider;
    private Avalonia.Point _lastDragPoint;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _paneCloseInProgress;

    public MainWindow(
        LayoutTree tree,
        SessionController session,
        KeymapConfiguration keymapConfiguration,
        string title)
    {
        _tree = tree;
        _session = session;
        _session.Register(this);
        _foreignProvider = new ForeignAppPaneProvider(() => _shellHwnd);
        _providers = new PaneProviderRegistry([
            new TerminalPaneProvider(),
            new FileBrowserPaneProvider(() => Environment.CurrentDirectory),
            _foreignProvider,
        ]);
        _cwdCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _cwdCaptureTimer.Tick += (_, _) =>
        {
            RefreshPaneRestoreStates();
            _session.RequestSave();
        };
        _actions.BackgroundDispatchCompleted += result => Dispatcher.UIThread.Post(() =>
        {
            if (!result.Succeeded) _message = result.Error ?? "action failed";
            UpdateStatus();
        });
        RegisterActions();
        _keymap = new KeymapRouter(new KeyBindingTable(keymapConfiguration), _actions);

        Title = string.IsNullOrWhiteSpace(title) ? "WinMux" : title;
        Width = tree.Bounds.Width > 0 ? Math.Max(640, tree.Bounds.Width) : 1400;
        Height = tree.Bounds.Height > 0 ? Math.Max(400, tree.Bounds.Height) : 860;
        if (!tree.Bounds.IsEmpty)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(tree.Bounds.X, tree.Bounds.Y);
        }
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));

        var dock = new DockPanel();
        var tabs = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x3a)),
            Child = _tabBar,
            IsVisible = false,
        };
        _tabBar.Tag = tabs;
        DockPanel.SetDock(tabs, Dock.Top);
        dock.Children.Add(tabs);
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)),
            Child = _status,
        };
        // Chrome lives where panes never do (CLAUDE.md section 6): a native window hosted in a pane
        // paints above anything Avalonia draws, so the status bar gets its own dock region.
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);
        dock.Children.Add(_canvas);
        Content = dock;

        _canvas.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
        _canvas.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Relayout(); };
        _canvas.PointerPressed += BeginDividerDrag;
        _canvas.PointerMoved += ContinueDividerDrag;
        _canvas.PointerReleased += EndDividerDrag;

        // Tunnel first so the shell prefix wins before a focused terminal translates Ctrl+B
        // into byte 0x02. Pass-through keys continue down to the pane normally.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            ClampRestoredGeometry();
            _shellHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await StartPanesAsync();
            _cwdCaptureTimer.Start();
        };
        Closing += (_, e) => Shutdown(e);
        PositionChanged += (_, _) => Relayout();
        // Attach-mode windows sit above the shell but are not owned by it, so activating the shell
        // buries them. Re-assert placement (and z-order) whenever we come forward.
        Activated += (_, _) => { _session.Activate(this); _foreignProvider.Refresh(); Relayout(); };
        Deactivated += (_, _) => Relayout();
    }

    // ---------------- pane lifecycle ----------------

    private async Task StartPanesAsync()
    {
        // Restore sequentially. Foreign providers may need to distinguish a newly launched window
        // from already-running candidates; concurrent launches make that materially less reliable.
        foreach (var pane in _tree.Panes.ToList())
            await RestoreRuntimeAsync(pane);
        Relayout();
    }

    private async Task RestoreRuntimeAsync(Pane pane)
    {
        try
        {
            var runtime = await _providers.RestoreAsync(PaneProviderContext.FromPane(pane), _shutdown.Token);
            AddRuntime(pane, runtime);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var message = $"could not restore provider '{pane.Kind}': {ex.Message}";
            _message = message;
            AddRuntime(pane, new UnavailablePaneRuntime(pane, message));
        }
    }

    private void AddRuntime(Pane pane, IPaneRuntime runtime)
    {
        if (runtime.PaneId != pane.Id || runtime.Kind != pane.Kind)
            throw new InvalidOperationException(
                $"Provider '{runtime.Kind}' returned a runtime for the wrong pane ({runtime.PaneId}).");

        _runtimes.Add(pane.Id, runtime);
        _canvas.Children.Add(runtime.View);
        runtime.StateChanged += (_, _) => Dispatcher.UIThread.Post(() => SyncRuntimeState(pane, runtime));
        runtime.View.GotFocus += (_, _) =>
        {
            if (_tree.Focused == pane.Id) return;
            _tree.Focus(pane.Id);
            Relayout();
        };
        if (runtime is ITerminalHandoffRuntime handoff)
        {
            handoff.TerminalHandoffRequested += (_, _) =>
                Dispatcher.UIThread.Post(() => _ = OpenTerminalHereAsync(runtime));
        }
        SyncRuntimeState(pane, runtime);
    }

    private void SyncRuntimeState(Pane pane, IPaneRuntime runtime)
    {
        pane.Restore = runtime.CaptureRestoreDescriptor();
        if (!string.IsNullOrWhiteSpace(pane.Restore.Title)) pane.Title = pane.Restore.Title;
        if (!string.IsNullOrWhiteSpace(runtime.StatusMessage))
        {
            _message = $"{pane.Title}: {runtime.StatusMessage}";
        }
        UpdateStatus();
        UpdateTabBar();
        _session.RequestSave();
    }

    // ---------------- layout ----------------

    private void Relayout()
    {
        if (_canvas.Bounds.Width < 4 || _canvas.Bounds.Height < 4) return;

        var arrangement = _tree.Arrange(new CoreRect(0, 0, (int)_canvas.Bounds.Width, (int)_canvas.Bounds.Height));

        foreach (var pane in _tree.Panes)
        {
            if (!_runtimes.TryGetValue(pane.Id, out var runtime)) continue;
            var view = runtime.View;
            var visible = arrangement.IsVisible(pane.Id);
            var rect = arrangement[pane.Id];

            view.IsVisible = visible;
            if (visible)
            {
                Canvas.SetLeft(view, rect.X);
                Canvas.SetTop(view, rect.Y);
                view.Width = rect.Width;
                view.Height = rect.Height;
            }

            runtime.Arrange(new PaneArrangement(rect, visible));
        }

        UpdateStatus();
        UpdateTabBar();
        _session.RequestSave();
    }

    private void UpdateTabBar()
    {
        _tabBar.Children.Clear();
        var leaf = _tree.Find(_tree.Focused);
        var stack = FindEnclosingStack(leaf);
        if (_tabBar.Tag is Control container) container.IsVisible = stack is not null;
        if (stack is null) return;

        for (var index = 0; index < stack.Children.Count; index++)
        {
            var child = stack.Children[index];
            var target = child.Leaves().First().Pane.Id;
            var title = string.Join(" + ", child.Leaves().Select(item => item.Pane.Title));
            var button = new Button
            {
                Content = title,
                Padding = new Thickness(10, 3),
                FontWeight = index == stack.ActiveIndex ? FontWeight.Bold : FontWeight.Normal,
                Opacity = index == stack.ActiveIndex ? 1.0 : 0.72,
            };
            button.Click += (_, _) =>
            {
                _tree.Focus(target);
                _runtimes.GetValueOrDefault(target)?.Focus();
                Relayout();
            };
            _tabBar.Children.Add(button);
        }
    }

    private static StackNode? FindEnclosingStack(LayoutNode? node)
    {
        for (var current = node?.Parent; current is not null; current = current.Parent)
            if (current is StackNode stack) return stack;
        return null;
    }

    private void BeginDividerDrag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(_canvas);
        var arrangement = _tree.Arrange();
        var divider = arrangement.Dividers.FirstOrDefault(item => item.Rect.Contains((int)point.X, (int)point.Y));
        if (divider.Split is null) return;

        _dragDivider = divider;
        _lastDragPoint = point;
        e.Pointer.Capture(_canvas);
        e.Handled = true;
    }

    private void ContinueDividerDrag(object? sender, PointerEventArgs e)
    {
        if (_dragDivider is not { } divider) return;
        var point = e.GetPosition(_canvas);
        var pixels = divider.Direction == SplitDirection.Columns ? point.X - _lastDragPoint.X : point.Y - _lastDragPoint.Y;
        var extent = SplitExtent(divider.Split, divider.Direction);
        if (extent > 0 && Math.Abs(pixels) >= 1)
        {
            _tree.ResizeDivider(divider, pixels / extent);
            _lastDragPoint = point;
            Relayout();
        }
        e.Handled = true;
    }

    private void EndDividerDrag(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragDivider is null) return;
        _dragDivider = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private double SplitExtent(SplitNode split, SplitDirection direction)
    {
        var arrangement = _tree.Arrange();
        var rectangles = split.Leaves()
            .Select(leaf => arrangement.PaneRects.GetValueOrDefault(leaf.Pane.Id))
            .Where(rect => !rect.IsEmpty)
            .ToList();
        if (rectangles.Count == 0) return 0;
        return direction == SplitDirection.Columns
            ? rectangles.Max(rect => rect.Right) - rectangles.Min(rect => rect.Left)
            : rectangles.Max(rect => rect.Bottom) - rectangles.Min(rect => rect.Top);
    }

    private void UpdateStatus()
    {
        var focused = _tree.GetPane(_tree.Focused);
        var prefix = _keymap.IsPrefixArmed ? "  [PREFIX]" : "";
        var hint = _keymap.IsPrefixArmed
            ? "  %/\" split    ←↑↓→ focus    ⇧←↑↓→ resize    x close    c tab    n/p cycle    : actions"
            : "  Ctrl+B then a key";
        _status.Text =
            $"{_tree.Panes.Count()} panes   focus: {focused?.Title ?? "-"}{prefix}{hint}" +
            (_message.Length > 0 ? "   |   " + _message : "");
    }

    // ---------------- keys ----------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var route = _keymap.Route(e.Key, e.KeyModifiers);
        e.Handled = route.Handled;
        if (!route.Handled) return;

        _message = route.Kind switch
        {
            KeymapRouteKind.PrefixArmed => "",
            KeymapRouteKind.UnboundPrefixedKey => "no binding for " + e.Key,
            KeymapRouteKind.ActionDispatched when route.Dispatch is { Succeeded: false } failed => failed.Error ?? "action failed",
            _ => _message,
        };
        UpdateStatus();
    }

    private void RegisterActions()
    {
        _actions.RegisterAsync(ShellActionNames.SplitColumns,
            _ => new ValueTask(SplitFocusedAsync(SplitDirection.Columns)));
        _actions.RegisterAsync(ShellActionNames.SplitRows,
            _ => new ValueTask(SplitFocusedAsync(SplitDirection.Rows)));
        _actions.Register(ShellActionNames.FocusLeft, () => MoveFocus(FocusDirection.Left));
        _actions.Register(ShellActionNames.FocusRight, () => MoveFocus(FocusDirection.Right));
        _actions.Register(ShellActionNames.FocusUp, () => MoveFocus(FocusDirection.Up));
        _actions.Register(ShellActionNames.FocusDown, () => MoveFocus(FocusDirection.Down));
        _actions.RegisterAsync(ShellActionNames.ClosePane, _ => new ValueTask(CloseFocusedAsync()));
        _actions.RegisterAsync(ShellActionNames.NewTab, _ => new ValueTask(AddTabAsync()));
        _actions.Register(ShellActionNames.NextTab, () => CycleTab(1));
        _actions.Register(ShellActionNames.PreviousTab, () => CycleTab(-1));
        _actions.Register(ShellActionNames.SaveSession, SaveSession);
        _actions.Register(ShellActionNames.ResizeLeft, () => ResizeFocused(FocusDirection.Left));
        _actions.Register(ShellActionNames.ResizeRight, () => ResizeFocused(FocusDirection.Right));
        _actions.Register(ShellActionNames.ResizeUp, () => ResizeFocused(FocusDirection.Up));
        _actions.Register(ShellActionNames.ResizeDown, () => ResizeFocused(FocusDirection.Down));
        _actions.Register(ShellActionNames.ShowPalette, ShowPalette);
        _actions.Register(ShellActionNames.SendPrefix, SendPrefix);
        _actions.RegisterAsync(ShellActionNames.NewTerminalCmd, _ => new ValueTask(AddTabAsync(TerminalProfiles.Cmd)));
        _actions.RegisterAsync(ShellActionNames.NewTerminalWindowsPowerShell,
            _ => new ValueTask(AddTabAsync(TerminalProfiles.WindowsPowerShell)));
        _actions.RegisterAsync(ShellActionNames.NewTerminalPowerShell,
            _ => new ValueTask(AddTabAsync(TerminalProfiles.PowerShell)));
        _actions.RegisterAsync(ShellActionNames.NewTerminalWsl, _ => new ValueTask(AddTabAsync(TerminalProfiles.Wsl)));
        _actions.RegisterAsync(ShellActionNames.NewFileBrowser, _ => new ValueTask(AddFileBrowserTabAsync()));
        _actions.RegisterAsync(ShellActionNames.OpenTerminalHere, _ => new ValueTask(OpenTerminalHereAsync()));
        _actions.RegisterAsync(ShellActionNames.ConfigureCwdReporting,
            _ => new ValueTask(ShowCwdIntegrationAsync(onlyIfUnseen: false)));
        _actions.RegisterAsync(ShellActionNames.ToggleForeignHostStrategy,
            _ => new ValueTask(ToggleForeignHostStrategyAsync()));
    }

    internal ActionDispatchResult DispatchNamedAction(string actionName)
    {
        var result = _actions.Dispatch(actionName);
        if (!result.Succeeded) _message = result.Error ?? "action failed";
        UpdateStatus();
        return result;
    }

    internal async ValueTask<ActionDispatchResult> DispatchNamedActionAsync(
        string actionName,
        CancellationToken cancellationToken = default)
    {
        var result = await _actions.DispatchAsync(actionName, cancellationToken);
        if (!result.Succeeded) _message = result.Error ?? "action failed";
        UpdateStatus();
        return result;
    }

    private void ShowPalette()
    {
        var palette = new CommandPaletteWindow(_actions.RegisteredActions, action => DispatchNamedAction(action));
        palette.Show(this);
    }

    private void SendPrefix()
    {
        if (_runtimes.GetValueOrDefault(_tree.Focused) is ITerminalInputRuntime terminal)
            _ = terminal.SendPrefixAsync(_shutdown.Token);
        else
            _message = "the focused pane does not accept terminal input";
    }

    private async Task ToggleForeignHostStrategyAsync()
    {
        if (_runtimes.GetValueOrDefault(_tree.Focused) is not IForeignHostStrategyRuntime app ||
            app.EffectiveStrategy is not { } current)
        {
            _message = "the focused pane is not a ready foreign application";
            UpdateStatus();
            return;
        }

        var target = current == HostStrategy.Embed ? HostStrategy.Attach : HostStrategy.Embed;
        _message = $"switching foreign pane to {target.ToString().ToLowerInvariant()}…";
        UpdateStatus();
        try
        {
            var result = await app.SwitchStrategyAsync(target, _shutdown.Token);
            _message = result.Message;
            _session.RequestSave();
            Relayout();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private Pane NewTerminalPane(TerminalProfile? profile = null, string? directory = null)
    {
        var focused = _tree.GetPane(_tree.Focused);
        var cwd = directory is { Length: > 0 }
            ? new WorkingDirectory(directory, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow)
            : focused?.Restore.Cwd;
        profile ??= focused?.Kind == PaneKind.Terminal && !string.IsNullOrWhiteSpace(focused.Restore.Program)
            ? new TerminalProfile(focused.Title, focused.Restore.Program!, focused.Restore.Args)
            : TerminalProfiles.Cmd;
        var pane = Pane.Terminal(profile.Name, profile.Program,
            cwd is { IsKnown: true }
                ? new WorkingDirectory(cwd.Path, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow)
                : null);
        pane.Restore = pane.Restore with { Args = profile.Arguments };
        var normalized = ExecutablePathResolver.ResolveTerminalDescriptor(pane.Restore);
        if (normalized.Succeeded)
        {
            pane.Restore = normalized.Descriptor;
        }
        else
        {
            _message = normalized.Error ?? $"could not resolve {profile.Program}";
        }
        return pane;
    }

    private Pane NewFileBrowserPane(string? directory = null)
    {
        directory ??= _runtimes.GetValueOrDefault(_tree.Focused) is ITerminalHandoffRuntime browser
            ? browser.TerminalHandoffDirectory
            : _tree.GetPane(_tree.Focused)?.Restore.Cwd is { IsKnown: true } cwd
                ? cwd.Path
                : Environment.CurrentDirectory;
        directory ??= Environment.CurrentDirectory;
        return new Pane(PaneId.New(), PaneKind.FileBrowser, "files", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "files",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [FileBrowserModel.CurrentDirectoryExtra] = directory,
                [FileBrowserModel.SelectedPathExtra] = string.Empty,
            },
        });
    }

    private async Task<IPaneRuntime> CreateNewRuntimeAsync(Pane pane)
    {
        try
        {
            return await _providers.CreateAsync(PaneProviderContext.FromPane(pane), _shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _message = $"could not create {pane.Kind} pane: {ex.Message}";
            UpdateStatus();
            throw new InvalidOperationException(_message, ex);
        }
    }

    private async Task SplitFocusedAsync(SplitDirection direction)
    {
        var pane = NewTerminalPane();
        var runtime = await CreateNewRuntimeAsync(pane);
        var id = _tree.Split(_tree.Focused, direction, pane);
        AddRuntime(pane, runtime);
        _message = direction == SplitDirection.Columns ? "split into columns" : "split into rows";
        Relayout();
        _runtimes.GetValueOrDefault(id)?.Focus();
    }

    private Task AddTabAsync() => AddTabAsync(NewTerminalPane(), "new tab");

    private Task AddTabAsync(TerminalProfile profile) =>
        AddTabAsync(NewTerminalPane(profile), "new " + profile.Name + " tab");

    private Task AddFileBrowserTabAsync() =>
        AddTabAsync(NewFileBrowserPane(), "new file-browser tab");

    private async Task AddTabAsync(Pane pane, string message, PaneId? beside = null)
    {
        var runtime = await CreateNewRuntimeAsync(pane);
        var id = _tree.AddTab(beside ?? _tree.Focused, pane);
        AddRuntime(pane, runtime);
        _message = message;
        Relayout();
        _runtimes.GetValueOrDefault(id)?.Focus();
    }

    private Task OpenTerminalHereAsync()
    {
        if (_runtimes.TryGetValue(_tree.Focused, out var runtime)) return OpenTerminalHereAsync(runtime);
        _message = "the focused pane is unavailable";
        UpdateStatus();
        throw new InvalidOperationException(_message);
    }

    private async Task OpenTerminalHereAsync(IPaneRuntime source)
    {
        if (source is not ITerminalHandoffRuntime { TerminalHandoffDirectory: { Length: > 0 } directory })
        {
            _message = "the focused pane is not a file browser";
            UpdateStatus();
            throw new InvalidOperationException(_message);
        }
        if (!Directory.Exists(directory))
        {
            _message = $"cannot open a terminal because the directory is unavailable: {directory}";
            UpdateStatus();
            throw new InvalidOperationException(_message);
        }

        await AddTabAsync(
            NewTerminalPane(TerminalProfiles.Cmd, directory),
            "opened terminal at " + directory,
            source.PaneId);
    }

    private void CycleTab(int delta)
    {
        _message = _tree.CycleTab(delta) ? "" : "this pane is not in a tab group";
        Relayout();
    }

    private void MoveFocus(FocusDirection direction)
    {
        if (!_tree.MoveFocus(direction)) { _message = "no pane " + direction.ToString().ToLowerInvariant(); }
        else
        {
            _message = "";
            _runtimes.GetValueOrDefault(_tree.Focused)?.Focus();
        }
        Relayout();
    }

    private void ResizeFocused(FocusDirection direction)
    {
        var leaf = _tree.Find(_tree.Focused);
        var expected = direction is FocusDirection.Left or FocusDirection.Right
            ? SplitDirection.Columns
            : SplitDirection.Rows;

        if (leaf?.Parent is not SplitNode split || split.Direction != expected)
        {
            _message = "no matching divider beside this pane";
            UpdateStatus();
            return;
        }

        var delta = direction is FocusDirection.Right or FocusDirection.Down ? 0.05 : -0.05;
        _message = _tree.Resize(_tree.Focused, delta) ? "resized" : "divider is at its limit";
        Relayout();
    }

    private async Task CloseFocusedAsync()
    {
        if (_paneCloseInProgress) { _message = "a pane is already closing"; UpdateStatus(); return; }
        if (_tree.Panes.Count() == 1) { _message = "cannot close the last pane"; UpdateStatus(); return; }

        var id = _tree.Focused;
        _paneCloseInProgress = true;
        try
        {
            if (_runtimes.TryGetValue(id, out var runtime))
            {
                _message = "closing pane safely…";
                UpdateStatus();
                var result = await runtime.CloseAsync(PaneCloseReason.PaneRemoved);
                if (!result.Succeeded)
                {
                    _message = "pane stayed open: " + result.Message;
                    UpdateStatus();
                    return;
                }
            }

            if (!_tree.Close(id)) { _message = "cannot close the last pane"; UpdateStatus(); return; }
            await RemoveRuntimeAsync(id);

            _message = "closed";
            Relayout();
        }
        finally
        {
            _paneCloseInProgress = false;
        }
    }

    private void SaveSession()
    {
        RefreshPaneRestoreStates();
        var result = _session.SaveNow();
        if (result.Succeeded)
        {
            _message = "wrote " + _session.SessionPath;
        }
        else
        {
            _message = "could not write session: " + result.Error?.Message;
        }
        UpdateStatus();
    }

    internal WindowSnapshot CaptureSnapshot()
    {
        RefreshPaneRestoreStates();
        var snapshot = SessionMapper.ToSnapshot(_tree, Title ?? "WinMux");
        var width = Math.Max(1, (int)Math.Round(Bounds.Width > 0 ? Bounds.Width : Width));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height > 0 ? Bounds.Height : Height));
        return snapshot with { Bounds = new CoreRect(Position.X, Position.Y, width, height) };
    }

    internal async Task ShowCwdIntegrationAsync(bool onlyIfUnseen)
    {
        try
        {
            var installer = new ProfileInstaller();
            var report = installer.Inspect();
            var unseen = CwdIntegrationOnboarding.Unseen(report);
            if (onlyIfUnseen && unseen.Count == 0) return;

            var dialog = new CwdIntegrationWindow(installer, report);
            await dialog.ShowDialog(this);
            CwdIntegrationOnboarding.MarkSeen(onlyIfUnseen ? unseen : report.Shells.Select(status => status.Shell));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _message = "cwd reporting setup is unavailable: " + ex.Message;
            UpdateStatus();
        }
    }

    internal void ShowMessage(string message)
    {
        _message = message;
        UpdateStatus();
    }

    private void Shutdown(WindowClosingEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (_shutdownStarted) return;
        _cwdCaptureTimer.Stop();
        RefreshPaneRestoreStates();
        var saved = _session.PrepareWindowClosing(this);
        if (!saved.Succeeded)
        {
            _cwdCaptureTimer.Start();
            _message = "WinMux stayed open because the session could not be saved: " + saved.Error?.Message;
            UpdateStatus();
            return;
        }

        _shutdownStarted = true;
        _message = "detaching foreign applications safely…";
        UpdateStatus();
        _ = CompleteShutdownAsync();
    }

    private async Task CompleteShutdownAsync()
    {
        try
        {
            var attempts = _runtimes.Values
                .Select(runtime => (Runtime: runtime, Task: CloseRuntimeSafelyAsync(runtime, PaneCloseReason.ShellShutdown)))
                .ToArray();
            var closeResults = await Task.WhenAll(attempts.Select(attempt => attempt.Task));
            var failed = closeResults.FirstOrDefault(result => !result.Succeeded);
            if (failed is not null)
            {
                var removed = 0;
                for (var index = 0; index < attempts.Length; index++)
                {
                    if (!closeResults[index].Succeeded || closeResults[index].CanContinueIfWindowStaysOpen) continue;
                    var runtime = attempts[index].Runtime;
                    _tree.Close(runtime.PaneId);
                    await RemoveRuntimeAsync(runtime.PaneId);
                    removed++;
                }

                _shutdownStarted = false;
                _cwdCaptureTimer.Start();
                _message = "WinMux stayed open because a pane could not close safely: " + failed.Message +
                           (removed > 0
                               ? $"; {removed} detached pane(s) were removed from this window"
                               : string.Empty);
                Relayout();
                return;
            }

            _session.CompleteWindowClosing(this);
            _shutdown.Cancel();

            foreach (var runtime in _runtimes.Values.ToArray())
                await runtime.DisposeAsync();
            _runtimes.Clear();
            _foreignProvider.Dispose();
            _shutdownComplete = true;
            // Always leave the original Closing event before asking Avalonia to close again.
            Dispatcher.UIThread.Post(Close);
        }
        catch (Exception ex)
        {
            _shutdownStarted = false;
            _cwdCaptureTimer.Start();
            _message = "WinMux stayed open because safe detach failed: " + ex.Message;
            UpdateStatus();
        }
    }

    private static async Task<PaneCloseResult> CloseRuntimeSafelyAsync(
        IPaneRuntime runtime,
        PaneCloseReason reason)
    {
        try
        {
            return await runtime.CloseAsync(reason);
        }
        catch (Exception ex)
        {
            return PaneCloseResult.Failure(ex.Message);
        }
    }

    private async Task RemoveRuntimeAsync(PaneId paneId)
    {
        if (!_runtimes.Remove(paneId, out var runtime)) return;
        _canvas.Children.Remove(runtime.View);
        await runtime.DisposeAsync();
    }

    private void RefreshPaneRestoreStates()
    {
        foreach (var pane in _tree.Panes)
        {
            if (!_runtimes.TryGetValue(pane.Id, out var runtime)) continue;
            runtime.RefreshRestoreState();
            pane.Restore = runtime.CaptureRestoreDescriptor();
            if (!string.IsNullOrWhiteSpace(pane.Restore.Title)) pane.Title = pane.Restore.Title;
        }
    }

    private void ClampRestoredGeometry()
    {
        var areas = Screens.All
            .OrderByDescending(screen => screen == Screens.Primary)
            .Select(screen => new CoreRect(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.Width,
                screen.WorkingArea.Height))
            .ToArray();
        if (areas.Length == 0) return;

        var saved = new CoreRect(Position.X, Position.Y, (int)Math.Round(Width), (int)Math.Round(Height));
        var visible = WindowGeometry.ClampToVisibleArea(saved, areas);
        Position = new PixelPoint(visible.X, visible.Y);
        Width = visible.Width;
        Height = visible.Height;
    }
}
