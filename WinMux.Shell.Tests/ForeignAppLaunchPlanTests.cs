using WinMux.Core.Model;
using WinMux.Platform.Win32.ForeignApps;

namespace WinMux.Shell.Tests;

public sealed class ForeignAppLaunchPlanTests
{
    private const string Seed = """
        {
          "version": 1,
          "generatedBy": "test",
          "entries": [{
            "match": {
              "exe": "ApplicationFrameHost.exe",
              "launchExe": "calc.exe",
              "windowClass": "ApplicationFrameWindow"
            },
            "strategy": "attach",
            "windowSelection": { "mode": "ClassName", "processName": "", "className": "ApplicationFrameWindow" },
            "launchDelayMs": 2500,
            "observedFindMs": 406,
            "dpiAwareness": "PerMonitor",
            "limitations": "packaged window refuses embedding",
            "verified": {
              "date": "2026-09-10", "os": "test", "result": "EMBED FAILED",
              "resizeFollows": false, "moveFollows": false, "detachExact": false
            }
          }]
        }
        """;

    private static ForeignAppQuirksDatabase Database => ForeignAppQuirksDatabase.Parse(Seed);

    [Fact]
    public void Auto_uses_a_measured_class_rule_even_when_the_launcher_is_a_shim()
    {
        var restore = Descriptor(HostStrategy.Auto, "calc.exe", "ApplicationFrameWindow");

        var plan = ForeignAppLaunchPlan.Resolve(restore, Database);

        Assert.Equal(HostStrategy.Attach, plan.Strategy);
        Assert.Equal("attach", plan.StrategyArgument);
        Assert.Equal("ApplicationFrameWindow", plan.WindowClass);
        Assert.Null(plan.ProcessName);
        Assert.Equal(ForeignWindowSelectionMode.ClassName, plan.SelectionMode);
        Assert.Equal("class-name", plan.MatchModeArgument);
        Assert.Equal(2500, plan.SettleMilliseconds);
        Assert.True(plan.UsedMeasuredRule);
        Assert.Contains("refuses", plan.Limitation, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_strategy_overrides_the_database_but_keeps_its_selection_rule()
    {
        var restore = Descriptor(HostStrategy.Embed, "calc.exe", "ApplicationFrameWindow");

        var plan = ForeignAppLaunchPlan.Resolve(restore, Database);

        Assert.Equal(HostStrategy.Embed, plan.Strategy);
        Assert.Equal("ApplicationFrameWindow", plan.WindowClass);
        Assert.True(plan.UsedMeasuredRule);
    }

    [Fact]
    public void Descriptor_launch_delay_overrides_the_measured_settle_time()
    {
        var restore = Descriptor(HostStrategy.Auto, "calc.exe", "ApplicationFrameWindow") with
        {
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["window_class"] = "ApplicationFrameWindow",
                ["launch_delay_ms"] = "42",
            },
        };

        Assert.Equal(42, ForeignAppLaunchPlan.Resolve(restore, Database).SettleMilliseconds);
    }

    [Fact]
    public void Unknown_apps_keep_the_documented_embed_default_and_are_marked_unmeasured()
    {
        var plan = ForeignAppLaunchPlan.Resolve(Descriptor(HostStrategy.Auto, "unknown.exe"), Database);

        Assert.Equal(HostStrategy.Embed, plan.Strategy);
        Assert.False(plan.UsedMeasuredRule);
        Assert.Contains("unverified", plan.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_descriptor_launch_delay_is_refused_instead_of_ignored()
    {
        var restore = Descriptor(HostStrategy.Auto, "calc.exe", "ApplicationFrameWindow") with
        {
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["window_class"] = "ApplicationFrameWindow",
                ["launch_delay_ms"] = "eventually",
            },
        };

        var error = Assert.Throws<ArgumentException>(() => ForeignAppLaunchPlan.Resolve(restore, Database));
        Assert.Contains("non-negative integer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Process_name_selection_is_forwarded_instead_of_replaced_by_the_launcher_name()
    {
        var json = Seed.Replace(
            "\"mode\": \"ClassName\", \"processName\": \"\", \"className\": \"ApplicationFrameWindow\"",
            "\"mode\": \"ProcessName\", \"processName\": \"RealApp.exe\", \"className\": \"\"",
            StringComparison.Ordinal);
        var restore = Descriptor(HostStrategy.Auto, "calc.exe");

        var plan = ForeignAppLaunchPlan.Resolve(restore, ForeignAppQuirksDatabase.Parse(json));

        Assert.Equal(ForeignWindowSelectionMode.ProcessName, plan.SelectionMode);
        Assert.Equal("RealApp.exe", plan.ProcessName);
        Assert.Equal("process-name", plan.MatchModeArgument);
    }

    private static RestoreDescriptor Descriptor(HostStrategy strategy, string program, string? windowClass = null) =>
        new()
        {
            Kind = PaneKind.ForeignApp,
            Program = program,
            Strategy = strategy,
            Extras = windowClass is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["window_class"] = windowClass },
        };
}
