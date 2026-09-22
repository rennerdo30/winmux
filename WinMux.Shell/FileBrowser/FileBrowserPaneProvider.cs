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

    public string DisplayName => "File browser";

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
        Func<bool, Task<IFileBrowserFileSystem?>>? connect = null;
        var target = Remote.RemoteFileBrowserTarget.From(context.Descriptor);

        if (target is not null)
        {
            var connector = new Remote.RemoteConnector(PlatformServices.Credentials);
            connect = askAgain => connector.ConnectAsync(target, _owner(), askAgain);
            filesystem = await connect(false);
        }

        // A remote pane falls back to the remote root, not to a Windows path. "E:\Development" means
        // nothing on an SFTP server, and the model would report it as unavailable forever.
        var fallback = target is null ? _fallbackDirectory() : Remote.RemotePath.Root;

        var runtime = new FileBrowserPaneRuntime(pane, fallback, filesystem, target, connect);
        await runtime.InitializeAsync(token);
        return runtime;
    }
}

internal sealed partial class FileBrowserPaneRuntime : IPaneRuntime, ITerminalHandoffRuntime
{
    private readonly Pane _pane;
    private readonly string _fallbackDirectory;
    private readonly Grid _root = new();
    private readonly TextBox _path = new() { PlaceholderText = "Directory" };
    // Focusable so that an empty directory, which has no rows to take focus, can still hold it.
    private readonly ListBox _list = new() { Focusable = true };
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

    /// <summary>The filesystem, replaced when a remote pane reconnects. Null means the local disk.</summary>
    private IFileBrowserFileSystem? _fileSystem;

    /// <summary>The server this pane is a view of, or null for a local pane.</summary>
    private readonly Remote.RemoteFileBrowserTarget? _target;

    /// <summary>Connect again — asking for the password when the argument is true.</summary>
    private readonly Func<bool, Task<IFileBrowserFileSystem?>>? _connect;

    public FileBrowserPaneRuntime(
        Pane pane,
        string fallbackDirectory,
        IFileBrowserFileSystem? fileSystem = null,
        Remote.RemoteFileBrowserTarget? target = null,
        Func<bool, Task<IFileBrowserFileSystem?>>? connect = null)
    {
        _pane = pane;
        _fallbackDirectory = fallbackDirectory;
        _fileSystem = fileSystem;
        _target = target;
        _connect = connect;
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
        UpdateLocation();
        _status.Text = _target is null ? "Loading directory…" : $"Connecting to {_target.Display}…";

        if (_target is not null)
        {
            // Connect before building the model, and stop with one clear sentence if it fails. The
            // model would otherwise try the saved directory, then the fallback, then the selection,
            // and report all three — one refusal said three times, which reads as three faults.
            // Falling back to the local disk would be worse: a pane labelled with a server showing C:.
            var problem = _fileSystem is null
                ? "The sign-in was cancelled."
                : await Task.Run(() => Probe(_fileSystem), linked.Token);

            if (problem is not null)
            {
                ShowConnectionProblem(problem);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        HideConnectionProblem();
        _model = await Task.Run(
            () => new FileBrowserModel(_pane, _fallbackDirectory, _fileSystem),
            linked.Token);
        RenderModel();
        UpdateLocation();
    }

    /// <summary>One round trip to the server, to find out whether there is a connection at all.</summary>
    private static string? Probe(IFileBrowserFileSystem fileSystem)
    {
        try
        {
            fileSystem.DirectoryExists(Remote.RemotePath.Root);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Try again after a refused or failed connection — asking for the password, because the one that
    /// was tried is the likeliest reason it failed.
    /// </summary>
    private async Task ReconnectAsync()
    {
        if (_connect is null || _target is null || Volatile.Read(ref _disposed) != 0) return;

        _connectionText.Text = $"Connecting to {_target.Display}…";
        var fresh = await _connect(true);
        if (fresh is null)
        {
            ShowConnectionProblem("The sign-in was cancelled.");
            return;
        }

        var old = _fileSystem;
        _fileSystem = fresh;
        if (old is not null)
        {
            FileBrowserClipboard.Shared.Forget(old);
            if (old is IDisposable disposable) _ = Task.Run(disposable.Dispose);
        }

        _model = null;
        await InitializeAsync(CancellationToken.None);
    }

    public bool Focus() => FocusList();

    /// <summary>
    /// Put keyboard focus in the list: the selected row, else the first, else the list itself.
    ///
    /// <c>_list.Focus()</c> alone does nothing — Fluent's ListBox is not focusable, only its rows
    /// are — and it fails silently. Every caller of it was a no-op whenever no row already had focus,
    /// including the shell moving focus to this pane from the keyboard.
    /// </summary>
    private bool FocusList()
    {
        if (_list.SelectedItem is Control selected && selected.Focus()) return true;
        if (_list.Items.Count > 0 && _list.Items[0] is Control first && first.Focus()) return true;
        return _list.Focus();
    }
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
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // toolbar
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // connection banner
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // column headers
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Star)); // the listing
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // status

        BuildChrome();

        var toolbar = new Grid { Margin = new Thickness(6), ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Auto),
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
        Grid.SetColumn(_location, 2);
        Grid.SetColumn(_path, 3);
        Grid.SetColumn(newFolder, 4);
        Grid.SetColumn(terminal, 5);
        toolbar.Children.Add(up);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(_location);
        toolbar.Children.Add(_path);
        toolbar.Children.Add(newFolder);
        toolbar.Children.Add(terminal);

        _list.ContextMenu = BuildContextMenu();

        // A click below the last row lands on the list's empty area, which does not take focus —
        // so the pane did not become the focused pane, and the Ctrl+V that followed went to
        // whichever pane had focus before. Anything clicked in the pane that leaves focus outside
        // it gives it to the list.
        _root.AddHandler(
            InputElement.PointerPressedEvent,
            (_, _) =>
            {
                if (!_root.IsKeyboardFocusWithin) FocusList();
            },
            Avalonia.Interactivity.RoutingStrategies.Bubble,
            handledEventsToo: true);

        FileBrowserChanges.DirectoryChanged += OnDirectoryChanged;
        InitializeDragDrop();

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_banner, 1);
        Grid.SetRow(_header, 2);
        Grid.SetRow(_list, 3);
        Grid.SetRow(_status, 4);
        _root.Children.Add(toolbar);
        _root.Children.Add(_banner);
        _root.Children.Add(_header);
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
            _model.SelectMany((_list.SelectedItems ?? Array.Empty<object>())
                .OfType<ListBoxItem>()
                .Select(row => row.Tag)
                .OfType<FileBrowserNavigationItem>()
                .Select(item => item.Path));
            RenderStatus();
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
        _list.DoubleTapped += (_, e) =>
        {
            // The row under the pointer, not the selection: with several rows selected the first of
            // them is not necessarily the one that was double-clicked.
            if (RowAt(e.Source)?.Tag is not FileBrowserNavigationItem { IsDirectory: true } item) return;
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
                    PasteHere();
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
        var paste = Item("Paste", Key.V, KeyModifiers.Control, PasteHere);
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
            rename.IsEnabled = writable && model?.SelectedItems.Count == 1;
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

    /// <summary>
    /// Paste, reporting progress in the status line. A copy from a server can run for minutes, and
    /// "Pasting…" alone for that long is indistinguishable from a hang.
    /// </summary>
    private void PasteHere()
    {
        // Created here, on the UI thread, so that its reports are posted back to it.
        var progress = new Progress<string>(message =>
        {
            if (Volatile.Read(ref _disposed) == 0) _status.Text = message;
        });

        _ = RunNavigationAsync((model, token) => model.Paste(token, progress), "Pasting…");
    }

    /// <summary>
    /// Another pane changed the directory this one is showing. Raised on whichever thread the
    /// change happened on, so it is only a request to refresh on the UI thread.
    /// </summary>
    private void OnDirectoryChanged(object? sender, FileBrowserDirectoryChanged change)
    {
        var model = _model;
        if (model is null || ReferenceEquals(sender, model) || !model.IsShowing(change.FileSystem, change.Directory)) return;

        Dispatcher.UIThread.Post(() =>
        {
            // Not while this pane is busy: starting a refresh cancels the operation in flight, and
            // that operation is a paste that re-reads the directory when it finishes anyway.
            if (Volatile.Read(ref _disposed) != 0 || _operationLock.CurrentCount == 0) return;
            _ = RunNavigationAsync((m, token) => { m.Refresh(token, reportLostSelection: false); return true; });
        });
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
        if (_model?.SelectedItems is not { Count: > 0 } items || Owner is not { } owner) return;

        // A Recycle Bin delete is undoable, so it does not interrupt — CLAUDE.md's "do not interrupt
        // for success" applies to anything the user can reverse. A permanent one cannot be undone
        // and is the only destructive act this pane has, so it always asks.
        if (permanent)
        {
            var item = items[0];
            var what = items.Count > 1 ? $"these {items.Count} items"
                : item.IsDirectory ? $"the folder '{item.Name}'"
                : $"the file '{item.Name}'";
            var confirmed = await NoticeWindow.ConfirmAsync(
                owner,
                "Delete permanently",
                $"Delete {what} permanently?\n\n" +
                (items.Any(i => i.IsDirectory) ? "Everything inside a folder goes too. " : "") +
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
            var selectedPaths = _model.SelectedItems.Select(item => item.Path).ToHashSet(_model.FileSystem.PathComparer);
            var selected = new List<ListBoxItem>();
            var icons = new List<RowView>(_model.Entries.Count);

            // Keep the header; the rows are about to be rebuilt.
            _rowGrids.RemoveRange(1, _rowGrids.Count - 1);
            foreach (var item in _model.Entries)
            {
                var row = new ListBoxItem
                {
                    Content = RowContent(item, out var view),
                    Tag = item,
                    Padding = new Thickness(8, 4),
                };
                icons.Add(view);
                if (selectedPaths.Contains(item.Path)) selected.Add(row);
                _list.Items.Add(row);
            }

            _list.SelectedItems?.Clear();
            foreach (var row in selected) _list.SelectedItems?.Add(row);
            LoadIcons(icons);
            RenderHeader();
            UpdateLocation();
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
                    FocusList();
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
        FileBrowserChanges.DirectoryChanged -= OnDirectoryChanged;
        Interlocked.Increment(ref _iconGeneration);
        _lifetime.Cancel();
        _operation?.Cancel();
        _operation?.Dispose();
        _lifetime.Dispose();

        // A remote filesystem holds a live socket. Closing the pane must close it, or a session of
        // opening and closing remote panes leaks a connection each time — and servers count those.
        //
        // Off the UI thread: disposing takes the connection's lock, and that lock is held for the
        // whole of any call in flight — a listing from a server that has stopped answering, or a
        // transfer another pane is running from this one. Waiting for either here would freeze the
        // shell for as long as it takes (CLAUDE.md section 1, priority 2).
        if (_fileSystem is not null) FileBrowserClipboard.Shared.Forget(_fileSystem);
        if (_fileSystem is IDisposable disposable) _ = Task.Run(disposable.Dispose);
    }
}
