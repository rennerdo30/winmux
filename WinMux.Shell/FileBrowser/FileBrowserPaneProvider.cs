using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.FileBrowser;

internal interface ITerminalHandoffRuntime
{
    string? TerminalHandoffDirectory { get; }
    event EventHandler? TerminalHandoffRequested;
}

internal sealed class FileBrowserPaneProvider(
    Func<string> fallbackDirectory,
    Func<Window?>? owner = null) : IPaneProvider
{
    private readonly Func<string> _fallbackDirectory = fallbackDirectory ?? throw new ArgumentNullException(nameof(fallbackDirectory));

    /// <summary>The window a credential prompt is shown over. Null means a remote pane cannot ask.</summary>
    private readonly Func<Window?> _owner = owner ?? (() => null);

    public PaneKind Kind => PaneKind.FileBrowser;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    private async ValueTask<IPaneRuntime> BuildAsync(PaneProviderContext context, CancellationToken token)
    {
        var pane = new Pane(context.PaneId, PaneKind.FileBrowser, context.Title, context.Descriptor);

        // A descriptor carrying a remote target makes this a remote browser. Everything past this
        // point — the model, the clipboard, every operation — is identical either way, which is the
        // entire reason IFileBrowserFileSystem is an interface.
        IFileBrowserFileSystem? filesystem = null;
        string? connectionNote = null;

        if (Remote.RemoteFileBrowserTarget.From(context.Descriptor) is { } target)
        {
            var connector = new Remote.RemoteConnector(PlatformServices.Credentials);
            filesystem = await connector.ConnectAsync(target, _owner());
            connectionNote = filesystem is null
                ? $"Not connected to {target.Display}. Close and reopen the pane to try again."
                : null;
        }

        // A remote pane falls back to the remote root, not to a Windows path. "E:\Development" means
        // nothing on an SFTP server, and the model would report it as unavailable forever.
        var fallback = filesystem is null ? _fallbackDirectory() : Remote.RemotePath.Root;

        var runtime = new FileBrowserPaneRuntime(pane, fallback, filesystem, connectionNote);
        await runtime.InitializeAsync(token);
        return runtime;
    }
}

internal sealed class FileBrowserPaneRuntime : IPaneRuntime, ITerminalHandoffRuntime
{
    private readonly Pane _pane;
    private readonly string _fallbackDirectory;
    private readonly Grid _root = new();
    private readonly TextBox _path = new() { PlaceholderText = "Directory" };
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(8, 4),
        Foreground = Chrome.Palette.MutedTextBrush,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private FileBrowserModel? _model;
    private long _generation;
    private bool _rendering;
    private int _disposed;

    private readonly IFileBrowserFileSystem? _fileSystem;
    private readonly string? _connectionNote;

    public FileBrowserPaneRuntime(
        Pane pane,
        string fallbackDirectory,
        IFileBrowserFileSystem? fileSystem = null,
        string? connectionNote = null)
    {
        _pane = pane;
        _fallbackDirectory = fallbackDirectory;
        _fileSystem = fileSystem;
        _connectionNote = connectionNote;
        BuildView();
    }

    public PaneId PaneId => _pane.Id;
    public PaneKind Kind => PaneKind.FileBrowser;
    public Control View => _root;
    public string? StatusMessage => _model?.StatusMessage;
    public string? TerminalHandoffDirectory => _model?.TerminalHandoffDirectory;
    public event EventHandler? StateChanged;
    public event EventHandler? TerminalHandoffRequested;

    public async Task InitializeAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        _status.Text = _connectionNote ?? "Loading directory…";

        if (_connectionNote is not null)
        {
            // The connection was refused or cancelled. Show the reason and stop: falling back to the
            // local filesystem would put the user somewhere they did not ask to be, under a tab
            // named after a server.
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _model = await Task.Run(
            () => new FileBrowserModel(_pane, _fallbackDirectory, _fileSystem),
            linked.Token);
        RenderModel();
    }

    public bool Focus() => _list.Focus();
    public void Arrange(PaneArrangement arrangement) { }
    public void RefreshRestoreState() { }
    public RestoreDescriptor CaptureRestoreDescriptor() => _pane.Restore;

    public ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken token = default)
    {
        if (reason == PaneCloseReason.PaneRemoved) DisposeCore();
        return ValueTask.FromResult(PaneCloseResult.Success("file browser closed"));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private static Button ToolbarButton(string content)
    {
        var button = new Button { Content = content };
        button.Classes.Add(Chrome.Theme.ToolbarButton);
        return button;
    }

    private void BuildView()
    {
        // The browser is shell UI, so it takes the shell's colours rather than its own. It used to
        // carry a hardcoded palette that drifted from the chrome around it.
        _root.Background = Chrome.Palette.SurfaceBrush;
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var toolbar = new Grid { Margin = new Thickness(6), ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Auto),
        }};
        var up = ToolbarButton("↑");
        var refresh = ToolbarButton("↻");
        var newFolder = ToolbarButton("New folder");
        var terminal = ToolbarButton("Terminal here");
        _path.Margin = new Thickness(6, 0);
        _path.CornerRadius = Chrome.Palette.ControlRadius;
        _path.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        ToolTip.SetTip(up, "Parent directory");
        ToolTip.SetTip(refresh, "Refresh");
        ToolTip.SetTip(newFolder, "New folder (Ctrl+Shift+N)");
        Grid.SetColumn(up, 0);
        Grid.SetColumn(refresh, 1);
        Grid.SetColumn(_path, 2);
        Grid.SetColumn(newFolder, 3);
        Grid.SetColumn(terminal, 4);
        toolbar.Children.Add(up);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(_path);
        toolbar.Children.Add(newFolder);
        toolbar.Children.Add(terminal);

        _list.ContextMenu = BuildContextMenu();

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        _root.Children.Add(toolbar);
        _root.Children.Add(_list);
        _root.Children.Add(_status);

        up.Click += (_, _) => _ = RunNavigationAsync((model, token) => model.NavigateParent(token));
        refresh.Click += (_, _) => _ = RunNavigationAsync((model, token) => { model.Refresh(token); return true; });
        newFolder.Click += (_, _) => _ = NewFolderAsync();
        terminal.Click += (_, _) => RequestTerminalHandoff();
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            // Read the box here, on the UI thread. The lambda below runs on a background thread via
            // Task.Run, and touching an Avalonia control from there throws "the calling thread
            // cannot access this object" — which is what typing a path and pressing Enter did from
            // Phase 4 until this was noticed on screen.
            var target = _path.Text ?? string.Empty;
            _ = RunNavigationAsync((model, token) => model.NavigateTo(target, token));
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (_rendering || _model is null) return;
            var item = (_list.SelectedItem as ListBoxItem)?.Tag as FileBrowserNavigationItem;
            _model.Select(item?.Path);
            RenderStatus();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _list.DoubleTapped += (_, e) =>
        {
            if ((_list.SelectedItem as ListBoxItem)?.Tag is not FileBrowserNavigationItem { IsDirectory: true } item) return;
            e.Handled = true;
            _ = RunNavigationAsync((model, token) => model.NavigateTo(item.Path, token));
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                RequestTerminalHandoff();
            }
            else if (e.Key == Key.Enter && (_list.SelectedItem as ListBoxItem)?.Tag is FileBrowserNavigationItem { IsDirectory: true } item)
            {
                e.Handled = true;
                _ = RunNavigationAsync((model, token) => model.NavigateTo(item.Path, token));
            }
            else if (e.Key == Key.Back)
            {
                e.Handled = true;
                _ = RunNavigationAsync((model, token) => model.NavigateParent(token));
            }
            else if (e.Key == Key.F5)
            {
                e.Handled = true;
                _ = RunNavigationAsync((model, token) => { model.Refresh(token); return true; });
            }
            else if (e.Key == Key.F2)
            {
                e.Handled = true;
                _ = RenameAsync();
            }
            else if (e.Key == Key.Delete)
            {
                // Shift is the long-standing Windows gesture for "skip the Recycle Bin", and it is
                // the one place this pane can destroy something, so it asks first.
                e.Handled = true;
                _ = DeleteAsync(permanent: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            else if (e.Key == Key.N && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                _ = NewFolderAsync();
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.C or Key.X or Key.V)
            {
                e.Handled = true;
                if (e.Key == Key.V)
                {
                    _ = RunNavigationAsync((model, token) => model.Paste(token), "Pasting…");
                }
                else
                {
                    HoldSelected(isMove: e.Key == Key.X);
                }
            }
        };
    }

    /// <summary>
    /// Every modifying operation, as a menu. CLAUDE.md section 5a: a capability with no interface
    /// is a note to the author, and these are not discoverable from a key alone.
    /// </summary>
    private ContextMenu BuildContextMenu()
    {
        var newFolder = Item("New folder", Key.N, KeyModifiers.Control | KeyModifiers.Shift, () => _ = NewFolderAsync());
        var cut = Item("Cut", Key.X, KeyModifiers.Control, () => HoldSelected(isMove: true));
        var copy = Item("Copy", Key.C, KeyModifiers.Control, () => HoldSelected(isMove: false));
        var paste = Item("Paste", Key.V, KeyModifiers.Control, () => _ = RunNavigationAsync((model, token) => model.Paste(token), "Pasting…"));
        var rename = Item("Rename…", Key.F2, KeyModifiers.None, () => _ = RenameAsync());
        var delete = Item("Delete", Key.Delete, KeyModifiers.None, () => _ = DeleteAsync(permanent: false));
        var destroy = Item("Delete permanently…", Key.Delete, KeyModifiers.Shift, () => _ = DeleteAsync(permanent: true));
        var terminal = Item("Open terminal here", Key.Enter, KeyModifiers.Control, RequestTerminalHandoff);

        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                newFolder, new Separator(),
                cut, copy, paste, new Separator(),
                rename, delete, destroy, new Separator(),
                terminal,
            },
        };

        // State is decided when the menu opens, not when it was built: what is selected, what is on
        // the clipboard and whether the location is writable all change underneath it.
        menu.Opening += (_, _) =>
        {
            var model = _model;
            var writable = model?.CanModify == true;
            var selected = model?.SelectedItem is not null;

            newFolder.IsEnabled = writable;
            cut.IsEnabled = writable && selected;
            copy.IsEnabled = selected;
            paste.IsEnabled = model?.CanPaste == true;
            rename.IsEnabled = writable && selected;
            delete.IsEnabled = writable && selected;
            destroy.IsEnabled = writable && selected;
            terminal.IsEnabled = model is not null;

            // Saying "no Recycle Bin here" on the item is better than letting the user press it and
            // read a refusal in the status line.
            delete.Header = model?.CanRecoverDeletes == false ? "Delete (no Recycle Bin here)" : "Delete";
        };

        return menu;
    }

    /// <summary>
    /// A menu item with its shortcut shown beside it.
    ///
    /// The gesture is built from <see cref="Key"/> and <see cref="KeyModifiers"/> rather than parsed
    /// from a string, because <c>KeyGesture.Parse</c> validates at run time and throws: "Del" is not
    /// a name it knows, and the resulting exception escaped through the provider and stopped the
    /// pane from being created at all. Built this way the compiler checks it and it cannot throw.
    /// </summary>
    private static MenuItem Item(string header, Key key, KeyModifiers modifiers, Action invoke)
    {
        var item = new MenuItem { Header = header, InputGesture = new KeyGesture(key, modifiers) };
        item.Click += (_, _) => invoke();
        return item;
    }

    /// <summary>
    /// Cut and copy, on the UI thread and without reloading the directory.
    ///
    /// They move no bytes — they put a path on the clipboard — so there is nothing to wait for and
    /// nothing to re-read. Routing them through the background runner rebuilt every row, and
    /// rebuilding the rows takes keyboard focus off the list, so the Ctrl+V that almost always
    /// follows a Ctrl+C went to nobody. Copy-then-paste from the keyboard silently did nothing.
    /// </summary>
    private void HoldSelected(bool isMove)
    {
        if (_model is null) return;
        _model.HoldSelected(isMove);
        RenderStatus();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Window? Owner => TopLevel.GetTopLevel(_root) as Window;

    private async Task NewFolderAsync()
    {
        if (_model is null || Owner is not { } owner) return;

        var prompt = new PromptWindow(
            "New folder",
            $"A new folder in {_model.CurrentDirectory}.",
            "New folder");
        await prompt.ShowDialog(owner);

        if (prompt.Result is not { } name) return;
        await RunNavigationAsync((model, token) => model.CreateDirectory(name, token), "Creating folder…");
    }

    private async Task RenameAsync()
    {
        if (_model?.SelectedItem is not { } item || Owner is not { } owner) return;

        var prompt = new PromptWindow(
            "Rename",
            $"A new name for '{item.Name}'.",
            item.Name);
        await prompt.ShowDialog(owner);

        if (prompt.Result is not { } name) return;
        await RunNavigationAsync((model, token) => model.RenameSelected(name, token), "Renaming…");
    }

    private async Task DeleteAsync(bool permanent)
    {
        if (_model?.SelectedItem is not { } item || Owner is not { } owner) return;

        // A Recycle Bin delete is undoable, so it does not interrupt — CLAUDE.md's "do not interrupt
        // for success" applies to anything the user can reverse. A permanent one cannot be undone
        // and is the only destructive act this pane has, so it always asks.
        if (permanent)
        {
            var what = item.IsDirectory ? "folder" : "file";
            var confirmed = await NoticeWindow.ConfirmAsync(
                owner,
                "Delete permanently",
                $"Delete the {what} '{item.Name}' permanently?\n\n" +
                (item.IsDirectory ? "Everything inside it goes too. " : "") +
                "This cannot be undone and it does not go to the Recycle Bin.",
                "Delete permanently");

            if (!confirmed) return;
        }

        await RunNavigationAsync((model, token) => model.DeleteSelected(permanent, token), "Deleting…");
    }

    private async Task RunNavigationAsync(
        Func<FileBrowserModel, CancellationToken, bool> operation,
        string busyMessage = "Loading directory…")
    {
        if (_model is null || Volatile.Read(ref _disposed) != 0) return;
        var generation = Interlocked.Increment(ref _generation);
        var previous = Interlocked.Exchange(ref _operation,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        previous?.Cancel();
        previous?.Dispose();
        var current = _operation!;

        try
        {
            await _operationLock.WaitAsync(current.Token);
            try
            {
                _status.Text = busyMessage;
                await Task.Run(() => operation(_model, current.Token), current.Token);
            }
            finally
            {
                _operationLock.Release();
            }

            if (generation != Volatile.Read(ref _generation) || current.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(RenderModel);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _status.Text = "File operation failed: " + ex.Message;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RenderModel()
    {
        if (_model is null) return;

        // Rebuilding the rows destroys the focused one, and focus goes with it. Anyone who deletes
        // a file and then presses Delete again expects the second key to reach the list, so put it
        // back where it was — but only if it was here, or a refresh would steal focus from whatever
        // the user moved to in the meantime.
        var hadFocus = _list.IsKeyboardFocusWithin;

        _rendering = true;
        try
        {
            _path.Text = _model.CurrentDirectory;
            _list.Items.Clear();
            ListBoxItem? selected = null;
            foreach (var item in _model.Entries)
            {
                var row = new ListBoxItem
                {
                    Content = (item.IsDirectory ? "📁  " : "     ") + item.Name,
                    Tag = item,
                    Padding = new Thickness(8, 5),
                };
                if (string.Equals(item.Path, _model.SelectedPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    selected = row;
                _list.Items.Add(row);
            }
            _list.SelectedItem = selected;
            RenderStatus();
        }
        finally
        {
            _rendering = false;
        }

        // Posted rather than called: the rows have just been replaced and the new ones are not laid
        // out yet, so focusing here lands on a control that does not exist on screen and silently
        // does nothing. At Input priority this runs after layout, which is when there is something
        // to focus.
        if (hadFocus)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (Volatile.Read(ref _disposed) != 0) return;
                    if (_list.SelectedItem is Control row) row.Focus();
                    else _list.Focus();
                },
                DispatcherPriority.Input);
        }
    }

    private void RenderStatus()
    {
        if (_model is null) return;
        _status.Text = _model.StatusMessage ??
            (_model.Entries.Count == 0 ? "This directory is empty." : $"{_model.Entries.Count} item(s)");
    }

    private void RequestTerminalHandoff()
    {
        if (_model is null || !Directory.Exists(_model.TerminalHandoffDirectory))
        {
            _status.Text = "Cannot open a terminal because the selected directory is unavailable.";
            return;
        }
        TerminalHandoffRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _operation?.Cancel();
        _operation?.Dispose();
        _lifetime.Dispose();

        // A remote filesystem holds a live socket. Closing the pane must close it, or a session of
        // opening and closing remote panes leaks a connection each time — and servers count those.
        (_fileSystem as IDisposable)?.Dispose();
    }
}
