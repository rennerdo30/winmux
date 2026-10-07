using WinMux.Platform;

namespace WinMux.Shell;

/// <summary>Registers the GUI after startup has selected this process as the primary instance.</summary>
internal static class ApplicationRegistration
{
    public static string? RegisterCurrentExecutable(string? executablePath, IApplicationRegistration registration)
    {
        // Tests, headless harnesses and dotnet-hosted runs must never change the user's launcher.
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !string.Equals(Path.GetFileName(executablePath), "WinMux.exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            registration.RegisterExecutable(executablePath);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or WinMux.Connections.ConnectionSourceException or ArgumentException)
        {
            return $"Could not register WinMux for launching from Explorer: {ex.Message}. "
                + $"You can still open {executablePath} directly. Launch it again after restoring access to your user registry.";
        }
    }
}
