namespace WinMux.Platform;

/// <summary>
/// Telling the user something went wrong before, or instead of, a window existing.
///
/// Small, but it has to be here: the startup failure path runs when there may be no Avalonia
/// window to put a message in, and "no silent failure around embedding or persistence"
/// (CLAUDE.md section 8) is only true if that path can actually say something.
/// </summary>
public interface IUserNotifier
{
    /// <summary>
    /// Show an error and return once the user has acknowledged it. Blocking is the point: it is
    /// used while the process is failing to start, and returning early would race the exit.
    /// </summary>
    void ShowError(string title, string message);
}
