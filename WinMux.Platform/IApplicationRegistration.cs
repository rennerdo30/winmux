namespace WinMux.Platform;

/// <summary>Make an executable discoverable by name through the operating system's launcher.</summary>
public interface IApplicationRegistration
{
    /// <summary>
    /// Register an absolute executable path for the current user. Does not change the user's
    /// command-line search path or require administrator privileges. Throws on registration failure.
    /// </summary>
    void RegisterExecutable(string executablePath);
}
