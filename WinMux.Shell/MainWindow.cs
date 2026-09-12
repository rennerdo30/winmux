using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Shell.Actions;
using WinMux.Shell.Cwd;
using WinMux.Shell.Keymap;
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
    private readonly ForeignWindowTracker _tracker = new();
    private readonly Dictionary<PaneId, Control> _views = [];
    private readonly Dictionary<PaneId, ForeignAppPane> _foreign = [];
    /// <summary>Windows already adopted by some pane, so two panes cannot claim the same one.</summary>
    private readonly HashSet<IntPtr> _claimedWindows = [];
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
        _cwdCaptureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _cwdCaptureTimer.Tick += (_, _) =>
        {
            RefreshProcessWorkingDirectories();
            _session.RequestSave();
        };
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
        Activated += (_, _) => { _session.Activate(this); _tracker.Refresh(); Relayout(); };
        Deactivated += (_, _) => Relayout();
    }

    // ---------------- pane lifecycle ----------------

    private async Task StartPanesAsync()
    {
        foreach (var pane in _tree.Panes.ToList()) EnsureView(pane);
        Relayout();

        // Foreign apps are launched one at a time: several installers-worth of windows appearing
        // at once makes "which window is mine" materially harder, and spike 2 showed that question
        // is where this goes wrong.
        foreach (var pane in _tree.Panes.Where(p => p.Kind == PaneKind.ForeignApp).ToList())
        {
            if (!_foreign.TryGetValue(pane.Id, out var app)) continue;
            await app.LaunchAsync(_claimedWindows, _shellHwnd, _shutdown.Token);

            if (app.Notice is { Length: > 0 }) _message = $"{pane.Title}: {app.Notice}";

            Relayout();
        }
        Relayout();
    }

    private Control EnsureView(Pane pane)
    {
        if (_views.TryGetValue(pane.Id, out var existing)) return existing;

        NormalizeProgram(pane);

        Control view;
        switch (pane.Kind)
        {
            case PaneKind.Terminal:
                view = CreateTerminal(pane);
                break;

            case PaneKind.ForeignApp:
                _foreign[pane.Id] = new ForeignAppPane(pane);
                view = CreateForeignPlaceholder(pane);
                break;

            default:
                view = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
                    Child = new TextBlock { Text = pane.Kind + " panes are not implemented yet", Margin = new Thickness(12) },
                };
                break;
        }

        _views[pane.Id] = view;
        _canvas.Children.Add(view);
        return view;
    }

    private Control CreateTerminal(Pane pane)
    {
        var r = pane.Restore;
        var program = string.IsNullOrWhiteSpace(r.Program)
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : r.Program;
        var isWsl = IsWsl(program);
        var arguments = r.Args.ToList();
        string cwd;
        if (isWsl)
        {
            cwd = Environment.CurrentDirectory;
            if (r.Cwd.IsKnown && r.Cwd.Path.StartsWith("/", StringComparison.Ordinal))
            {
                arguments.Insert(0, r.Cwd.Path);
                arguments.Insert(0, "--cd");
            }
        }
        else if (r.Cwd.IsKnown && Directory.Exists(r.Cwd.Path))
        {
            cwd = r.Cwd.Path;
        }
        else
        {
            cwd = Environment.CurrentDirectory;
            if (r.Cwd.IsKnown)
            {
                _message = $"saved cwd for \"{pane.Title}\" is unavailable: {r.Cwd.Path}; started in {cwd}";
            }
            else
            {
                pane.Restore = r with
                {
                    Cwd = new WorkingDirectory(cwd, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow),
                };
            }
        }

        var term = new TerminalPaneControl(program, arguments, cwd, r.EnvOverrides);

        term.TitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(title)) { pane.Title = title; UpdateStatus(); UpdateTabBar(); }
            _session.RequestSave();
        });
        term.WorkingDirectoryChanged += path =>
        {
            pane.Restore = pane.Restore with
            {
                Cwd = new WorkingDirectory(path, CwdSource.ShellReported, DateTimeOffset.UtcNow),
            };
            _message = $"captured cwd for \"{pane.Title}\" from the shell";
            UpdateStatus();
            _session.RequestSave();
        };
        term.Exited += exitCode => Dispatcher.UIThread.Post(() =>
        {
            _message = $"pane \"{pane.Title}\" exited with code {exitCode}";
            UpdateStatus();
        });
        _ = StartTerminalAsync(term, pane, program);
        return term;
    }

    private async Task StartTerminalAsync(TerminalPaneControl terminal, Pane pane, string program)
    {
        try
        {
            await terminal.StartAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _message = $"could not start {program}: {ex.Message}";
            pane.Title = "failed: " + pane.Title;
            UpdateStatus();
            UpdateTabBar();
        }
    }

    /// <summary>
    /// What sits under a foreign window. The real application floats above it, so this is only ever
    /// seen while the app is starting or if it never appeared — which is exactly when the user needs
    /// to be told something, rather than looking at an empty rectangle.
    /// </summary>
    private static Control CreateForeignPlaceholder(Pane pane)
    {
        var text = new TextBlock
        {
            Margin = new Thickness(14),
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
            Text = $"{pane.Title}\n\nstarting…",
        };
        text.Tag = pane.Id;
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x27, 0x3a)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5a)),
            BorderThickness = new Thickness(1),
            Child = text,
        };
    }

    // ---------------- layout ----------------

    private void Relayout()
    {
        if (_canvas.Bounds.Width < 4 || _canvas.Bounds.Height < 4) return;

        var arrangement = _tree.Arrange(new CoreRect(0, 0, (int)_canvas.Bounds.Width, (int)_canvas.Bounds.Height));

        foreach (var pane in _tree.Panes)
        {
            var view = EnsureView(pane);
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

            if (_foreign.TryGetValue(pane.Id, out var app))
            {
                if (app.Located)
                {
                    var origin = _canvas.PointToScreen(new Avalonia.Point(rect.X, rect.Y));
                    var scale = TopLevel.GetTopLevel(_canvas)?.RenderScaling ?? 1.0;
                    _tracker.Place(
                        pane.Id,
                        app.Hwnd,
                        origin.X,
                        origin.Y,
                        Math.Max(1, (int)Math.Round(rect.Width * scale)),
                        Math.Max(1, (int)Math.Round(rect.Height * scale)),
                        visible,
                        child: false);
                }
                else if (view is Border { Child: TextBlock tb })
                {
                    tb.Text = $"{pane.Title}\n\n{app.Status}";
                }
            }
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
                _views.GetValueOrDefault(target)?.Focus();
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
        _actions.Register(ShellActionNames.SplitColumns, () => SplitFocused(SplitDirection.Columns));
        _actions.Register(ShellActionNames.SplitRows, () => SplitFocused(SplitDirection.Rows));
        _actions.Register(ShellActionNames.FocusLeft, () => MoveFocus(FocusDirection.Left));
        _actions.Register(ShellActionNames.FocusRight, () => MoveFocus(FocusDirection.Right));
        _actions.Register(ShellActionNames.FocusUp, () => MoveFocus(FocusDirection.Up));
        _actions.Register(ShellActionNames.FocusDown, () => MoveFocus(FocusDirection.Down));
        _actions.Register(ShellActionNames.ClosePane, () => _ = CloseFocusedAsync());
        _actions.Register(ShellActionNames.NewTab, AddTab);
        _actions.Register(ShellActionNames.NextTab, () => CycleTab(1));
        _actions.Register(ShellActionNames.PreviousTab, () => CycleTab(-1));
        _actions.Register(ShellActionNames.SaveSession, SaveSession);
        _actions.Register(ShellActionNames.ResizeLeft, () => ResizeFocused(FocusDirection.Left));
        _actions.Register(ShellActionNames.ResizeRight, () => ResizeFocused(FocusDirection.Right));
        _actions.Register(ShellActionNames.ResizeUp, () => ResizeFocused(FocusDirection.Up));
        _actions.Register(ShellActionNames.ResizeDown, () => ResizeFocused(FocusDirection.Down));
        _actions.Register(ShellActionNames.ShowPalette, ShowPalette);
        _actions.Register(ShellActionNames.SendPrefix, SendPrefix);
        _actions.Register(ShellActionNames.NewTerminalCmd, () => AddTab(TerminalProfiles.Cmd));
        _actions.Register(ShellActionNames.NewTerminalWindowsPowerShell, () => AddTab(TerminalProfiles.WindowsPowerShell));
        _actions.Register(ShellActionNames.NewTerminalPowerShell, () => AddTab(TerminalProfiles.PowerShell));
        _actions.Register(ShellActionNames.NewTerminalWsl, () => AddTab(TerminalProfiles.Wsl));
        _actions.Register(ShellActionNames.ConfigureCwdReporting, () => _ = ShowCwdIntegrationAsync(onlyIfUnseen: false));
        _actions.Register(ShellActionNames.ToggleForeignHostStrategy, () => _ = ToggleForeignHostStrategyAsync());
    }

    internal ActionDispatchResult DispatchNamedAction(string actionName)
    {
        var result = _actions.Dispatch(actionName);
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
        if (_views.GetValueOrDefault(_tree.Focused) is TerminalPaneControl terminal)
            _ = terminal.SendText("\u0002");
        else
            _message = "the focused pane does not accept terminal input";
    }

    private async Task ToggleForeignHostStrategyAsync()
    {
        if (!_foreign.TryGetValue(_tree.Focused, out var app) || app.EffectiveStrategy is not { } current)
        {
            _message = "the focused pane is not a ready foreign application";
            UpdateStatus();
            return;
        }

        var target = current == HostStrategy.Embed ? HostStrategy.Attach : HostStrategy.Embed;
        _message = $"switching {app.Pane.Title} to {target.ToString().ToLowerInvariant()}…";
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

    private Pane NewTerminalPane(TerminalProfile? profile = null)
    {
        var focused = _tree.GetPane(_tree.Focused);
        var cwd = focused?.Restore.Cwd;
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

    private void SplitFocused(SplitDirection direction)
    {
        var id = _tree.Split(_tree.Focused, direction, NewTerminalPane());
        _message = direction == SplitDirection.Columns ? "split into columns" : "split into rows";
        Relayout();
        _views.GetValueOrDefault(id)?.Focus();
    }

    private void AddTab()
    {
        var id = _tree.AddTab(_tree.Focused, NewTerminalPane());
        _message = "new tab";
        Relayout();
        _views.GetValueOrDefault(id)?.Focus();
    }

    private void AddTab(TerminalProfile profile)
    {
        var id = _tree.AddTab(_tree.Focused, NewTerminalPane(profile));
        _message = "new " + profile.Name + " tab";
        Relayout();
        _views.GetValueOrDefault(id)?.Focus();
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
            if (_views.GetValueOrDefault(_tree.Focused) is { } v) v.Focus();
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
            if (_foreign.TryGetValue(id, out var foreign))
            {
                var claimedWindow = foreign.ChildHwnd;
                _message = $"closing {foreign.Pane.Title} safely…";
                UpdateStatus();
                var result = await foreign.CloseAsync();
                if (!result.Succeeded)
                {
                    _message = "pane stayed open: " + result.Message;
                    UpdateStatus();
                    return;
                }
                lock (_claimedWindows) _claimedWindows.Remove(claimedWindow);
            }

            if (!_tree.Close(id)) { _message = "cannot close the last pane"; UpdateStatus(); return; }

            if (_views.Remove(id, out var view)) _canvas.Children.Remove(view);
            if (_foreign.Remove(id, out _)) _tracker.Forget(id);
            if (view is TerminalPaneControl term) { try { term.Dispose(); } catch (ObjectDisposedException) { } }

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
        RefreshProcessWorkingDirectories();
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
        RefreshProcessWorkingDirectories();
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
            var attempts = _foreign.Values
                .Select(app => (App: app, Child: app.ChildHwnd, Task: DetachSafelyAsync(app)))
                .ToArray();
            var detachResults = await Task.WhenAll(attempts.Select(attempt => attempt.Task));
            var failed = detachResults.FirstOrDefault(result => !result.Succeeded);
            if (failed is not null)
            {
                var removed = 0;
                for (var index = 0; index < attempts.Length; index++)
                {
                    if (!detachResults[index].Succeeded) continue;
                    var (app, child, _) = attempts[index];
                    _foreign.Remove(app.Id);
                    _tracker.Forget(app.Id);
                    if (_views.Remove(app.Id, out var view)) _canvas.Children.Remove(view);
                    lock (_claimedWindows) _claimedWindows.Remove(child);
                    _tree.Close(app.Id);
                    removed++;
                }

                _shutdownStarted = false;
                _cwdCaptureTimer.Start();
                _message = "WinMux stayed open because an application could not detach: " + failed.Message +
                           (removed > 0
                               ? $"; {removed} app(s) already detached safely and were removed from this window"
                               : string.Empty);
                Relayout();
                return;
            }

            _session.CompleteWindowClosing(this);
            _shutdown.Cancel();

            foreach (var app in _foreign.Values) _tracker.Forget(app.Id);
            foreach (var view in _views.Values)
                if (view is TerminalPaneControl t) { try { t.Dispose(); } catch (ObjectDisposedException) { } }
            _tracker.Dispose();
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

    private static async Task<ForeignAppShutdownResult> DetachSafelyAsync(ForeignAppPane app)
    {
        try
        {
            return await app.DetachAsync();
        }
        catch (Exception ex)
        {
            return new ForeignAppShutdownResult(false, ex.Message);
        }
    }

    private void RefreshProcessWorkingDirectories()
    {
        foreach (var pane in _tree.Panes.Where(candidate => candidate.Kind == PaneKind.Terminal))
        {
            if (_views.GetValueOrDefault(pane.Id) is not TerminalPaneControl terminal ||
                terminal.ProcessId is not int processId)
            {
                continue;
            }

            var isWsl = IsWsl(pane.Restore.Program);
            var result = ProcessWorkingDirectoryResolver.Resolve(processId, disablePebForWsl: isWsl);
            if (!result.Succeeded) continue;

            var capture = new WorkingDirectory(result.Path!, result.Provenance, DateTimeOffset.UtcNow);
            pane.Restore = pane.Restore with { Cwd = WorkingDirectory.Better(pane.Restore.Cwd, capture) };
        }
    }

    private static bool IsWsl(string? program) =>
        string.Equals(Path.GetFileNameWithoutExtension(program), "wsl", StringComparison.OrdinalIgnoreCase);

    private void NormalizeProgram(Pane pane)
    {
        if (string.IsNullOrWhiteSpace(pane.Restore.Program)) return;

        var resolution = ExecutablePathResolver.Resolve(pane.Restore.Program);
        if (resolution.Succeeded)
        {
            pane.Restore = pane.Restore with { Program = resolution.AbsolutePath };
        }
        else
        {
            _message = resolution.Error ?? $"could not resolve {pane.Restore.Program}";
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
