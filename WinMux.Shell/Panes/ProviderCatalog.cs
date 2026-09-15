using System.Reflection;
using System.Runtime.Loader;
using WinMux.Core.Model;
using WinMux.Panes;

namespace WinMux.Shell.Panes;

/// <summary>
/// Loading pane providers that did not ship with WinMux.
///
/// `PaneKind` stopped being an enum in
/// [ADR 0012](../../docs/adr/0012-phase-4-pane-providers-and-file-browser.md) specifically so a
/// provider in another assembly could define its own kind, and nothing has ever loaded one. The
/// extension point existed in the type system and nowhere else.
///
/// **Layout:** `providers/` beside `WinMux.exe`, one directory per provider, each holding its
/// assembly and whatever it depends on. One directory apiece rather than one flat folder because
/// two providers wanting different versions of the same library is the normal case, not the exotic
/// one, and a flat folder makes that unresolvable.
///
/// **Loading:** each directory gets its own <see cref="AssemblyLoadContext"/> with a dependency
/// resolver, so a provider's private dependencies stay private. The contract assemblies are the
/// deliberate exception: <c>WinMux.Panes</c>, <c>WinMux.Core</c> and Avalonia must be the *same*
/// types the shell is using, or an <c>IPaneProvider</c> from a provider would not be assignable to
/// the <c>IPaneProvider</c> the registry wants — the classic plugin failure, and a confusing one
/// because the type names match. Returning null from <see cref="Load"/> defers to the default
/// context and gets that unification.
///
/// **Failure is loud.** A provider that does not appear and does not say why is worse than one that
/// never shipped, so every reason is collected and surfaced rather than swallowed.
/// </summary>
internal static class ProviderCatalog
{
    /// <summary>The folder providers are loaded from, relative to the executable.</summary>
    public const string DirectoryName = "providers";

    /// <summary>
    /// Load every provider found beside the executable.
    /// </summary>
    /// <param name="isKindTaken">Whether a pane kind is already provided by a built-in.</param>
    /// <param name="root">The directory to scan. Defaults to <c>providers/</c> beside the app.</param>
    public static ProviderDiscoveryResult Load(Func<PaneKind, bool> isKindTaken, string? root = null)
    {
        var directory = root ?? DefaultDirectory();
        if (!Directory.Exists(directory))
        {
            // Not an error, and not worth a message: most installations have no providers.
            return new ProviderDiscoveryResult([], []);
        }

        var providers = new List<DiscoveredProvider>();
        var failures = new List<ProviderLoadFailure>();
        var taken = new HashSet<PaneKind>();

        bool Taken(PaneKind kind) => isKindTaken(kind) || taken.Contains(kind);

        foreach (var folder in Directory.EnumerateDirectories(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(folder);
            var assemblyPath = Path.Combine(folder, name + ".dll");

            if (!File.Exists(assemblyPath))
            {
                failures.Add(new ProviderLoadFailure(
                    name,
                    $"expected {name}.dll in {folder}; a provider directory is named after its assembly."));
                continue;
            }

            try
            {
                var context = new ProviderLoadContext(assemblyPath);
                var assembly = context.LoadFromAssemblyPath(assemblyPath);
                var result = PaneProviderDiscovery.FromTypes(
                    PaneProviderDiscovery.PublicTypes(assembly), name, Taken);

                foreach (var provider in result.Providers) taken.Add(provider.Provider.Kind);
                providers.AddRange(result.Providers);
                failures.AddRange(result.Failures);

                if (result.Providers.Count == 0 && result.Failures.Count == 0)
                    failures.Add(new ProviderLoadFailure(name, "contains no pane providers."));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException)
            {
                failures.Add(new ProviderLoadFailure(name, "could not be loaded: " + ex.Message));
            }
        }

        return new ProviderDiscoveryResult(providers, failures);
    }

    private static string DefaultDirectory() =>
        Path.Combine(AppContext.BaseDirectory, DirectoryName);

    private sealed class ProviderLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Anything the shell already has must unify with the shell's copy, or the provider's
            // IPaneProvider would be a different type from ours and nothing would be assignable.
            foreach (var loaded in Default.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
