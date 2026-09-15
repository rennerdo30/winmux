using System.Reflection;
using WinMux.Core.Model;

namespace WinMux.Panes;

/// <summary>A provider that was found and could be constructed.</summary>
/// <param name="Provider">The instance, ready to register.</param>
/// <param name="Source">Where it came from, for saying so in the interface.</param>
public sealed record DiscoveredProvider(IPaneProvider Provider, string Source);

/// <summary>A provider that could not be loaded, and why — never silently dropped.</summary>
public sealed record ProviderLoadFailure(string Source, string Reason);

/// <param name="Providers">Everything usable, in the order found.</param>
/// <param name="Failures">Everything that was meant to be a provider and is not.</param>
public sealed record ProviderDiscoveryResult(
    IReadOnlyList<DiscoveredProvider> Providers,
    IReadOnlyList<ProviderLoadFailure> Failures);

/// <summary>
/// Which types in an assembly are pane providers, and what to say about the ones that nearly are.
///
/// `PaneKind` became a string in [ADR 0012](../docs/adr/0012-phase-4-pane-providers-and-file-browser.md)
/// so that a provider in another assembly could define its own kind. That made third-party panes
/// *expressible*; it did not make them *loadable*, and nothing has ever loaded one — which is the
/// same failure as shipping a capability with no interface (CLAUDE.md section 5a), one layer down.
///
/// The rules live here, apart from any file or assembly loading, because they are the part worth
/// testing: a plugin that does not appear and does not say why is the worst outcome available, and
/// that is exactly the case that is hard to reproduce by hand.
/// </summary>
public static class PaneProviderDiscovery
{
    /// <summary>
    /// Sort candidate types into providers and complaints.
    /// </summary>
    /// <param name="types">Every public type in a candidate assembly.</param>
    /// <param name="source">What to call this assembly in a message.</param>
    /// <param name="isKindTaken">
    /// Whether a kind is already registered. A provider claiming <c>terminal</c> must not silently
    /// replace the built-in one — nor be dropped without a word.
    /// </param>
    public static ProviderDiscoveryResult FromTypes(
        IEnumerable<Type> types,
        string source,
        Func<PaneKind, bool>? isKindTaken = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var found = new List<DiscoveredProvider>();
        var failures = new List<ProviderLoadFailure>();
        var claimed = new HashSet<PaneKind>();

        foreach (var type in types)
        {
            if (!typeof(IPaneProvider).IsAssignableFrom(type)) continue;

            // Abstract bases and interfaces in a provider assembly are normal, not mistakes.
            if (type.IsAbstract || type.IsInterface) continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                failures.Add(new ProviderLoadFailure(
                    source,
                    $"{type.Name} implements IPaneProvider but has no public parameterless constructor, " +
                    "so WinMux cannot create it."));
                continue;
            }

            IPaneProvider provider;
            try
            {
                provider = (IPaneProvider)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                // A provider that throws in its constructor must not take the shell down with it.
                failures.Add(new ProviderLoadFailure(
                    source, $"{type.Name} could not be created: {Unwrap(ex).Message}"));
                continue;
            }

            PaneKind kind;
            try
            {
                kind = provider.Kind;
            }
            catch (Exception ex)
            {
                failures.Add(new ProviderLoadFailure(
                    source, $"{type.Name} could not say what kind of pane it provides: {Unwrap(ex).Message}"));
                continue;
            }

            if (isKindTaken?.Invoke(kind) == true || !claimed.Add(kind))
            {
                failures.Add(new ProviderLoadFailure(
                    source,
                    $"{type.Name} claims the pane kind '{kind}', which is already provided. " +
                    "Two providers for one kind would make restoring a session ambiguous."));
                continue;
            }

            found.Add(new DiscoveredProvider(provider, source));
        }

        return new ProviderDiscoveryResult(found, failures);
    }

    /// <summary>
    /// Every public type in an assembly, tolerating one that will not load.
    ///
    /// <see cref="ReflectionTypeLoadException"/> still carries the types it managed to load, and a
    /// provider assembly referencing something absent should cost that provider, not all of them.
    /// </summary>
    public static IReadOnlyList<Type> PublicTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(type => type is not null).Select(type => type!)];
        }
    }

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
}
