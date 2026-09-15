using Avalonia.Controls;
using CoreRect = WinMux.Core.Layout.Rect;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Tests.Panes;

public sealed class PaneProviderRegistryTests
{
    [Fact]
    public void Register_and_resolve_are_keyed_by_pane_kind()
    {
        var terminal = new RecordingProvider(PaneKind.Terminal);
        var files = new RecordingProvider(PaneKind.FileBrowser);
        var registry = new PaneProviderRegistry([files, terminal]);

        Assert.Same(terminal, registry.GetRequired(PaneKind.Terminal));
        Assert.Same(files, registry.GetRequired(PaneKind.FileBrowser));
        Assert.Equal(2, registry.Count);
        Assert.Contains(terminal, registry.Providers);
        Assert.Contains(files, registry.Providers);
        Assert.True(registry.TryGet(PaneKind.FileBrowser, out var found));
        Assert.Same(files, found);
        Assert.False(registry.TryGet(PaneKind.ForeignApp, out _));
    }

    [Fact]
    public void Register_rejects_duplicate_kind_with_a_specific_error()
    {
        var registry = new PaneProviderRegistry();
        registry.Register(new RecordingProvider(PaneKind.Terminal));

        var error = Assert.Throws<DuplicatePaneProviderException>(
            () => registry.Register(new RecordingProvider(PaneKind.Terminal)));

        Assert.Equal(PaneKind.Terminal, error.Kind);
        Assert.Contains(PaneKind.Terminal.Value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_duplicate_kind()
    {
        var providers = new[]
        {
            new RecordingProvider(PaneKind.FileBrowser),
            new RecordingProvider(PaneKind.FileBrowser),
        };

        var error = Assert.Throws<DuplicatePaneProviderException>(() => new PaneProviderRegistry(providers));

        Assert.Equal(PaneKind.FileBrowser, error.Kind);
    }

    [Fact]
    public void Missing_provider_has_a_specific_kind_aware_error()
    {
        var registry = new PaneProviderRegistry();

        var error = Assert.Throws<PaneProviderNotFoundException>(
            () => registry.GetRequired(PaneKind.ForeignApp));

        Assert.Equal(PaneKind.ForeignApp, error.Kind);
        Assert.Contains(PaneKind.ForeignApp.Value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_routes_initial_context_and_cancellation_to_the_matching_provider()
    {
        var provider = new RecordingProvider(PaneKind.FileBrowser);
        var registry = new PaneProviderRegistry([provider]);
        var context = new PaneProviderContext(
            PaneId.New(), "files", Descriptor(PaneKind.FileBrowser));
        using var cancellation = new CancellationTokenSource();

        var runtime = await registry.CreateAsync(context, cancellation.Token);

        Assert.Same(provider.Runtime, runtime);
        Assert.Same(context, provider.CreationContext);
        Assert.Equal(cancellation.Token, provider.CreationToken);
        Assert.Null(provider.RestoreContext);
    }

    [Fact]
    public async Task Restore_routes_persisted_context_and_cancellation_to_the_matching_provider()
    {
        var provider = new RecordingProvider(PaneKind.Terminal);
        var registry = new PaneProviderRegistry([provider]);
        var context = new PaneProviderContext(
            PaneId.New(), "terminal", Descriptor(PaneKind.Terminal));
        using var cancellation = new CancellationTokenSource();

        var runtime = await registry.RestoreAsync(context, cancellation.Token);

        Assert.Same(provider.Runtime, runtime);
        Assert.Same(context, provider.RestoreContext);
        Assert.Equal(cancellation.Token, provider.RestoreToken);
        Assert.Null(provider.CreationContext);
    }

    [Fact]
    public async Task Runtime_contract_covers_control_layout_state_capture_and_lifecycle()
    {
        var runtime = new RecordingRuntime(PaneId.New(), PaneKind.FileBrowser);
        var bounds = new CoreRect(12, 24, 640, 480);

        var focused = runtime.Focus();
        runtime.Arrange(new PaneArrangement(bounds, IsVisible: false));
        var descriptor = runtime.CaptureRestoreDescriptor();
        var closed = await runtime.CloseAsync(PaneCloseReason.PaneRemoved);
        await runtime.DisposeAsync();

        Assert.IsType<Border>(runtime.View);
        Assert.True(focused);
        Assert.True(runtime.WasFocused);
        Assert.Equal(bounds, runtime.Bounds);
        Assert.False(runtime.IsVisible);
        Assert.Equal(PaneKind.FileBrowser, descriptor.Kind);
        Assert.True(closed.Succeeded);
        Assert.Equal(PaneCloseReason.PaneRemoved, runtime.CloseReason);
        Assert.Equal(1, runtime.CloseCount);
        Assert.Equal(1, runtime.DisposeCount);
    }

    [Fact]
    public async Task Routed_operations_report_a_missing_provider_before_starting_work()
    {
        var registry = new PaneProviderRegistry();
        var create = new PaneProviderContext(PaneId.New(), "files", Descriptor(PaneKind.FileBrowser));
        var restore = new PaneProviderContext(PaneId.New(), "app", Descriptor(PaneKind.ForeignApp));

        var createError = await Assert.ThrowsAsync<PaneProviderNotFoundException>(
            async () => await registry.CreateAsync(create));
        var restoreError = await Assert.ThrowsAsync<PaneProviderNotFoundException>(
            async () => await registry.RestoreAsync(restore));

        Assert.Equal(PaneKind.FileBrowser, createError.Kind);
        Assert.Equal(PaneKind.ForeignApp, restoreError.Kind);
    }

    private static RestoreDescriptor Descriptor(PaneKind kind) => new()
    {
        Kind = kind,
        Title = kind.ToString(),
    };

    private sealed class RecordingProvider(PaneKind kind) : IPaneProvider
    {
        public PaneKind Kind { get; } = kind;
        public RecordingRuntime Runtime { get; } = new(PaneId.New(), kind);
        public PaneProviderContext? CreationContext { get; private set; }
        public PaneProviderContext? RestoreContext { get; private set; }
        public CancellationToken CreationToken { get; private set; }
        public CancellationToken RestoreToken { get; private set; }

        public ValueTask<IPaneRuntime> CreateAsync(
            PaneProviderContext context,
            CancellationToken cancellationToken = default)
        {
            CreationContext = context;
            CreationToken = cancellationToken;
            return ValueTask.FromResult<IPaneRuntime>(Runtime);
        }

        public ValueTask<IPaneRuntime> RestoreAsync(
            PaneProviderContext context,
            CancellationToken cancellationToken = default)
        {
            RestoreContext = context;
            RestoreToken = cancellationToken;
            return ValueTask.FromResult<IPaneRuntime>(Runtime);
        }
    }

    private sealed class RecordingRuntime(PaneId paneId, PaneKind kind) : IPaneRuntime
    {
        public PaneId PaneId { get; } = paneId;
        public PaneKind Kind { get; } = kind;
        public Control View { get; } = new Border();
        public string? StatusMessage => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public bool WasFocused { get; private set; }
        public CoreRect Bounds { get; private set; }
        public bool IsVisible { get; private set; } = true;
        public PaneCloseReason? CloseReason { get; private set; }
        public int CloseCount { get; private set; }
        public int DisposeCount { get; private set; }

        public bool Focus()
        {
            WasFocused = true;
            return true;
        }

        public void Arrange(PaneArrangement arrangement)
        {
            Bounds = arrangement.Bounds;
            IsVisible = arrangement.IsVisible;
        }

        public void RefreshRestoreState() { }

        public RestoreDescriptor CaptureRestoreDescriptor() => Descriptor(Kind);

        public ValueTask<PaneCloseResult> CloseAsync(
            PaneCloseReason reason,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            CloseReason = reason;
            return ValueTask.FromResult(PaneCloseResult.Success());
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
