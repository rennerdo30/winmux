using System.Security.Cryptography;
using System.Text;

namespace WinMux.Core;

/// <summary>One process-scoped data identity shared by the GUI and command-line client.</summary>
public static class WinMuxDataDirectory
{
    public const string EnvironmentVariable = "WINMUX_DATA_DIR";

    public static string? OverridePath
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        }
    }

    public static bool IsIsolated => OverridePath is not null;

    public static string RoamingPath => OverridePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinMux");

    public static string LocalPath => OverridePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMux");

    /// <summary>Stable for the same directory, distinct from the installed instance.</summary>
    public static string EndpointSuffix
    {
        get
        {
            var path = OverridePath;
            if (path is null) return string.Empty;
            var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
            return "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        }
    }

    public static string CommandPipeName => "winmux-command-v1" + EndpointSuffix;
}
