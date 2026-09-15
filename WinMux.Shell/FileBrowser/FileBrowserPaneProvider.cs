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

internal sealed class FileBrowserPaneProvider(Func<string> fallbackDirectory) : IPaneProvider
{
    private readonly Func<string> _fallbackDirectory = fallbackDirectory ?? throw new ArgumentNullException(nameof(fallbackDirectory));

    public PaneKind Kind => PaneKind.FileBrowser;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    private async ValueTask<IPaneRuntime> BuildAsync(PaneProviderContext context, CancellationToken token)
    {
        var pane = new Pane(context.PaneId, PaneKind.FileBrowser, context.Title, context.Descriptor);
        var runtime = new FileBrowserPaneRuntime(pane, _fallbackDirectory());
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
        Foreground = new SolidColorBrush(Color.FromRgb(0xa6, 0xad, 0xc8)),
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private FileBrowserModel? _model;
    private long _generation;
    private bool _rendering;
    private int _disposed;

    public FileBrowserPaneRuntime(Pane pane, string fallbackDirectory)
    {
        _pane = pane;
        _fallbackDirectory = fallbackDirectory;
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
        _status.Text = "Loading directory…";
        _model = await Task.Run(() => new FileBrowserModel(_pane, _fallbackDirectory), linked.Token);
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

    private void BuildView()
    {
        _root.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var toolbar = new Grid { Margin = new Thickness(6), ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Auto),
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(GridLength.Auto),
        }};
        var up = new Button { Content = "↑", Padding = new Thickness(10, 4) };
        var refresh = new Button { Content = "↻", Padding = new Thickness(10, 4) };
        var terminal = new Button { Content = "Terminal here", Padding = new Thickness(10, 4) };
        ToolTip.SetTip(up, "Parent directory");
        ToolTip.SetTip(refresh, "Refresh");
        Grid.SetColumn(up, 0);
        Grid.SetColumn(refresh, 1);
        Grid.SetColumn(_path, 2);
        Grid.SetColumn(terminal, 3);
        toolbar.Children.Add(up);
        toolbar.Children.Add(refresh);
        toolbar.Children.Add(_path);
        toolbar.Children.Add(terminal);

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_list, 1);
        Grid.SetRow(_status, 2);
        _root.Children.Add(toolbar);
        _root.Children.Add(_list);
        _root.Children.Add(_status);

        up.Click += (_, _) => _ = RunNavigationAsync((model, token) => model.NavigateParent(token));
        refresh.Click += (_, _) => _ = RunNavigationAsync((model, token) => { model.Refresh(token); return true; });
        terminal.Click += (_, _) => RequestTerminalHandoff();
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = RunNavigationAsync((model, token) => model.NavigateTo(_path.Text ?? string.Empty, token));
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
        };
    }

    private async Task RunNavigationAsync(Func<FileBrowserModel, CancellationToken, bool> operation)
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
                _status.Text = "Loading directory…";
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
            _status.Text = "Directory operation failed: " + ex.Message;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RenderModel()
    {
        if (_model is null) return;
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
    }
}
