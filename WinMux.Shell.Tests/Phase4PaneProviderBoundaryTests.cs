using System.Reflection;
using System.Runtime.Versioning;
using Avalonia.Controls;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Tests;

public sealed class Phase4PaneProviderBoundaryTests
{
    [Fact]
    public async Task External_assembly_can_register_and_restore_a_custom_pane_kind()
    {
        var kind = PaneKind.Create("com.example.preview");
        var provider = new CustomProvider(kind);
        var registry = new PaneProviderRegistry([provider]);
        var descriptor = new RestoreDescriptor
        {
            Kind = kind,
            Title = "preview",
            Extras = new Dictionary<string, string> { ["document"] = "guide.md" },
        };

        var runtime = await registry.RestoreAsync(
            new PaneProviderContext(PaneId.New(), "preview", descriptor));

        Assert.Equal(kind, runtime.Kind);
        Assert.Equal("guide.md", runtime.CaptureRestoreDescriptor().Extras["document"]);
        Assert.Equal(1, provider.RestoreCount);
    }

    [Fact]
    public void Provider_contract_is_os_neutral_and_does_not_reference_runtime_projects()
    {
        var assembly = typeof(IPaneProvider).Assembly;
        var forbiddenPrefixes = new[]
        {
            "WinMux.Platform", "WinMux.Shell", "WinMux.PaneHost", "WinMux.Pty",
            "Microsoft.Win32", "System.Management",
        };
        var offenders = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => forbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Empty(offenders);
        Assert.Null(assembly.GetCustomAttribute<TargetPlatformAttribute>());
    }

    private sealed class CustomProvider(PaneKind kind) : IPaneProvider
    {
        public PaneKind Kind { get; } = kind;
        public int RestoreCount { get; private set; }

        public ValueTask<IPaneRuntime> CreateAsync(
            PaneProviderContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IPaneRuntime>(new CustomRuntime(context));

        public ValueTask<IPaneRuntime> RestoreAsync(
            PaneProviderContext context,
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            return ValueTask.FromResult<IPaneRuntime>(new CustomRuntime(context));
        }
    }

    private sealed class CustomRuntime(PaneProviderContext context) : IPaneRuntime
    {
        public PaneId PaneId => context.PaneId;
        public PaneKind Kind => context.Descriptor.Kind;
        public Control View { get; } = new Border();
        public string? StatusMessage => null;
        public event EventHandler? StateChanged { add { } remove { } }
        public bool Focus() => true;
        public void Arrange(PaneArrangement arrangement) { }
        public void RefreshRestoreState() { }
        public RestoreDescriptor CaptureRestoreDescriptor() => context.Descriptor;
        public ValueTask<PaneCloseResult> CloseAsync(
            PaneCloseReason reason,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PaneCloseResult.Success());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
