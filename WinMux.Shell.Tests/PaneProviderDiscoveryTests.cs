using WinMux.Core.Model;
using WinMux.Panes;
using WinMux.Shell.Panes;

namespace WinMux.Shell.Tests;

/// <summary>
/// Which types count as a pane provider, and what is said about the ones that nearly do.
///
/// `PaneKind` stopped being an enum in ADR 0012 so a provider in another assembly could define its
/// own kind, and until now nothing loaded one: the extension point existed in the type system and
/// nowhere else. These pin the rules, and especially the refusals — a plugin that does not appear
/// and does not say why is the worst outcome available, and the hardest to reproduce by hand.
/// </summary>
public sealed class PaneProviderDiscoveryTests
{
    private abstract class ProviderBase : IPaneProvider
    {
        public abstract PaneKind Kind { get; }

        public ValueTask<IPaneRuntime> CreateAsync(PaneProviderContext context, CancellationToken token = default) =>
            throw new NotSupportedException();

        public ValueTask<IPaneRuntime> RestoreAsync(PaneProviderContext context, CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    private sealed class GoodProvider : ProviderBase
    {
        public override PaneKind Kind => PaneKind.Create("com.example.good");
    }

    private sealed class SecondProvider : ProviderBase
    {
        public override PaneKind Kind => PaneKind.Create("com.example.second");
    }

    private sealed class DuplicateProvider : ProviderBase
    {
        public override PaneKind Kind => PaneKind.Create("com.example.good");
    }

    private sealed class TerminalImpostor : ProviderBase
    {
        public override PaneKind Kind => PaneKind.Terminal;
    }

    private sealed class NeedsArguments : ProviderBase
    {
        // A provider that wants configuration handed to it. Plausible, and unloadable.
        public NeedsArguments(string configuration) => Configuration = configuration;

        public string Configuration { get; }

        public override PaneKind Kind => PaneKind.Create("com.example.args");
    }

    private sealed class ThrowsOnConstruction : ProviderBase
    {
        public ThrowsOnConstruction() => throw new InvalidOperationException("no configuration file");
        public override PaneKind Kind => PaneKind.Create("com.example.throws");
    }

    private sealed class ThrowsOnKind : ProviderBase
    {
        public override PaneKind Kind => throw new InvalidOperationException("kind is unset");
    }

    private sealed class NotAProvider;

    [Fact]
    public void A_well_formed_provider_is_found()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(GoodProvider)], "example");

        var found = Assert.Single(result.Providers);
        Assert.Equal(PaneKind.Create("com.example.good"), found.Provider.Kind);
        Assert.Equal("example", found.Source);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void Types_that_are_not_providers_are_ignored_silently()
    {
        // Every assembly is full of types that are not providers; complaining about them would
        // bury the one complaint that matters.
        var result = PaneProviderDiscovery.FromTypes([typeof(NotAProvider), typeof(string)], "example");

        Assert.Empty(result.Providers);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void An_abstract_base_is_not_a_failure()
    {
        // A provider assembly with a shared base class is normal, not a mistake.
        var result = PaneProviderDiscovery.FromTypes([typeof(ProviderBase)], "example");

        Assert.Empty(result.Providers);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void A_provider_with_no_parameterless_constructor_says_so()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(NeedsArguments)], "example");

        Assert.Empty(result.Providers);
        var failure = Assert.Single(result.Failures);
        Assert.Contains("parameterless constructor", failure.Reason, StringComparison.Ordinal);
        Assert.Contains(nameof(NeedsArguments), failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_that_throws_while_being_created_does_not_take_the_shell_with_it()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(ThrowsOnConstruction)], "example");

        Assert.Empty(result.Providers);
        // The provider's own reason, not a reflection wrapper's.
        Assert.Contains("no configuration file", Assert.Single(result.Failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_that_throws_when_asked_its_kind_is_reported()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(ThrowsOnKind)], "example");

        Assert.Empty(result.Providers);
        Assert.Contains("kind is unset", Assert.Single(result.Failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void One_bad_provider_does_not_cost_the_good_ones()
    {
        var result = PaneProviderDiscovery.FromTypes(
            [typeof(ThrowsOnConstruction), typeof(GoodProvider), typeof(NeedsArguments)],
            "example");

        Assert.Single(result.Providers);
        Assert.Equal(2, result.Failures.Count);
    }

    [Fact]
    public void A_provider_cannot_take_a_kind_a_built_in_already_provides()
    {
        // Silently replacing the terminal provider would be a very confusing way to lose terminals.
        var result = PaneProviderDiscovery.FromTypes(
            [typeof(TerminalImpostor)],
            "example",
            isKindTaken: kind => kind == PaneKind.Terminal);

        Assert.Empty(result.Providers);
        Assert.Contains("already provided", Assert.Single(result.Failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_providers_in_one_assembly_cannot_share_a_kind()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(GoodProvider), typeof(DuplicateProvider)], "example");

        Assert.Single(result.Providers);
        Assert.Contains("already provided", Assert.Single(result.Failures).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_providers_in_one_assembly_are_all_found()
    {
        var result = PaneProviderDiscovery.FromTypes([typeof(GoodProvider), typeof(SecondProvider)], "example");

        Assert.Equal(2, result.Providers.Count);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void A_discovered_provider_registers_like_any_other()
    {
        // The whole point: a provider from outside is not a second-class citizen.
        var registry = new PaneProviderRegistry();
        var result = PaneProviderDiscovery.FromTypes([typeof(GoodProvider)], "example");

        registry.Register(result.Providers[0].Provider);

        Assert.True(registry.TryGet(PaneKind.Create("com.example.good"), out _));
    }
}

/// <summary>Scanning the providers folder, which is the half that touches the disk.</summary>
public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "winmux-providers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void No_providers_directory_is_not_an_error()
    {
        // Most installations have none, and a message on every launch would be noise.
        var result = ProviderCatalog.Load(_ => false, Path.Combine(_root, "absent"));

        Assert.Empty(result.Providers);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void An_empty_providers_directory_is_not_an_error()
    {
        Directory.CreateDirectory(_root);

        var result = ProviderCatalog.Load(_ => false, _root);

        Assert.Empty(result.Providers);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void A_directory_without_its_assembly_says_what_was_expected()
    {
        // The most likely installation mistake: unzipping to the wrong depth.
        Directory.CreateDirectory(Path.Combine(_root, "Acme.Provider"));

        var result = ProviderCatalog.Load(_ => false, _root);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Acme.Provider", failure.Source);
        Assert.Contains("Acme.Provider.dll", failure.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_reported_rather_than_thrown()
    {
        var folder = Path.Combine(_root, "Broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Broken.dll"), "this is not a PE file");

        var result = ProviderCatalog.Load(_ => false, _root);

        Assert.Empty(result.Providers);
        Assert.Equal("Broken", Assert.Single(result.Failures).Source);
    }
}
