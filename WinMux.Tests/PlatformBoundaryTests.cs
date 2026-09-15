using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using WinMux.Platform;

namespace WinMux.Tests;

/// <summary>
/// Phase 5's boundary, enforced the same way Core's is: by test, not by discipline (CLAUDE.md
/// section 8).
///
/// `WinMux.Platform` is the *shape* of an operating system — <c>IHostWindowService</c>,
/// <c>IProcessInspector</c>, <c>IUserNotifier</c> — and nothing else. Priority 6 says cross-platform
/// is a design discipline rather than a shipping commitment; the discipline is only real if
/// something fails when it lapses. These tests are that something. They would each have passed
/// trivially before the extraction, so each one names what it would catch.
/// </summary>
public class PlatformBoundaryTests
{
    private static readonly Assembly Platform = typeof(IHostWindowService).Assembly;

    [Fact]
    public void Platform_targets_a_platform_neutral_framework()
    {
        // net10.0-windows here would be the quietest possible way to lose the boundary: nothing
        // else about the build changes, and suddenly the contract can name a Windows type.
        var platform = Platform.GetCustomAttribute<TargetPlatformAttribute>();
        Assert.True(platform is null,
            $"WinMux.Platform targets \"{platform?.PlatformName}\". The interface must target net10.0 " +
            "with no OS suffix — the implementations are what get an OS.");

        var xml = File.ReadAllText(Metadata("PlatformProjectPath"));
        var match = Regex.Match(xml, @"<TargetFrameworks?>([^<]+)</TargetFrameworks?>");
        Assert.True(match.Success, "WinMux.Platform declares no TargetFramework.");
        Assert.Equal("net10.0", match.Groups[1].Value.Trim());
    }

    [Fact]
    public void Platform_declares_only_a_reference_to_Core()
    {
        // Same reasoning as CoreIsPlatformFreeTests: GetReferencedAssemblies() cannot see a
        // dependency the compiler did not need, so the declaration is what has to be asserted.
        var xml = File.ReadAllText(Metadata("PlatformProjectPath"));
        var declared = Regex
            .Matches(xml, @"<(PackageReference|ProjectReference|FrameworkReference)\s+Include=""([^""]+)""")
            .Select(m => m.Groups[2].Value)
            .Where(name => !name.EndsWith(@"WinMux.Core\WinMux.Core.csproj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(declared.Length == 0,
            "WinMux.Platform declares dependencies beyond WinMux.Core: " + string.Join(", ", declared) +
            ". It is a contract; anything it needs to express belongs in Core.");
    }

    [Fact]
    public void Platform_references_no_platform_assembly()
    {
        string[] forbidden =
            ["Avalonia", "Microsoft.Win32", "Microsoft.Windows", "System.Drawing", "System.Management",
             "WinMux.Platform.Win32", "WinMux.Shell", "WinMux.Pty", "Terminal."];

        var offenders = Platform.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => forbidden.Any(f => name.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "WinMux.Platform references platform assemblies: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Platform_contains_no_P_Invoke()
    {
        // The contract states intent. The moment it can call an OS directly, implementations stop
        // being the only place an OS is named, and the second platform becomes impossible to add.
        var offenders = Platform.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
                BindingFlags.DeclaredOnly))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "WinMux.Platform declares P/Invoke: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Platform_exposes_no_type_named_after_one_operating_system()
    {
        // WindowHandle is deliberately allowed: it is an opaque integer handle every windowing
        // system has. "Hwnd" is not — it is Windows leaking through a name.
        string[] banned = ["Hwnd", "HWND", "Win32", "Wayland", "Cocoa", "Nswindow"];

        var offenders = Platform.GetExportedTypes()
            .Where(t => banned.Any(b => t.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.FullName!)
            .ToArray();

        Assert.True(offenders.Length == 0, "OS-shaped types in WinMux.Platform: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_shell_makes_no_operating_system_call_of_its_own()
    {
        // Phase 5's headline: WinMux.Shell used to carry Win32Interop.cs and 24 imports, most of
        // them dead. The shell is now application logic, and this is what keeps it that way.
        // A source scan rather than reflection, because the shell is net10.0-windows/x64 and this
        // guard project is deliberately neither.
        var offenders = SourceFiles("WinMux.Shell")
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"\[\s*(DllImport|LibraryImport)"))
            .Select(RepositoryRelative)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "WinMux.Shell declares P/Invoke in: " + string.Join(", ", offenders) +
            ". Platform work belongs behind WinMux.Platform, implemented in WinMux.Platform.Win32.");
    }

    [Fact]
    public void The_shell_names_a_concrete_operating_system_in_exactly_one_file()
    {
        // The composition root. One seam is a design; two is the boundary quietly dissolving.
        var offenders = SourceFiles("WinMux.Shell")
            .Where(file => File.ReadAllText(file).Contains("WinMux.Platform.Win32.Windows", StringComparison.Ordinal))
            .Select(RepositoryRelative)
            .ToArray();

        Assert.True(
            offenders is [var only] && only.EndsWith("PlatformServices.cs", StringComparison.OrdinalIgnoreCase),
            "WinMux.Shell should name the Win32 implementations only in PlatformServices.cs, but found: " +
            (offenders.Length == 0 ? "nothing at all" : string.Join(", ", offenders)));
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(Metadata("RepositoryRoot"), project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RepositoryRelative(string path) =>
        Path.GetRelativePath(Metadata("RepositoryRoot"), path);

    private static string Metadata(string key)
    {
        var value = typeof(PlatformBoundaryTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        Assert.False(string.IsNullOrEmpty(value), $"The test project must pass {key} through as assembly metadata.");
        Assert.True(Path.Exists(value), $"{key} does not exist at {value}");
        return value!;
    }
}
