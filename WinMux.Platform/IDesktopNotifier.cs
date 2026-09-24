namespace WinMux.Platform;

/// <summary>
/// A notification on the desktop — on Windows, a toast in the corner and an entry in the
/// notification centre.
///
/// For a program in a pane that wants the user back: Claude Code waiting on a permission, a long
/// build that finished. The platform decides how it looks and honours the user's own quiet hours;
/// the shell decides <i>whether</i> to show one, which is why this has no policy of its own.
/// </summary>
public interface IDesktopNotifier : IDisposable
{
    /// <summary>Whether this system can show notifications at all. False means <see cref="Show"/> does nothing.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Why a notification shown now would not be seen, in words for the user — for instance that
    /// notifications are switched off for the whole system — or null when nothing is in the way.
    /// <see cref="Show"/> still works when this is set; the system simply keeps it to itself, and the
    /// application should say so rather than appear to have ignored the request.
    /// </summary>
    string? BlockedReason { get; }

    /// <summary>
    /// Take the user to wherever <see cref="BlockedReason"/> can be lifted — on Windows, the
    /// notification page of the system settings.
    ///
    /// The reason is a sentence telling someone to go somewhere, and a sentence telling someone to
    /// go somewhere is not an interface (CLAUDE.md section 5a). Does nothing when nothing is in the
    /// way, and never throws: failing to open a settings page is not worth an error dialog.
    /// </summary>
    void OpenSystemSettings();

    /// <summary>
    /// Show a notification. Returns at once; the notification appears shortly after.
    /// </summary>
    /// <param name="title">The heading — which pane is asking.</param>
    /// <param name="message">What it said.</param>
    /// <param name="activated">
    /// Called when the user clicks the notification, on a thread of the notifier's choosing — the
    /// caller must marshal to its UI thread. Only the latest notification's callback is kept: a
    /// click answers the most recent question.
    /// </param>
    void Show(string title, string message, Action? activated);

    /// <summary>
    /// Withdraw anything still shown, and whatever presence the platform needed in order to show it.
    /// Called when the user comes back to the application, at which point there is nothing left to
    /// be told.
    /// </summary>
    void Clear();
}
