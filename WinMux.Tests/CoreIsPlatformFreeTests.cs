using System.Reflection;
using System.Runtime.Versioning;
using WinMux.Core.Layout;

namespace WinMux.Tests;

/// <summary>
/// CLAUDE.md section 3: "`WinMux.Core` must not reference any platform assembly. Enforce it with a
/// test that asserts the dependency set." Section 8 repeats it: enforced by test, not by discipline.
///
/// If the layout engine ever needs an HWND, the design has gone wrong — and this is what says so.
/// </summary>
public class CoreIsPlatformFreeTests
{
    private static readonly Assembly Core = typeof(LayoutTree).Assembly;

    /// <summary>Anything here in the dependency set means the portability boundary has been crossed.</summary>
    private static readonly string[] Forbidden =
    [
        "Avalonia",
        "System.Windows",
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "Microsoft.Win32",
        "Microsoft.Windows",
        "System.Drawing",
        "System.Management",
        "Terminal.",          // the adopted VT engine — ADR 0002 forbids its types reaching Core
        "Porta.Pty",
        "WinMux.Platform",
        "WinMux.Pty",
        "WinMux.PaneHost",
        "WinMux.Shell",
    ];

    [Fact]
    public void Core_references_no_platform_assembly()
    {
        var offenders = Core.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => Forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "WinMux.Core references platform assemblies: " + string.Join(", ", offenders) +
            ". Everything portable lives in Core; platform work belongs behind WinMux.Platform.");
    }

    [Fact]
    public void Core_references_only_the_base_class_library()
    {
        string[] allowed = ["System", "netstandard", "mscorlib", "WinMux.Core"];

        var unexpected = Core.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => !allowed.Any(p =>
                name.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(unexpected.Length == 0,
            "WinMux.Core picked up dependencies beyond the BCL: " + string.Join(", ", unexpected) +
            ". Adding one is a design decision that belongs in an ADR first.");
    }

    [Fact]
    public void Core_targets_a_platform_neutral_framework()
    {
        // A platform-specific TFM (net10.0-windows) emits TargetPlatformAttribute. That is the
        // quietest way for the boundary to rot, because nothing else about the build changes.
        var platform = Core.GetCustomAttribute<TargetPlatformAttribute>();
        Assert.True(platform is null,
            $"WinMux.Core targets platform \"{platform?.PlatformName}\". It must target net10.0 with no OS suffix.");

        var framework = Core.GetCustomAttribute<TargetFrameworkAttribute>();
        Assert.NotNull(framework);
        Assert.StartsWith(".NETCoreApp", framework!.FrameworkName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared dependencies, not the used ones. `GetReferencedAssemblies` reports only what
    /// the compiler actually emitted a reference for, so a platform package that is referenced but
    /// not yet called would pass every other test in this file while the boundary is already gone.
    /// </summary>
    [Fact]
    public void Core_declares_no_dependencies_at_all()
    {
        var xml = File.ReadAllText(CoreProjectPath());

        var declared = System.Text.RegularExpressions.Regex
            .Matches(xml, @"<(PackageReference|ProjectReference|FrameworkReference)\s+Include=""([^""]+)""")
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .ToArray();

        Assert.True(declared.Length == 0,
            "WinMux.Core declares dependencies: " + string.Join(", ", declared) +
            ". Core is the portable half of the product and depends on the BCL only; " +
            "adding anything here is a design decision that belongs in an ADR first.");
    }

    [Fact]
    public void Core_project_targets_exactly_net10_with_no_os_suffix()
    {
        var xml = File.ReadAllText(CoreProjectPath());
        var match = System.Text.RegularExpressions.Regex.Match(xml, @"<TargetFrameworks?>([^<]+)</TargetFrameworks?>");

        Assert.True(match.Success, "WinMux.Core declares no TargetFramework.");
        Assert.Equal("net10.0", match.Groups[1].Value.Trim());
    }

    private static string CoreProjectPath()
    {
        var path = Core is not null
            ? typeof(CoreIsPlatformFreeTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "CoreProjectPath")?.Value
            : null;

        Assert.False(string.IsNullOrEmpty(path),
            "The test project must pass CoreProjectPath through as assembly metadata.");
        Assert.True(File.Exists(path), $"Core project not found at {path}");
        return path!;
    }

    [Fact]
    public void Core_exposes_no_type_named_after_a_platform_primitive()
    {
        string[] banned = ["Hwnd", "HWND", "Win32", "WindowHandle", "IntPtrRect"];

        var offenders = Core.GetExportedTypes()
            .Where(t => banned.Any(b => t.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.True(offenders.Length == 0, "Platform-shaped types in Core: " + string.Join(", ", offenders));
    }
}
