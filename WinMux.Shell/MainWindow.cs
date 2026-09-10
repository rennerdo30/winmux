using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Terminal.Avalonia;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using CoreRect = WinMux.Core.Layout.Rect;

namespace WinMux.Shell;

/// <summary>
/// The shell window: one OS window holding the layout tree.
///
/// Terminal panes are Avalonia controls. Foreign-app panes are NOT — they stay top-level windows
/// driven to follow their pane rectangle (attach mode), because ADR 0001 forbids the shell from
/// reparenting a pane host into itself while the input-queue question is unmeasured, and forbids
/// the UI thread from making synchronous window calls against foreign windows at all. All such
/// calls go through <see cref="ForeignWindowTracker"/> on its own thread.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 3), FontSize = 12 };
    private readonly ForeignWindowTracker _tracker = new();
    private readonly Dictionary<PaneId, Control> _views = [];
    private readonly Dictionary<PaneId, ForeignAppPane> _foreign = [];
    /// <summary>Windows already adopted by some pane, so two panes cannot claim the same one.</summary>
    private readonly HashSet<IntPtr> _claimedWindows = [];
    private readonly CancellationTokenSource _shutdown = new();

    private LayoutTree _tree;
    private IntPtr _shellHwnd;
    private string _sessionPath;
    private bool _prefixArmed;
    private string _message = "";

    public MainWindow(LayoutTree tree, string sessionPath)
    {
        _tree = tree;
        _sessionPath = sessionPath;

        Title = "WinMux";
        Width = 1400;
        Height = 860;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));

        var dock = new DockPanel();
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

        KeyDown += OnKeyDown;
        Opened += async (_, _) =>
        {
            _shellHwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await StartPanesAsync();
        };
        Closing += (_, _) => Shutdown();
        PositionChanged += (_, _) => Relayout();
        // Attach-mode windows sit above the shell but are not owned by it, so activating the shell
        // buries them. Re-assert placement (and z-order) whenever we come forward.
        Activated += (_, _) => { _tracker.Refresh(); Relayout(); };
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
            await app.LaunchAsync(_claimedWindows, _shutdown.Token);

            // Embed, always. Attach mode is not a runtime fallback: a window that merely follows
            // the pane keeps its own title bar and close button and is not contained at all.
            if (app.Located) AdoptIntoLayout(pane, app);
            Relayout();
        }
        Relayout();
    }

    private Control EnsureView(Pane pane)
    {
        if (_views.TryGetValue(pane.Id, out var existing)) return existing;

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
        var term = new TerminalControl
        {
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Courier New,monospace"),
            FontSize = 13,
            ScrollbackCapacity = 10_000,
        };

        var r = pane.Restore;
        var program = string.IsNullOrWhiteSpace(r.Program)
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : r.Program;
        var cwd = r.Cwd.IsKnown && Directory.Exists(r.Cwd.Path) ? r.Cwd.Path : Environment.CurrentDirectory;

        term.TitleChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(term.Title)) { pane.Title = term.Title!; UpdateStatus(); }
        });
        term.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _message = $"pane \"{pane.Title}\" exited";
            UpdateStatus();
        });

        try
        {
            term.Start(new Terminal.Pty.PtyOptions
            {
                Command = program,
                Arguments = r.Args.ToArray(),
                WorkingDirectory = cwd,
                Columns = 80,
                Rows = 25,
            }, []);
        }
        catch (Exception ex)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x5a, 0x2a, 0x2a)),
                Child = new TextBlock
                {
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"could not start {program}\n\n{ex.Message}",
                },
            };
        }
        return term;
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

            // An adopted window is a NativeControlHost and rides the layout like any other control.
            // Only a pane still waiting on its application needs anything said about it.
            if (_foreign.TryGetValue(pane.Id, out var app) && !app.Located &&
                view is Border { Child: TextBlock tb })
            {
                tb.Text = $"{pane.Title}\n\n{app.Status}";
            }
        }

        UpdateStatus();
    }

    /// <summary>
    /// Replace the pane's placeholder with a host that owns the application's window.
    ///
    /// Avalonia reparents and positions it from here on, so there is nothing for the tracker
    /// thread to do: an embedded window is a child control like any other and rides the layout.
    /// </summary>
    private void AdoptIntoLayout(Pane pane, ForeignAppPane app)
    {
        app.StripFrame();

        if (_views.Remove(pane.Id, out var placeholder)) _canvas.Children.Remove(placeholder);

        var host = new ForeignHost(app.Hwnd);
        _views[pane.Id] = host;
        _canvas.Children.Add(host);
        _tracker.Forget(pane.Id);
    }

    private void UpdateStatus()
    {
        var focused = _tree.GetPane(_tree.Focused);
        var prefix = _prefixArmed ? "  [PREFIX]" : "";
        var hint = _prefixArmed
            ? "  %  split ┃    \"  split ━    ←↑↓→ focus    x  close    c  tab    n/p  cycle    w  write"
            : "  Ctrl+B then a key";
        _status.Text =
            $"{_tree.Panes.Count()} panes   focus: {focused?.Title ?? "-"}{prefix}{hint}" +
            (_message.Length > 0 ? "   |   " + _message : "");
    }

    // ---------------- keys ----------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // tmux-style prefix (CLAUDE.md section 6). Without one, every useful binding collides with
        // a key the terminal legitimately wants -- Ctrl+D, Ctrl+W and friends belong to the shell.
        if (!_prefixArmed)
        {
            if (e.Key == Key.B && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _prefixArmed = true;
                _message = "";
                UpdateStatus();
                e.Handled = true;
            }
            return;
        }

        _prefixArmed = false;
        e.Handled = true;

        switch (e.Key)
        {
            case Key.D5 when e.KeyModifiers.HasFlag(KeyModifiers.Shift):   // '%'
            case Key.V:
                SplitFocused(SplitDirection.Columns);
                break;

            case Key.OemQuotes:                                            // '"'
            case Key.S:
                SplitFocused(SplitDirection.Rows);
                break;

            case Key.Left: MoveFocus(FocusDirection.Left); break;
            case Key.Right: MoveFocus(FocusDirection.Right); break;
            case Key.Up: MoveFocus(FocusDirection.Up); break;
            case Key.Down: MoveFocus(FocusDirection.Down); break;

            case Key.X: CloseFocused(); break;
            case Key.C: AddTab(); break;
            case Key.N: CycleTab(1); break;
            case Key.P: CycleTab(-1); break;
            case Key.W: SaveSession(); break;

            case Key.B:
                // Ctrl+B B sends a literal Ctrl+B to the pane, as tmux does.
                if (_views.GetValueOrDefault(_tree.Focused) is TerminalControl t) t.SendText("");
                break;

            default:
                _message = "no binding for " + e.Key;
                break;
        }
        UpdateStatus();
    }

    private Pane NewTerminalPane()
    {
        var focused = _tree.GetPane(_tree.Focused);
        var cwd = focused?.Restore.Cwd;
        return Pane.Terminal("shell",
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            cwd is { IsKnown: true }
                ? new WorkingDirectory(cwd.Path, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow)
                : null);
    }

    private void SplitFocused(SplitDirection direction)
    {
        _tree.Split(_tree.Focused, direction, NewTerminalPane());
        _message = direction == SplitDirection.Columns ? "split into columns" : "split into rows";
        Relayout();
    }

    private void AddTab()
    {
        _tree.AddTab(_tree.Focused, NewTerminalPane());
        _message = "new tab";
        Relayout();
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

    private void CloseFocused()
    {
        var id = _tree.Focused;
        if (!_tree.Close(id)) { _message = "cannot close the last pane"; UpdateStatus(); return; }

        if (_views.Remove(id, out var view)) _canvas.Children.Remove(view);
        if (_foreign.Remove(id, out var app)) { app.Close(); _tracker.Forget(id); }
        if (view is TerminalControl term) { try { term.Dispose(); } catch (ObjectDisposedException) { } }

        _message = "closed";
        Relayout();
    }

    private void SaveSession()
    {
        try
        {
            SessionFile.Save(_sessionPath, new SessionSnapshot
            {
                SavedAt = DateTimeOffset.UtcNow,
                Windows = [SessionMapper.ToSnapshot(_tree, Title ?? "main")],
            });
            _message = "wrote " + _sessionPath;
        }
        catch (Exception ex)
        {
            // No silent failure around persistence (CLAUDE.md section 8).
            _message = "could not write session: " + ex.Message;
        }
    }

    private void Shutdown()
    {
        _shutdown.Cancel();

        // Detach every embedded window BEFORE this window is destroyed. A parent takes its children
        // with it, and spike 3 measured exactly that: the app survives as a process with no window,
        // which from the user's side is the application being lost. Bounded, because detaching
        // touches a foreign window and a wedged app must not stop WinMux from closing.
        var detach = new Thread(() =>
        {
            foreach (var app in _foreign.Values)
            {
                try { app.Detach(); } catch (Exception) { /* nothing useful to do while closing */ }
            }
        }) { IsBackground = true, Name = "winmux-detach" };
        detach.Start();
        detach.Join(TimeSpan.FromSeconds(3));

        foreach (var app in _foreign.Values) _tracker.Forget(app.Id);
        foreach (var view in _views.Values)
            if (view is TerminalControl t) { try { t.Dispose(); } catch (ObjectDisposedException) { } }
        _tracker.Dispose();
    }
}
