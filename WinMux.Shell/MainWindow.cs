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
using WinMux.Shell.Chrome;
using WinMux.Shell.Cwd;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.Keymap;
using WinMux.Shell.Panes;
using WinMux.Platform;
using Avalonia.Platform.Storage;
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

    /// <summary>
    /// The per-stack tab strips currently on screen, rebuilt from the arrangement each layout.
    /// There is one per stack, drawn where that stack is — see <see cref="TabStripView"/>.
    /// </summary>
    private readonly List<Control> _tabStrips = [];

    /// <summary>
    /// The visible handle in each divider gutter, rebuilt with the strips on every layout.
    ///
    /// The gutters were always draggable and always invisible: six pixels of window background
    /// with no handle and no cursor change, so nothing said a pane could be resized at all.
    /// </summary>
    private readonly List<Control> _dividerHandles = [];

    /// <summary>
    /// The ring around the focused pane, or null when nothing is focused.
    ///
    /// It lives here rather than inside the pane because it has to be drawn *in the gutter*: a pane
    /// hosting a native window paints over anything Avalonia draws inside that pane's rectangle
    /// (CLAUDE.md section 6), so a border drawn by the pane would be invisible for exactly the
    /// panes hardest to identify. Inflating into the divider gap keeps every pixel of it outside
    /// every pane.
    ///
    /// It also replaces a ring the terminal drew only when <c>IsKeyboardFocusWithin</c> — which is
    /// false whenever a dialog, the palette or a foreign pane has focus, so the mark vanished in
    /// precisely the situations where "which pane does this act on?" is worth asking.
    /// </summary>
    private Control? _focusRing;
    /// <summary>The focused pane and its captured working directory.</summary>
    private readonly TextBlock _statusLocation = new()
    {
        FontSize = Palette.CaptionSize,
        Foreground = Palette.MutedTextBrush,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>The session file and whether it is saved.</summary>
    private readonly TextBlock _statusSession = new()
    {
        FontSize = Palette.CaptionSize,
        Foreground = Palette.FaintTextBrush,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(Palette.GapLarge, 0),
    };

    /// <summary>The prefix, as a chip that lights up while the prefix is armed.</summary>
    private readonly TextBlock _statusPrefix = new()
    {
        FontSize = Palette.CaptionSize,
        Foreground = Palette.MutedTextBrush,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private Border _statusPrefixChip = null!;

    /// <summary>The last thing that happened, which clears itself rather than lingering all session.</summary>
    private readonly TextBlock _statusMessage = new()
    {
        FontSize = Palette.CaptionSize,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsVisible = false,
    };

    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private StatusMessageKind _messageKind = StatusMessageKind.Info;
    private readonly Dictionary<PaneId, IPaneRuntime> _runtimes = [];
    private readonly PaneProviderRegistry _providers;
    private readonly ForeignAppPaneProvider _foreignProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ActionDispatcher _actions = new();
    private readonly KeyBindingTable _bindings;
    private readonly KeymapRouter _keymap;

    /// <summary>
    /// The one command palette, or null when none is open.
    ///
    /// Held here rather than created per invocation because the palette used to be newed up on
    /// every dispatch: pressing the key ten times gave ten palettes, stacked, each with its own
    /// query and its own idea of what was selected.
    /// </summary>
    private CommandPaletteWindow? _palette;
    private IDisposable? _foregroundWatch;

    /// <summary>The open-windows tray, when it is up. One, like the palette and the find bar.</summary>
    private WindowPickerWindow? _windowTray;

    /// <summary>The one find bar, for the same reason the palette is one: asking twice means "find".</summary>
    private TerminalSearchWindow? _search;
    private readonly SessionController _session;
    private readonly DispatcherTimer _cwdCaptureTimer;

    private LayoutTree _tree;
    private WindowHandle _shellHwnd;
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
        _foreignProvider = new ForeignAppPaneProvider(() => _shellHwnd, PlatformServices.HostWindows);
        _providers = new PaneProviderRegistry([
            new TerminalPaneProvider(),
            new FileBrowserPaneProvider(() => Environment.CurrentDirectory),
            new EmptyPaneProvider(
                new EmptyPaneCommands(
                    (pane, profile) => Run(ReplacePaneAsync(pane, ProfilePaneFactory.Create(profile,
                        _tree.GetPane(_tree.Focused)?.Restore.Cwd), "opened " + profile.Name)),
                    pane => Run(ChooseApplicationForAsync(pane)),
                    pane => Run(AttachWindowToAsync(pane))),
                () => Settings.ShellProfiles.All),
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
        _bindings = new KeyBindingTable(keymapConfiguration);
        _keymap = new KeymapRouter(_bindings, _actions);

        Title = string.IsNullOrWhiteSpace(title) ? "WinMux" : title;
        Chrome.AppIcon.Apply(this);
        Width = tree.Bounds.Width > 0 ? Math.Max(640, tree.Bounds.Width) : 1400;
        Height = tree.Bounds.Height > 0 ? Math.Max(400, tree.Bounds.Height) : 860;
        if (!tree.Bounds.IsEmpty)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(tree.Bounds.X, tree.Bounds.Y);
        }
        // Mica: the Windows 11 backdrop, the desktop wallpaper blurred and tinted behind the app.
        // It is the single biggest reason a window reads as native, and it costs one hint plus
        // translucent chrome brushes. Windows falls back down the list on its own, and the opaque
        // fallback keeps the window readable if none of them is available.
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None,
        ];
        TransparencyBackgroundFallback = new SolidColorBrush(Palette.OpaqueWindowFallback);
        Background = Palette.WindowBrush;

        var dock = new DockPanel();
        var toolbar = ShellToolbar.Build(
            action => DispatchNamedAction(action),
            SetFocusedTabPlacement,
            (profile, split) => Run(OpenProfileAsync(profile, split)),
            () => Settings.ShellProfiles.All);
        // The toolbar is the caption row's left half rather than a bar beneath the system one.
        // Two stacked bars is the most visible way an app can fail to look like Windows 11.
        var caption = Chrome.TitleBar.Build(this, toolbar);
        DockPanel.SetDock(caption, Dock.Top);
        dock.Children.Add(caption);
        var statusBar = new Border
        {
            Background = Palette.SurfaceBrush,
            BorderBrush = Palette.EdgeBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Palette.GapMedium, 4),
            Child = BuildStatusBar(),
        };
        // Chrome lives where panes never do (CLAUDE.md section 6): a native window hosted in a pane
        // paints above anything Avalonia draws, so the status bar gets its own dock region.
        DockPanel.SetDock(statusBar, Dock.Bottom);
        dock.Children.Add(statusBar);
        dock.Children.Add(_canvas);
        Content = dock;

        // A maximised window with an extended client area is deliberately larger than the monitor,
        // by the resize border on each edge. Without this the caption buttons sit past the right
        // edge of the screen and cannot be clicked.
        dock.Bind(MarginProperty, this.GetObservable(OffScreenMarginProperty));

        EnableWindowDrop();

        // The canvas shows through the divider gutters, so it is the line between panes. The margin
        // is what makes the outermost panes tiles rather than a filled window: without it a pane
        // runs to the window edge on three sides and to the toolbar on the fourth, and the rounded
        // corners it now draws have nothing to sit against.
        _canvas.Margin = new Thickness(Palette.GapSmall, 4, Palette.GapSmall, Palette.GapSmall);
        _canvas.Background = Palette.WindowBrush;
        _canvas.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Relayout(); };

        // Focus is explicit, and the OS is the authority on it (CLAUDE.md section 6). Without this
        // the shell only ever learns about focus it caused itself, so clicking into an Explorer
        // pane left it believing a terminal elsewhere was focused — and the next key, or the next
        // "close pane", acted on that terminal.
        PlatformServices.Foreground.Changed += OnForegroundWindowChanged;
        Opened += (_, _) => _foregroundWatch ??= PlatformServices.Foreground.Start();

        // Say so when Windows could not give us the backdrop we asked for, rather than leaving
        // someone to wonder why their machine looks different from the screenshots.
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel == WindowTransparencyLevel.None)
                _message = "Mica is unavailable on this system; using an opaque window";
        };
        _canvas.PointerPressed += BeginDividerDrag;
        _canvas.PointerMoved += ContinueDividerDrag;
        _canvas.PointerReleased += EndDividerDrag;

        // Tunnel first so the shell prefix wins before a focused terminal translates Ctrl+B
        // into byte 0x02. Pass-through keys continue down to the pane normally.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            ClampRestoredGeometry();
            _shellHwnd = WindowHandle.FromPlatformValue(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
            await StartPanesAsync();
            _cwdCaptureTimer.Start();
        };
        Closing += (_, e) => Shutdown(e);
        // Moving the window changes no pane rectangle, so a full relayout — which rebuilds every
        // tab strip — is pure waste at mouse-move frequency. The one thing that must follow the
        // window is a foreign pane: its host is a separate top-level window positioned in SCREEN
        // coordinates, so it does not move with us for free.
        PositionChanged += (_, _) => FollowWindowMove();
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
        // Keep the user's name for the pane across whatever the runtime reports about itself.
        var custom = pane.Restore.TitleIsCustom ? pane.Title : null;
        pane.Restore = runtime.CaptureRestoreDescriptor();
        if (custom is not null)
        {
            pane.Title = custom;
            pane.Restore = pane.Restore with { Title = custom, TitleIsCustom = true };
        }
        else if (!string.IsNullOrWhiteSpace(pane.Restore.Title))
        {
            pane.Title = pane.Restore.Title;
        }
        if (!string.IsNullOrWhiteSpace(runtime.StatusMessage))
        {
            _message = $"{pane.Title}: {runtime.StatusMessage}";
        }
        UpdateStatus();
        UpdateTabStrips(_tree.Arrange());
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
        UpdateTabStrips(arrangement);
        _session.RequestSave();
    }

    /// <summary>
    /// Draw one tab strip per stack, in the band the layout engine reserved for it.
    ///
    /// Rebuilt wholesale on every layout rather than diffed. The strips are small and there are as
    /// many as there are stacks — usually one or two — so the cost is nothing next to the bugs that
    /// come from keeping a second model of the tree in sync with the tree.
    /// </summary>
    /// <summary>
    /// Re-assert foreign window placement after the shell window moves. Everything else in the
    /// layout is expressed in canvas coordinates and has not changed.
    /// </summary>
    private void FollowWindowMove()
    {
        if (_canvas.Bounds.Width < 4 || _canvas.Bounds.Height < 4) return;

        var arrangement = _tree.Arrange();
        foreach (var pane in _tree.Panes)
        {
            if (!_runtimes.TryGetValue(pane.Id, out var runtime)) continue;
            runtime.Arrange(new PaneArrangement(arrangement[pane.Id], arrangement.IsVisible(pane.Id)));
        }

        // The window's own bounds are persisted, so this still needs saving — just not per frame.
        _session.RequestSave();
    }

    private void UpdateTabStrips(Arrangement arrangement)
    {
        foreach (var strip in _tabStrips) _canvas.Children.Remove(strip);
        _tabStrips.Clear();
        foreach (var handle in _dividerHandles) _canvas.Children.Remove(handle);
        _dividerHandles.Clear();
        UpdateFocusRing(arrangement);

        foreach (var divider in arrangement.Dividers)
        {
            var handle = DividerHandle.Build(divider);
            Canvas.SetLeft(handle, divider.Rect.X);
            Canvas.SetTop(handle, divider.Rect.Y);
            handle.Width = divider.Rect.Width;
            handle.Height = divider.Rect.Height;
            _canvas.Children.Add(handle);
            _dividerHandles.Add(handle);
        }

        var commands = new TabStripCommands(
            Activate: FocusPane,
            Rename: pane => Run(RenamePaneAsync(pane)),
            CloseTab: pane => Run(CloseTabAsync(pane)),
            AddTab: stack => Run(AddTabToStackAsync(stack)),
            MoveStrip: SetTabPlacement,
            MoveTab: MoveTab,
            MoveTabTo: (pane, index) =>
            {
                if (_tree.MoveTabTo(pane, index)) Relayout();
            });

        foreach (var strip in arrangement.TabStrips)
        {
            var view = TabStripView.Build(strip, _tree.Focused, commands);
            Canvas.SetLeft(view, strip.Rect.X);
            Canvas.SetTop(view, strip.Rect.Y);
            view.Width = strip.Rect.Width;
            view.Height = strip.Rect.Height;
            _canvas.Children.Add(view);
            _tabStrips.Add(view);
        }
    }

    /// <summary>
    /// Mark the focused pane, in the gutter around it.
    ///
    /// Two pixels, inflated outward, so the ring occupies the divider gap and the canvas margin and
    /// never a single pixel of any pane. <c>DividerThickness</c> is 6, so two adjacent rings still
    /// leave two pixels of gutter between them.
    /// </summary>
    private void UpdateFocusRing(Arrangement arrangement)
    {
        if (_focusRing is not null)
        {
            _canvas.Children.Remove(_focusRing);
            _focusRing = null;
        }

        if (!arrangement.IsVisible(_tree.Focused)) return;
        var rect = arrangement[_tree.Focused];
        if (rect.Width <= 0 || rect.Height <= 0) return;

        const int Thickness = 2;
        var ring = new Border
        {
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(Thickness),
            CornerRadius = new CornerRadius(10),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ring, rect.X - Thickness);
        Canvas.SetTop(ring, rect.Y - Thickness);
        ring.Width = rect.Width + Thickness * 2;
        ring.Height = rect.Height + Thickness * 2;

        // Before the pane views in z-order, so a native pane window still covers nothing of it and
        // an Avalonia pane does not paint over it either.
        _canvas.Children.Insert(0, ring);
        _focusRing = ring;
    }

    /// <summary>
    /// A button click cannot await. Without this the task is unobserved, so a provider that threw
    /// while creating a pane would leave the user looking at a button that did nothing and said
    /// nothing — exactly the silent failure CLAUDE.md section 8 rules out.
    /// </summary>
    private void Run(Task work) => _ = Observe(work);

    private async Task Observe(Task work)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _message = ex.Message;
            UpdateStatus();
        }
    }

    /// <summary>
    /// Name a pane, or hand it back to whatever it wants to call itself.
    ///
    /// tmux binds this to `prefix ,` and so do we, because the people most likely to want it are
    /// the people who already have that in their fingers.
    /// </summary>
    internal async Task RenamePaneAsync(PaneId target)
    {
        if (_tree.GetPane(target) is not { } pane)
        {
            _message = "no pane to rename";
            UpdateStatus();
            return;
        }

        var prompt = new PromptWindow(
            "Rename pane",
            "What should this pane be called? The name stays put even when the program inside " +
            "changes its own title, and it is saved with the session.",
            pane.Title,
            clearLabel: pane.Restore.TitleIsCustom ? "Use automatic name" : null);

        await prompt.ShowDialog(this);

        if (prompt.Cleared)
        {
            // Back to automatic. The next title the runtime reports wins, and until then the
            // current text stays so the tab does not go blank.
            pane.Restore = pane.Restore with { TitleIsCustom = false };
            _message = "pane will use its automatic name again";
        }
        else if (prompt.Result is { } name)
        {
            if (name.Length == 0)
            {
                _message = "a pane name cannot be empty";
                UpdateStatus();
                return;
            }

            pane.Title = name;
            pane.Restore = pane.Restore with { Title = name, TitleIsCustom = true };
            _message = "renamed to " + name;
        }
        else
        {
            return;
        }

        Relayout();
        _session.RequestSave();
    }

    private void FocusPane(PaneId pane)
    {
        _tree.Focus(pane);
        _runtimes.GetValueOrDefault(pane)?.Focus();
        Relayout();
    }

    /// <summary>Closing a tab is closing its pane, and goes through the same path.</summary>
    private async Task CloseTabAsync(PaneId pane)
    {
        FocusPane(pane);
        await CloseFocusedAsync();
    }

    private Task AddTabToStackAsync(StackNode stack)
    {
        // Add beside the stack's active tab so the new one lands in this stack rather than
        // wherever focus happens to be — the user clicked a specific "+".
        var beside = stack.Active.Leaves().First().Pane.Id;
        return AddTabAsync(NewTerminalPane(), "new tab", beside);
    }

    private void SetTabPlacement(StackNode stack, TabStripPlacement placement)
    {
        if (stack.TabStrip == placement) return;
        stack.TabStrip = placement;
        _message = $"tabs moved to the {placement.ToString().ToLowerInvariant()}";
        Relayout();
    }

    /// <summary>The toolbar and keymap act on whichever stack holds the focused pane.</summary>
    private void SetFocusedTabPlacement(TabStripPlacement placement)
    {
        if (FindEnclosingStack(_tree.Find(_tree.Focused)) is not { } stack)
        {
            _message = "the focused pane is not in a tab group yet — use Tab this pane first";
            UpdateStatus();
            return;
        }
        SetTabPlacement(stack, placement);
    }

    /// <summary>A tab group whose tabs run down the side, created in one step.</summary>
    private async Task AddVerticalTabAsync()
    {
        await AddTabAsync(NewTerminalPane(), "new tab, tabs on the left");
        if (FindEnclosingStack(_tree.Find(_tree.Focused)) is { } stack)
        {
            stack.TabStrip = TabStripPlacement.Left;
            Relayout();
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

    /// <summary>
    /// Three segments rather than one debug string: where you are, whether it is saved, and what
    /// the prefix will do. See <see cref="StatusBarModel"/> for why those three.
    /// </summary>
    private Control BuildStatusBar()
    {
        _statusPrefixChip = new Border
        {
            CornerRadius = Palette.ControlRadius,
            Padding = new Thickness(Palette.GapSmall, 2),
            Child = _statusPrefix,
        };

        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            ],
        };

        var left = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = Palette.GapMedium,
            Children = { _statusLocation, _statusMessage },
        };

        Grid.SetColumn(left, 0);
        Grid.SetColumn(_statusSession, 1);
        Grid.SetColumn(_statusPrefixChip, 2);
        grid.Children.Add(left);
        grid.Children.Add(_statusSession);
        grid.Children.Add(_statusPrefixChip);

        // A message that never clears is indistinguishable from the current state.
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            _message = "";
            UpdateStatus();
        };

        // "saved 4s ago" has to keep counting, or it is a timestamp pretending to be a status.
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        return grid;
    }

    private void UpdateStatus()
    {
        var focused = _tree.GetPane(_tree.Focused);
        var text = StatusBarModel.Build(
            focused?.Title,
            focused?.Restore.Cwd,
            _session.SessionPath,
            _session.LastSavedAt,
            saving: false,
            prefixGesture: "Ctrl+B",
            armed: _keymap.IsPrefixArmed,
            now: DateTimeOffset.UtcNow);

        _statusLocation.Text = text.Location;
        _statusSession.Text = text.Session;
        _statusPrefix.Text = text.Prefix;

        // Armed is a mode. A mode with no visible state is how a stray keystroke closes a pane.
        _statusPrefixChip.Background = _keymap.IsPrefixArmed ? Palette.AccentBrush : Brushes.Transparent;
        _statusPrefix.Foreground = _keymap.IsPrefixArmed ? Palette.TextBrush : Palette.MutedTextBrush;

        _statusMessage.Text = _message;
        _statusMessage.IsVisible = _message.Length > 0;
        _statusMessage.Foreground = _messageKind == StatusMessageKind.Error
            ? Palette.DangerBrush
            : Palette.MutedTextBrush;

        if (_message.Length > 0)
        {
            _messageTimer.Stop();
            _messageTimer.Start();
        }
    }

    // ---------------- keys ----------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // KeySymbol is what this keyboard produced, which is the only way a binding written as "%"
        // or ":" can work on a layout that does not put them where a US keyboard does.
        var route = _keymap.Route(new KeyStroke(e.Key, e.KeyModifiers), e.KeySymbol);
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
        _actions.RegisterAsync(ShellActionNames.NewTabVertical, _ => new ValueTask(AddVerticalTabAsync()));
        _actions.RegisterAsync(ShellActionNames.RenamePane, _ => new ValueTask(RenamePaneAsync(_tree.Focused)));
        _actions.RegisterAsync(ShellActionNames.NewEmptyPane,
            _ => new ValueTask(AddTabAsync(Pane.Empty(), "new empty pane")));
        _actions.Register(ShellActionNames.MoveTabsTop, () => SetFocusedTabPlacement(TabStripPlacement.Top));
        _actions.Register(ShellActionNames.MoveTabsBottom, () => SetFocusedTabPlacement(TabStripPlacement.Bottom));
        _actions.Register(ShellActionNames.MoveTabsLeft, () => SetFocusedTabPlacement(TabStripPlacement.Left));
        _actions.Register(ShellActionNames.MoveTabsRight, () => SetFocusedTabPlacement(TabStripPlacement.Right));
        _actions.Register(ShellActionNames.NextTab, () => CycleTab(1));
        _actions.Register(ShellActionNames.PreviousTab, () => CycleTab(-1));
        _actions.RegisterAsync(ShellActionNames.ShowSettings, _ => new ValueTask(ShowSettingsAsync()));
        _actions.RegisterAsync(ShellActionNames.OpenSession, _ => new ValueTask(OpenSessionAsync()));
        _actions.Register(ShellActionNames.SaveSession, SaveSession);
        _actions.RegisterAsync(ShellActionNames.SaveSessionAs, _ => new ValueTask(SaveSessionAsAsync()));
        _actions.Register(ShellActionNames.ResizeLeft, () => ResizeFocused(FocusDirection.Left));
        _actions.Register(ShellActionNames.ResizeRight, () => ResizeFocused(FocusDirection.Right));
        _actions.Register(ShellActionNames.ResizeUp, () => ResizeFocused(FocusDirection.Up));
        _actions.Register(ShellActionNames.ResizeDown, () => ResizeFocused(FocusDirection.Down));
        _actions.Register(ShellActionNames.ShowPalette, ShowPalette);
        _actions.Register(ShellActionNames.FindInPane, ShowSearch);
        _actions.Register(ShellActionNames.ShowOpenWindows, ShowWindowTray);
        _actions.Register(ShellActionNames.MoveTabEarlier, () => MoveTab(-1));
        _actions.Register(ShellActionNames.MoveTabLater, () => MoveTab(1));
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
        // Asking for the palette while it is open means "I want the palette", not "I want another
        // palette". Raise the one that exists.
        if (_palette is { } open)
        {
            open.Activate();
            return;
        }

        var palette = new CommandPaletteWindow(
            _actions.RegisteredActions,
            action => DispatchNamedAction(action),
            _bindings);
        palette.Closed += (_, _) => _palette = null;
        _palette = palette;
        palette.ShowOver(this);
    }

    /// <summary>
    /// Follow the OS when it moves focus to a window one of our panes is standing in for.
    ///
    /// Only ever *records* the change; it never calls Focus back, because the window already has
    /// focus and asking for it again would be a cross-process window call on the UI thread, which
    /// ADR 0001 measured freezing the shell for six seconds against a wedged application.
    ///
    /// A window that belongs to no pane — the user alt-tabbed to their browser — is not interesting:
    /// WinMux's focused pane is where its keys will go when it is focused again, and moving it
    /// because the user left would be wrong.
    /// </summary>
    private void OnForegroundWindowChanged(ForegroundWindow foreground)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_shutdownStarted) return;

            var hosted = _runtimes
                .Where(entry => entry.Value is IHostedWindowPane)
                .Select(entry => (entry.Key, (IHostedWindowPane)entry.Value));

            if (FocusReconciliation.PaneFor(foreground, hosted, _tree.Focused) is not { } pane) return;

            _tree.Focus(pane);
            UpdateStatus();
            UpdateTabStrips(_tree.Arrange());
        });
    }

    /// <summary>
    /// The tray of windows that are already open, which can be dragged onto a pane.
    ///
    /// Modeless on purpose: a modal dialog makes its owner uninteractive, so there would be nothing
    /// to drag onto. The existing modal picker stays for "attach into *this* pane", which is a
    /// different question with a different answer.
    /// </summary>
    private void ShowWindowTray()
    {
        if (_windowTray is { } open)
        {
            open.Activate();
            return;
        }

        var tray = new WindowPickerWindow(PlatformServices.Windows, [_shellHwnd, .. ClaimedWindows()], draggable: true);
        tray.Closed += (_, _) => _windowTray = null;
        _windowTray = tray;
        tray.Show(this);
        ShowMessage("drag a window onto a pane to put it there");
    }

    /// <summary>
    /// Accept a window dragged from the tray, into whichever pane it was dropped on.
    ///
    /// The drop point is in canvas coordinates and the arrangement is too, so the pane is found by
    /// asking the layout rather than by hit-testing controls — a pane hosting a native window has
    /// no Avalonia control under the pointer to find.
    /// </summary>
    private void EnableWindowDrop()
    {
        DragDrop.SetAllowDrop(_canvas, true);

        _canvas.AddHandler(DragDrop.DragOverEvent, (object? _, DragEventArgs e) =>
        {
            e.DragEffects = PaneAt(e.GetPosition(_canvas)) is null
                ? DragDropEffects.None
                : DragDropEffects.Move;
            e.Handled = true;
        });

        _canvas.AddHandler(DragDrop.DropEvent, (object? _, DragEventArgs e) =>
        {
            e.Handled = true;
            if (e.DataTransfer?.TryGetValue(WindowPickerWindow.WindowDragFormat) is not { } dragged) return;
            if (PaneAt(e.GetPosition(_canvas)) is not { } pane) return;

            Run(ReplacePaneAsync(pane, AdoptedPane(dragged.Window), "attached " + dragged.Window.Title));
        });
    }

    /// <summary>Which visible pane covers a point in canvas coordinates.</summary>
    private PaneId? PaneAt(Avalonia.Point point)
    {
        var arrangement = _tree.Arrange();
        foreach (var pane in _tree.Panes)
        {
            if (!arrangement.IsVisible(pane.Id)) continue;
            var rect = arrangement[pane.Id];
            if (point.X >= rect.X && point.X < rect.X + rect.Width &&
                point.Y >= rect.Y && point.Y < rect.Y + rect.Height)
            {
                return pane.Id;
            }
        }

        return null;
    }

    /// <summary>Reorder the focused tab within its strip.</summary>
    private void MoveTab(int delta)
    {
        if (!_tree.MoveTab(delta))
        {
            _message = "the focused pane cannot move any further in its tab group";
            UpdateStatus();
            return;
        }

        Relayout();
    }

    /// <summary>
    /// Open the find bar over the focused terminal.
    ///
    /// Only terminals: a file browser has its own filtering and a foreign application is not ours
    /// to search. Saying so is better than opening a bar that would never match anything.
    /// </summary>
    private void ShowSearch()
    {
        if (_search is { } open)
        {
            open.Activate();
            return;
        }

        if (_runtimes.GetValueOrDefault(_tree.Focused)?.View is not TerminalPaneControl terminal)
        {
            ShowMessage("the focused pane is not a terminal, so there is nothing to search");
            return;
        }

        var bounds = _tree.Arrange(new CoreRect(0, 0, (int)_canvas.Bounds.Width, (int)_canvas.Bounds.Height))
            [_tree.Focused];

        var search = new TerminalSearchWindow(terminal);
        search.Closed += (_, _) => _search = null;
        _search = search;
        search.ShowOver(this, new Avalonia.Rect(
            _canvas.Bounds.X + bounds.X,
            _canvas.Bounds.Y + bounds.Y,
            bounds.Width,
            bounds.Height));
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
        // Inherit from the focused terminal first — "another one of these" is almost always what a
        // split means — and fall back to the profile the user chose in settings.
        profile ??= focused?.Kind == PaneKind.Terminal && !string.IsNullOrWhiteSpace(focused.Restore.Program)
            ? new TerminalProfile(focused.Title, focused.Restore.Program!, focused.Restore.Args)
            : Settings.ShellProfiles.DefaultTerminal() is { } configured
                ? new TerminalProfile(configured.Name, configured.Program, configured.Args)
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

    private Task SplitFocusedAsync(SplitDirection direction) =>
        SplitFocusedAsync(direction, NewTerminalPane(),
            direction == SplitDirection.Columns ? "split into columns" : "split into rows");

    private async Task SplitFocusedAsync(SplitDirection direction, Pane pane, string message)
    {
        var runtime = await CreateNewRuntimeAsync(pane);
        var id = _tree.Split(_tree.Focused, direction, pane);
        AddRuntime(pane, runtime);
        _message = message;
        Relayout();
        _runtimes.GetValueOrDefault(id)?.Focus();
    }

    /// <summary>
    /// Open a profile in a new tab. The single entry point every surface uses, so the toolbar, the
    /// palette, an empty pane's launcher and the CLI cannot drift apart.
    /// </summary>
    internal Task OpenProfileAsync(Core.Settings.LaunchProfile profile, bool split = false,
        SplitDirection direction = SplitDirection.Columns)
    {
        var inherited = _tree.GetPane(_tree.Focused)?.Restore.Cwd;
        var pane = ProfilePaneFactory.Create(profile, inherited);
        return split
            ? SplitFocusedAsync(direction, pane, $"opened {profile.Name}")
            : AddTabAsync(pane, "opened " + profile.Name);
    }

    internal Task OpenProfileAsync(string profileId)
    {
        if (Settings.ShellProfiles.ById(profileId) is not { } profile)
        {
            _message = $"no profile called \"{profileId}\"";
            UpdateStatus();
            return Task.CompletedTask;
        }
        return OpenProfileAsync(profile);
    }

    /// <summary>
    /// Swap one pane for another in place, keeping its position in the tree.
    ///
    /// This is how an empty pane becomes something: the layout the user built stays exactly as it
    /// is, and only the contents change.
    /// </summary>
    private async Task ReplacePaneAsync(PaneId target, Pane replacement, string message)
    {
        if (_tree.Find(target) is null) return;

        var runtime = await CreateNewRuntimeAsync(replacement);

        if (_runtimes.TryGetValue(target, out var existing))
        {
            var closed = await CloseRuntimeSafelyAsync(existing, PaneCloseReason.PaneRemoved);
            if (!closed.Succeeded)
            {
                await runtime.DisposeAsync();
                _message = "kept the pane because it could not close safely: " + closed.Message;
                UpdateStatus();
                return;
            }
            await RemoveRuntimeAsync(target);
        }

        _tree.ReplacePane(target, replacement);
        AddRuntime(replacement, runtime);
        _tree.Focus(replacement.Id);
        _message = message;
        Relayout();
        _runtimes.GetValueOrDefault(replacement.Id)?.Focus();
    }

    private async Task ChooseApplicationForAsync(PaneId target)
    {
        var picker = new AppPickerWindow(PlatformServices.Apps);
        await picker.ShowDialog(this);
        if (picker.Result is not { } app) return;

        // Opened without becoming a profile: putting something in a pane once should not require
        // curating a list first. "Add application…" in Settings is there when it is worth keeping.
        await ReplacePaneAsync(target, ProfilePaneFactory.Create(new Core.Settings.LaunchProfile
        {
            Id = Core.Settings.LaunchProfile.MakeId(app.Name),
            Name = app.Name,
            Kind = Core.Settings.ProfileKind.Application,
            Program = app.Program,
            Args = app.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            WorkingDirectory = app.WorkingDirectory,
        }), "opened " + app.Name);
    }

    private async Task AttachWindowToAsync(PaneId target)
    {
        var picker = new WindowPickerWindow(PlatformServices.Windows, [_shellHwnd, .. ClaimedWindows()]);
        await picker.ShowDialog(this);
        if (picker.Result is not { } window) return;

        await ReplacePaneAsync(target, AdoptedPane(window), "attached " + window.Title);
    }

    /// <summary>
    /// A pane for a window that already exists.
    ///
    /// The handle goes in as a launch-time instruction, and the program path goes in as the restore
    /// descriptor — so this session adopts the window that is there now, and the next session starts
    /// the same program instead of chasing a handle that will not exist. Without a readable path,
    /// which an elevated process will not give, restoring lands on an empty pane that says why.
    /// </summary>
    private static Pane AdoptedPane(Platform.AdoptableWindow window)
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adopt_window"] = window.Window.Value.ToString(),
        };
        if (window.ProgramPath.Length == 0)
            extras["note"] = "This pane held an attached window. WinMux could not read that " +
                             "program's path, so it cannot be reopened automatically.";

        return new Pane(PaneId.New(), PaneKind.ForeignApp, window.Title, new RestoreDescriptor
        {
            Kind = PaneKind.ForeignApp,
            Title = window.Title,
            Program = window.ProgramPath,
            Strategy = HostStrategy.Embed,
            Extras = extras,
        });
    }

    private IEnumerable<WindowHandle> ClaimedWindows() =>
        _runtimes.Keys.Select(id => _tree.GetPane(id)).OfType<Pane>()
            .Select(pane => pane.Restore.Extras.TryGetValue("adopt_window", out var text) &&
                            long.TryParse(text, out var value)
                ? WindowHandle.FromPlatformValue((nint)value)
                : WindowHandle.None)
            .Where(handle => !handle.IsNone);

    private Task AddTabAsync() => AddTabAsync(NewTerminalPane(), "new tab");

    private Task AddTabAsync(TerminalProfile profile) =>
        AddTabAsync(NewTerminalPane(profile), "new " + profile.Name + " tab");

    private Task AddFileBrowserTabAsync() =>
        AddTabAsync(NewFileBrowserPane(), "new file-browser tab");

    private async Task AddTabAsync(Pane pane, string message, PaneId? beside = null)
    {
        var runtime = await CreateNewRuntimeAsync(pane);
        var target = beside ?? _tree.Focused;
        var existing = FindEnclosingStack(_tree.Find(target));
        var id = _tree.AddTab(target, pane);

        // A group that already existed keeps whatever placement it was given; only a brand new one
        // takes the default, so changing the setting never rearranges someone's open layout.
        if (existing is null && FindEnclosingStack(_tree.Find(id)) is { } created)
            created.TabStrip = Settings.ShellSettings.Current.DefaultTabPlacement;
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

    private async Task ShowSettingsAsync()
    {
        var dialog = new SettingsWindow(
            Settings.ShellSettings.Current,
            Settings.ShellProfiles.All,
            _session.SessionPath,
            Settings.ShellSettings.Path,
            Settings.ShellProfiles.Path);

        await dialog.ShowDialog(this);

        if (dialog.Result is { } chosen)
        {
            var problems = new List<string>();
            if (dialog.Profiles is { } editedProfiles &&
                Settings.ShellProfiles.Replace(editedProfiles) is { } profileError)
            {
                problems.Add(profileError);
            }
            if (Settings.ShellSettings.Update(chosen) is { } settingsError) problems.Add(settingsError);

            _message = problems.Count == 0 ? "settings saved" : string.Join("; ", problems);
            UpdateStatus();
        }

        // The cwd page is its own dialog, and stacking modals on top of each other is how people
        // lose track of which one they are answering.
        if (dialog.OpenCwdReporting) await ShowCwdIntegrationAsync(onlyIfUnseen: false);
    }

    /// <summary>
    /// Open a saved layout, replacing the one on screen.
    ///
    /// Destructive by nature — the panes you are looking at have to close first — so it asks, and
    /// it validates the file **before** touching anything live. A file that will not parse leaves
    /// the current session exactly as it was.
    ///
    /// Foreign applications are detached rather than killed, the same as at shutdown: they were
    /// adopted, and adopting something is not a licence to close it.
    /// </summary>
    private async Task OpenSessionAsync()
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open session",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("WinMux session") { Patterns = ["*.toml"] },
            ],
        });

        if (picked.Count == 0)
        {
            _message = "open cancelled";
            UpdateStatus();
            return;
        }

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            _message = "that location is not a file WinMux can read";
            UpdateStatus();
            return;
        }

        // Read and validate first. SessionFile.Load quarantines and refuses a malformed file rather
        // than starting empty (ADR 0006), and there is no reason to close a single pane until the
        // replacement is known to be good.
        SessionSnapshot snapshot;
        try
        {
            snapshot = SessionFile.Load(path);
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException)
        {
            _message = "could not open that session: " + ex.Message;
            UpdateStatus();
            return;
        }

        if (_session.WindowCount > 1)
        {
            _message = "close the other WinMux windows first; opening a session replaces all of them";
            UpdateStatus();
            return;
        }

        var extra = snapshot.Windows.Count > 1
            ? Environment.NewLine + Environment.NewLine +
              $"That session has {snapshot.Windows.Count} windows. Only the first is opened here; " +
              "the rest stay in the file until you save."
            : string.Empty;

        var confirmed = !Settings.ShellSettings.Current.ConfirmBeforeClosingPanes || await NoticeWindow.ConfirmAsync(
            this,
            "Open this session?",
            $"The {_tree.Panes.Count()} pane(s) in this window will be closed first. Terminals end; " +
            "foreign applications are detached and keep running." + extra,
            "Open session");
        if (!confirmed)
        {
            _message = "open cancelled";
            UpdateStatus();
            return;
        }

        await ReplaceSessionAsync(snapshot, path);
    }

    private async Task ReplaceSessionAsync(SessionSnapshot snapshot, string path)
    {
        _cwdCaptureTimer.Stop();
        _message = "closing panes safely…";
        UpdateStatus();

        var runtimes = _runtimes.Values.ToArray();
        var results = await Task.WhenAll(
            runtimes.Select(runtime => CloseRuntimeSafelyAsync(runtime, PaneCloseReason.ShellShutdown)));

        if (results.FirstOrDefault(result => !result.Succeeded) is { } failed)
        {
            _cwdCaptureTimer.Start();
            _message = "kept the current session because a pane could not close safely: " + failed.Message;
            UpdateStatus();
            return;
        }

        foreach (var runtime in runtimes)
        {
            _canvas.Children.Remove(runtime.View);
            await runtime.DisposeAsync();
        }
        _runtimes.Clear();

        // Point the autosaver at the new file before the first save, and without writing the old
        // layout into it.
        var switched = await _session.SwitchFileAsync(path);
        if (!switched.Succeeded)
        {
            _message = "could not switch session file: " + switched.Error?.Message;
            UpdateStatus();
        }

        var window = snapshot.Windows[0];
        _tree = SessionMapper.FromSnapshot(window);
        Title = string.IsNullOrWhiteSpace(window.Title) ? "WinMux" : window.Title;

        await StartPanesAsync();
        _cwdCaptureTimer.Start();
        _message = "opened " + _session.SessionPath;
        UpdateStatus();
    }

    /// <summary>
    /// Save the layout to a file the user picks, and keep working there afterwards — Save As, not
    /// "export a copy". The controller moves the autosaver with it.
    /// </summary>
    private async Task SaveSessionAsAsync()
    {
        var suggested = Path.GetFileName(_session.SessionPath);
        var startIn = Path.GetDirectoryName(_session.SessionPath);

        var picked = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save session as",
            SuggestedFileName = string.IsNullOrWhiteSpace(suggested) ? "session.toml" : suggested,
            DefaultExtension = "toml",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("WinMux session") { Patterns = ["*.toml"] },
            ],
            SuggestedStartLocation = string.IsNullOrWhiteSpace(startIn)
                ? null
                : await StorageProvider.TryGetFolderFromPathAsync(startIn),
        });

        if (picked is null)
        {
            _message = "save as cancelled";
            UpdateStatus();
            return;
        }

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            _message = "that location is not a file WinMux can write to";
            UpdateStatus();
            return;
        }

        RefreshPaneRestoreStates();
        var result = await _session.SaveAsAsync(path);
        _message = result.Succeeded
            ? "session is now " + _session.SessionPath
            : "could not save as: " + result.Error?.Message;
        UpdateStatus();
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

    /// <summary>How many panes this window holds, for the restore notice.</summary>
    internal int PaneCount => _tree.Panes.Count();

    internal void ShowMessage(string message, StatusMessageKind kind = StatusMessageKind.Info)
    {
        _message = message;
        _messageKind = kind;
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
            var custom = pane.Restore.TitleIsCustom ? pane.Title : null;
            pane.Restore = runtime.CaptureRestoreDescriptor();
            if (custom is not null)
            {
                pane.Title = custom;
                pane.Restore = pane.Restore with { Title = custom, TitleIsCustom = true };
            }
            else if (!string.IsNullOrWhiteSpace(pane.Restore.Title))
            {
                pane.Title = pane.Restore.Title;
            }
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
