using Avalonia.Threading;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Panes;

internal interface ITerminalInputRuntime
{
    ValueTask SendPrefixAsync(CancellationToken cancellationToken = default);
}

internal sealed class TerminalPaneProvider : IPaneProvider
{
    public PaneKind Kind => PaneKind.Terminal;

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        BuildAsync(context, token);

    private static async ValueTask<IPaneRuntime> BuildAsync(PaneProviderContext context, CancellationToken token)
    {
        var pane = new Pane(context.PaneId, KindValue(), context.Title, context.Descriptor);
        var normalized = ExecutablePathResolver.ResolveTerminalDescriptor(pane.Restore);
        if (normalized.Succeeded) pane.Restore = normalized.Descriptor;

        var runtime = new TerminalPaneRuntime(pane, normalized.Error);
        try
        {
            await runtime.StartAsync(token);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    private static PaneKind KindValue() => PaneKind.Terminal;
}

internal sealed class TerminalPaneRuntime : IPaneRuntime, ITerminalInputRuntime
{
    private readonly Pane _pane;
    private readonly TerminalPaneControl _terminal;
    private readonly bool _isWsl;
    private int _disposed;

    public TerminalPaneRuntime(Pane pane, string? initialWarning)
    {
        _pane = pane;
        var restore = pane.Restore;
        var program = string.IsNullOrWhiteSpace(restore.Program)
            ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"
            : restore.Program;
        _isWsl = IsWsl(program);
        var arguments = restore.Args.ToList();
        string cwd;

        if (_isWsl)
        {
            cwd = Environment.CurrentDirectory;
            if (restore.Cwd.IsKnown && restore.Cwd.Path.StartsWith("/", StringComparison.Ordinal))
            {
                arguments.Insert(0, restore.Cwd.Path);
                arguments.Insert(0, "--cd");
            }
        }
        else if (restore.Cwd.IsKnown && Directory.Exists(restore.Cwd.Path))
        {
            cwd = restore.Cwd.Path;
        }
        else
        {
            cwd = Environment.CurrentDirectory;
            StatusMessage = restore.Cwd.IsKnown
                ? $"saved cwd is unavailable: {restore.Cwd.Path}; started in {cwd}"
                : initialWarning;
            if (!restore.Cwd.IsKnown)
            {
                _pane.Restore = restore with
                {
                    Cwd = new WorkingDirectory(cwd, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow),
                };
            }
        }

        _terminal = new TerminalPaneControl(program, arguments, cwd, restore.EnvOverrides);
        _terminal.TitleChanged += title => Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                _pane.Title = title;
                _pane.Restore = _pane.Restore with { Title = title };
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
        _terminal.WorkingDirectoryChanged += path => Dispatcher.UIThread.Post(() =>
        {
            _pane.Restore = _pane.Restore with
            {
                Cwd = new WorkingDirectory(path, CwdSource.ShellReported, DateTimeOffset.UtcNow),
            };
            StatusMessage = $"captured cwd from the shell: {path}";
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
        _terminal.Exited += exitCode => Dispatcher.UIThread.Post(() =>
        {
            StatusMessage = $"terminal exited with code {exitCode}";
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public PaneId PaneId => _pane.Id;
    public PaneKind Kind => PaneKind.Terminal;
    public Avalonia.Controls.Control View => _terminal;
    public string? StatusMessage { get; private set; }
    public event EventHandler? StateChanged;

    public Task StartAsync(CancellationToken token) => _terminal.StartAsync(token);
    public bool Focus() => _terminal.Focus();
    public void Arrange(PaneArrangement arrangement) { }

    /// <summary>
    /// Strategy 2 of the layered cwd capture (CLAUDE.md section 4). One for the whole shell, so
    /// every pane shares the cached process list rather than each paying for its own.
    /// </summary>
    private static readonly Cwd.ProcessWorkingDirectoryResolver WorkingDirectories =
        new(new Cwd.CachedProcessInspector(PlatformServices.Processes));

    public void RefreshRestoreState()
    {
        if (_terminal.ProcessId is not int processId) return;
        var result = WorkingDirectories.Resolve(processId, disablePebForWsl: _isWsl);
        if (!result.Succeeded) return;
        var capture = new WorkingDirectory(result.Path!, result.Provenance, DateTimeOffset.UtcNow);
        _pane.Restore = _pane.Restore with { Cwd = WorkingDirectory.Better(_pane.Restore.Cwd, capture) };
    }

    public RestoreDescriptor CaptureRestoreDescriptor()
    {
        RefreshRestoreState();
        return _pane.Restore;
    }

    public ValueTask SendPrefixAsync(CancellationToken token = default) =>
        _terminal.SendText("\u0002", token);

    public ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken token = default)
    {
        if (reason == PaneCloseReason.PaneRemoved) DisposeCore();
        return ValueTask.FromResult(PaneCloseResult.Success("terminal closed"));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _terminal.Dispose();
    }

    private static bool IsWsl(string? program) =>
        string.Equals(Path.GetFileNameWithoutExtension(program), "wsl", StringComparison.OrdinalIgnoreCase);
}
