using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Terminal;

namespace WinMux.Shell.Notifications;

/// <summary>A pane runtime whose program can ask for the user's attention.</summary>
internal interface IAttentionRuntime
{
    /// <summary>Raised on the UI thread.</summary>
    event Action<TerminalNotification>? AttentionRequested;
}

/// <summary>
/// Whether a request for attention from a pane becomes a desktop notification, and what it says.
///
/// Kept apart from the window so the rules can be tested, and the rules are the product here: a
/// notification about the pane already in front of you is noise, a program that rings the bell in
/// a loop must not bury the notification centre, and the setting decides whether a bare bell counts.
/// </summary>
internal sealed class TerminalAttention
{
    /// <summary>At most one notification per pane in this span, however often it asks.</summary>
    public static readonly TimeSpan PerPaneQuiet = TimeSpan.FromSeconds(3);

    /// <summary>The same words from the same pane are not repeated inside this span.</summary>
    public static readonly TimeSpan RepeatQuiet = TimeSpan.FromSeconds(30);

    private readonly Dictionary<PaneId, (DateTimeOffset At, string Text)> _last = [];
    private readonly Dictionary<PaneId, string> _waiting = [];

    /// <param name="looking">
    /// The user is looking at this pane: WinMux is the active window and this is the focused pane.
    /// </param>
    public bool ShouldNotify(
        TerminalNotificationPolicy policy,
        TerminalNotification notification,
        PaneId pane,
        bool looking,
        DateTimeOffset now)
    {
        if (policy == TerminalNotificationPolicy.Off) return false;
        if (notification.Kind == TerminalNotificationKind.Bell && policy != TerminalNotificationPolicy.MessagesAndBells)
            return false;
        if (looking) return false;

        var text = $"{notification.Title}\n{notification.Body}";
        if (_last.TryGetValue(pane, out var last))
        {
            if (now - last.At < PerPaneQuiet) return false;
            if (last.Text == text && now - last.At < RepeatQuiet) return false;
        }

        _last[pane] = (now, text);
        return true;
    }

    /// <summary>
    /// Whether a request should leave a mark on the pane's tab.
    ///
    /// <para>
    /// The same policy filter as a notification and none of its quiet periods. A toast repeated
    /// every second would bury the notification centre, which is why those exist; a mark that is
    /// already showing cannot be shown twice, so there is nothing to rate-limit. A program ringing
    /// in a loop is a program that still wants you.
    /// </para>
    /// </summary>
    public static bool ShouldMark(TerminalNotificationPolicy policy, TerminalNotification notification) =>
        policy != TerminalNotificationPolicy.Off &&
        (notification.Kind != TerminalNotificationKind.Bell || policy == TerminalNotificationPolicy.MessagesAndBells);

    /// <summary>Mark a pane as waiting, with what it said, for the tab to show and explain.</summary>
    public void Mark(PaneId pane, string message) => _waiting[pane] = message;

    /// <summary>
    /// Drop the mark, because the user has looked. Returns true when there was one, so the caller
    /// can skip a relayout it does not need.
    /// </summary>
    public bool Seen(PaneId pane) => _waiting.Remove(pane);

    /// <summary>What <paramref name="pane"/> is waiting to say, or null when it is not waiting.</summary>
    public string? WaitingMessage(PaneId pane) => _waiting.GetValueOrDefault(pane);

    /// <summary>Whether any pane is waiting. Cheap enough to ask on every tab that is drawn.</summary>
    public bool IsWaiting(PaneId pane) => _waiting.ContainsKey(pane);

    /// <summary>Forget a pane that has closed.</summary>
    public void Forget(PaneId pane)
    {
        _last.Remove(pane);
        _waiting.Remove(pane);
    }

    /// <summary>
    /// The heading names the pane, because "Claude needs your permission" is only useful once you
    /// know which of six Claude sessions said it.
    /// </summary>
    public static (string Title, string Message) Compose(string paneTitle, TerminalNotification notification)
    {
        var pane = string.IsNullOrWhiteSpace(paneTitle) ? "A terminal pane" : paneTitle.Trim();
        var title = notification.Title is { Length: > 0 } heading ? $"{pane} · {heading}" : pane;
        var message = notification.Kind == TerminalNotificationKind.Bell || notification.Body.Length == 0
            ? "Rang the bell. It may be waiting for you."
            : notification.Body;
        return (title, message);
    }
}
