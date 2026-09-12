using WinMux.Core.Model;
using WinMux.Platform.Win32.ForeignApps;

namespace WinMux.Shell;

/// <summary>
/// Resolves a persisted foreign-app descriptor into concrete PaneHost arguments. This layer is
/// deliberately free of HWND calls: the shell may choose a strategy, but only PaneHost touches
/// the foreign window.
/// </summary>
internal sealed record ForeignAppLaunchPlan(
    HostStrategy Strategy,
    string? WindowClass,
    string? TitleContains,
    string? ProcessName,
    ForeignWindowSelectionMode SelectionMode,
    int SettleMilliseconds,
    string? Limitation,
    bool UsedMeasuredRule)
{
    public static ForeignAppLaunchPlan Resolve(
        RestoreDescriptor restore,
        ForeignAppQuirksDatabase database)
    {
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(database);

        var configuredClass = restore.Extras.GetValueOrDefault("window_class");
        var configuredTitle = restore.Extras.GetValueOrDefault("window_title_contains");
        // Rules may name a launcher separately from the eventual window process. That keeps
        // Calculator/ApplicationFrameHost support without letting a generic class-only match steal
        // an unrelated application's window.
        var selected = database.Select(restore.Program, configuredClass, configuredTitle);
        var rule = selected.Rule;

        var strategy = restore.Strategy == HostStrategy.Auto ? rule.Strategy : restore.Strategy;
        var windowClass = configuredClass;
        if (string.IsNullOrWhiteSpace(windowClass) &&
            rule.WindowSelection.Mode == ForeignWindowSelectionMode.ClassName)
        {
            windowClass = rule.WindowSelection.ClassName;
        }
        var titleContains = string.IsNullOrWhiteSpace(configuredTitle)
            ? rule.Match.TitleContains
            : configuredTitle;
        var selectionMode = string.IsNullOrWhiteSpace(configuredClass)
            ? rule.WindowSelection.Mode
            : ForeignWindowSelectionMode.ClassName;
        var processName = selectionMode == ForeignWindowSelectionMode.ProcessName
            ? rule.WindowSelection.ProcessName
            : null;

        var settle = rule.LaunchDelayMilliseconds;
        if (restore.Extras.GetValueOrDefault("launch_delay_ms") is { Length: > 0 } configuredDelay &&
            (!int.TryParse(configuredDelay, out var parsedDelay) || parsedDelay < 0))
        {
            throw new ArgumentException("foreign-app launch_delay_ms must be a non-negative integer");
        }
        if (restore.Extras.GetValueOrDefault("launch_delay_ms") is { Length: > 0 } validDelay)
        {
            settle = int.Parse(validDelay);
        }

        return new ForeignAppLaunchPlan(
            strategy,
            string.IsNullOrWhiteSpace(windowClass) ? null : windowClass,
            string.IsNullOrWhiteSpace(titleContains) ? null : titleContains,
            processName,
            selectionMode,
            settle,
            rule.Limitations,
            UsedMeasuredRule: !selected.IsDefault);
    }

    public string StrategyArgument => Strategy switch
    {
        HostStrategy.Embed => "embed",
        HostStrategy.Attach => "attach",
        _ => throw new InvalidOperationException("An automatic strategy must be resolved before PaneHost starts."),
    };

    public string MatchModeArgument => SelectionMode switch
    {
        ForeignWindowSelectionMode.Pid => "pid",
        ForeignWindowSelectionMode.ProcessName => "process-name",
        ForeignWindowSelectionMode.ClassName => "class-name",
        _ => throw new InvalidOperationException("Unknown foreign-window selection mode."),
    };
}
