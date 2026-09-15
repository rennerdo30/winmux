using System.Diagnostics.CodeAnalysis;
using WinMux.Core.Model;

namespace WinMux.Panes;

public sealed class PaneProviderRegistry
{
    private readonly Dictionary<PaneKind, IPaneProvider> _providers = [];

    public PaneProviderRegistry() { }

    public PaneProviderRegistry(IEnumerable<IPaneProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (var provider in providers) Register(provider);
    }

    public int Count => _providers.Count;
    public IReadOnlyCollection<IPaneProvider> Providers => _providers.Values;

    public void Register(IPaneProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_providers.TryAdd(provider.Kind, provider))
            throw new DuplicatePaneProviderException(provider.Kind);
    }

    public bool TryGet(PaneKind kind, [NotNullWhen(true)] out IPaneProvider? provider) =>
        _providers.TryGetValue(kind, out provider);

    public IPaneProvider GetRequired(PaneKind kind) =>
        TryGet(kind, out var provider) ? provider : throw new PaneProviderNotFoundException(kind);

    public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
        GetRequired(context.Descriptor.Kind).CreateAsync(context, token);

    public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
        GetRequired(context.Descriptor.Kind).RestoreAsync(context, token);
}

public sealed class DuplicatePaneProviderException(PaneKind kind)
    : InvalidOperationException($"A pane provider is already registered for '{kind}'.")
{
    public PaneKind Kind { get; } = kind;
}

public sealed class PaneProviderNotFoundException(PaneKind kind)
    : KeyNotFoundException($"No pane provider is registered for '{kind}'. Install or register that provider to restore this pane.")
{
    public PaneKind Kind { get; } = kind;
}
