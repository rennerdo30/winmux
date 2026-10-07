using System.Reflection;
using Avalonia.Controls;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Platform;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

/// <summary>Exercises launch dispatch through the actual window layout/provider path without
/// showing MainWindow, starting a PTY, running the command server, or invoking its Opened hooks.</summary>
public sealed class LaunchDirectoryIntegrationTests
{
    [Fact]
    public Task WindowLaunchWaitsForStartupAndAddsCmdAtExplicitDirectory() => Headless.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.NewWindow(out var tree, out var provider, out var original);
        var startup = Field<TaskCompletionSource>(window, "_startupReady");
        var launch = window.OpenLaunchDirectoryAsync(fixture.Incoming);
        Assert.False(launch.IsCompleted);
        Assert.Empty(provider.Created);
        Assert.Single(tree.Panes);
        startup.SetResult();
        await launch;
        Assert.Equal(2, tree.Panes.Count());
        Assert.Same(original, tree.GetPane(original.Id));
        Assert.Equal(fixture.Focused, original.Restore.Cwd.Path);
        var first = Assert.Single(provider.Created);
        AssertIncomingCmd(first, fixture.Incoming);
        Assert.Equal(first.PaneId, tree.Focused);

        await window.OpenLaunchDirectoryAsync(fixture.SecondIncoming);
        Assert.Equal(3, tree.Panes.Count());
        Assert.Equal(2, provider.Created.Count);
        AssertIncomingCmd(provider.Created[1], fixture.SecondIncoming);
        Assert.NotEqual(first.PaneId, provider.Created[1].PaneId);
        Assert.Equal(provider.Created[1].PaneId, tree.Focused);
        Assert.Same(original, tree.GetPane(original.Id));
    });

    [Fact]
    public Task SessionDispatchRoutesEachLaunchToActiveWindowAndKeepsExistingPanes() => Headless.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var first = fixture.NewWindow(out var firstTree, out var firstProvider, out _);
        var second = fixture.NewWindow(out var secondTree, out var secondProvider, out var original);
        Field<TaskCompletionSource>(first, "_startupReady").SetResult();
        Field<TaskCompletionSource>(second, "_startupReady").SetResult();
        fixture.Session.Activate(second);
        await fixture.Session.DispatchLaunchAsync(fixture.Incoming);
        await fixture.Session.DispatchLaunchAsync(fixture.SecondIncoming);
        Assert.Single(firstTree.Panes);
        Assert.Empty(firstProvider.Created);
        Assert.Equal(3, secondTree.Panes.Count());
        Assert.Same(original, secondTree.GetPane(original.Id));
        Assert.Equal(2, secondProvider.Created.Count);
        AssertIncomingCmd(secondProvider.Created[0], fixture.Incoming);
        AssertIncomingCmd(secondProvider.Created[1], fixture.SecondIncoming);
        Assert.Equal(secondProvider.Created[1].PaneId, secondTree.Focused);
    });

    [Fact]
    public Task MissingIncomingDirectoryDoesNotCreatePane() => Headless.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var window = fixture.NewWindow(out var tree, out var provider, out _);
        Field<TaskCompletionSource>(window, "_startupReady").SetResult();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            window.OpenLaunchDirectoryAsync(Path.Combine(fixture.Incoming, "missing")));
        Assert.Single(tree.Panes);
        Assert.Empty(provider.Created);
    });

    private static void AssertIncomingCmd(PaneProviderContext context, string path)
    {
        Assert.Equal(PaneKind.Terminal, context.Descriptor.Kind);
        Assert.Equal("cmd.exe", Path.GetFileName(context.Descriptor.Program), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFullPath(path), context.Descriptor.Cwd.Path);
        Assert.Equal(CwdSource.LaunchDirectory, context.Descriptor.Cwd.Source);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "winmux-launch-tests-" + Guid.NewGuid());
        private readonly List<MainWindow> _windows = [];
        private readonly List<FakeProvider> _providers = [];
        public string Focused { get; }
        public string Incoming { get; }
        public string SecondIncoming { get; }
        public SessionController Session { get; }

        public Fixture()
        {
            Focused = Directory.CreateDirectory(Path.Combine(_root, "existing-focused-cwd")).FullName;
            Incoming = Directory.CreateDirectory(Path.Combine(_root, "Explorer incoming folder")).FullName;
            SecondIncoming = Directory.CreateDirectory(Path.Combine(_root, "another incoming folder")).FullName;
            Session = new SessionController(Path.Combine(_root, "session.toml"));
        }

        public MainWindow NewWindow(out LayoutTree tree, out FakeProvider provider, out Pane original)
        {
            original = Pane.Terminal("existing PowerShell", "pwsh.exe",
                new WorkingDirectory(Focused, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow));
            tree = new LayoutTree(original);
            var window = new MainWindow(tree, Session, KeymapConfiguration.TmuxDefaults(), "launch test");
            // Deliberately do not Show(): MainWindow.Opened launches real providers and foreground
            // monitoring. Replace only its provider registry; production dispatch stays intact.
            provider = new FakeProvider();
            typeof(MainWindow).GetField("_providers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(window, new PaneProviderRegistry([provider]));
            _windows.Add(window);
            _providers.Add(provider);
            return window;
        }

        public void Dispose()
        {
            Session.Dispose();
            foreach (var window in _windows)
            {
                var handler = (Action<ForegroundWindow>)Delegate.CreateDelegate(typeof(Action<ForegroundWindow>),
                    window, "OnForegroundWindowChanged");
                PlatformServices.Foreground.Changed -= handler;
                Field<IDisposable>(window, "_foreignProvider").Dispose();
                var shutdown = Field<CancellationTokenSource>(window, "_shutdown");
                shutdown.Cancel();
                shutdown.Dispose();
                typeof(MainWindow).GetField("_shutdownComplete", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(window, true);
                window.Close();
            }
            foreach (var provider in _providers)
                foreach (var runtime in provider.Runtimes) runtime.DisposeAsync().GetAwaiter().GetResult();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeProvider : IPaneProvider
    {
        public PaneKind Kind => PaneKind.Terminal;
        public List<PaneProviderContext> Created { get; } = [];
        public List<FakeRuntime> Runtimes { get; } = [];
        public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken cancellationToken = default)
        {
            Created.Add(context);
            var runtime = new FakeRuntime(context);
            Runtimes.Add(runtime);
            return ValueTask.FromResult<IPaneRuntime>(runtime);
        }
        public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This test must never trigger MainWindow.Opened or restore providers.");
    }

    private sealed class FakeRuntime(PaneProviderContext context) : IPaneRuntime
    {
        public PaneId PaneId => context.PaneId;
        public PaneKind Kind => PaneKind.Terminal;
        public Control View { get; } = new Border();
        public string? StatusMessage => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public bool Focus() => false;
        public void Arrange(PaneArrangement arrangement) { }
        public void RefreshRestoreState() { }
        public RestoreDescriptor CaptureRestoreDescriptor() => context.Descriptor;
        public ValueTask<PaneCloseResult> CloseAsync(PaneCloseReason reason, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PaneCloseResult.Success());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
