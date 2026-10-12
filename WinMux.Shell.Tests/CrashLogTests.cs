using System.Reflection;

namespace WinMux.Shell.Tests;

public sealed class CrashLogTests
{
    [Fact]
    public void Entry_uses_product_version_and_isolated_data_directory()
    {
        var marker = "test marker " + Guid.NewGuid().ToString("N");
        CrashLog.Write(marker, null);

        Assert.StartsWith(WinMux.Core.WinMuxDataDirectory.OverridePath!, CrashLog.Path,
            StringComparison.OrdinalIgnoreCase);
        var line = File.ReadLines(CrashLog.Path).Single(line => line.Contains(marker, StringComparison.Ordinal));
        var version = typeof(CrashLog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Contains("version " + version, line, StringComparison.Ordinal);
    }
}
