using System.Runtime.CompilerServices;

namespace WinMux.Shell.Tests;

internal static class TestDataDirectory
{
    [ModuleInitializer]
    internal static void IsolateProcessData()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinMux.Tests", Environment.ProcessId.ToString(),
            Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(WinMux.Core.WinMuxDataDirectory.EnvironmentVariable, path);
    }
}
