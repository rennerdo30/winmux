namespace WinMux.Shell.Actions;

public enum ActionDispatchStatus
{
    Executed,
    NotRegistered,
    Failed,
}

/// <summary>
/// The explicit result of invoking a named action. Failures are returned so callers can surface
/// them in shell chrome instead of silently losing an input command.
/// </summary>
public sealed record ActionDispatchResult(
    string ActionName,
    ActionDispatchStatus Status,
    string? Error = null,
    Exception? Exception = null)
{
    public bool Succeeded => Status == ActionDispatchStatus.Executed;
}

/// <summary>
/// One registry for every shell action, independent of whether it was requested by the keymap,
/// command palette, or CLI.
/// </summary>
public sealed class ActionDispatcher
{
    private readonly Dictionary<string, Action> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> RegisteredActions => _handlers.Keys;

    public void Register(string actionName, Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentNullException.ThrowIfNull(handler);

        actionName = actionName.Trim();
        if (!_handlers.TryAdd(actionName, handler))
            throw new InvalidOperationException($"Action '{actionName}' is already registered.");
    }

    public bool Unregister(string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        return _handlers.Remove(actionName.Trim());
    }

    public ActionDispatchResult Dispatch(string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        actionName = actionName.Trim();

        if (!_handlers.TryGetValue(actionName, out var handler))
        {
            return new ActionDispatchResult(
                actionName,
                ActionDispatchStatus.NotRegistered,
                $"Action '{actionName}' is not registered.");
        }

        try
        {
            handler();
            return new ActionDispatchResult(actionName, ActionDispatchStatus.Executed);
        }
        catch (Exception ex)
        {
            return new ActionDispatchResult(
                actionName,
                ActionDispatchStatus.Failed,
                $"Action '{actionName}' failed: {ex.Message}",
                ex);
        }
    }
}
