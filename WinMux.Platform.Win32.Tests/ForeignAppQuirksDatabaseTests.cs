using WinMux.Core.Model;
using WinMux.Platform.Win32.ForeignApps;

namespace WinMux.Platform.Win32.Tests;

public sealed class ForeignAppQuirksDatabaseTests
{
    [Fact]
    public void Measured_seed_loads_without_losing_compatibility_evidence()
    {
        var database = ForeignAppQuirksDatabase.Load(FindMeasuredSeed());

        Assert.Equal(1, database.Version);
        Assert.Equal("spikes/02-reparent", database.GeneratedBy);
        Assert.Equal(8, database.Entries.Count);

        var taskManager = database.Select(@"C:\Windows\System32\TASKMGR.EXE", "taskmanagerwindow");
        Assert.False(taskManager.IsDefault);
        Assert.Equal(HostStrategy.Attach, taskManager.Rule.Strategy);
        Assert.Equal(ForeignWindowSelectionMode.ProcessName, taskManager.Rule.WindowSelection.Mode);
        Assert.Equal("Taskmgr.exe", taskManager.Rule.WindowSelection.ProcessName);
        Assert.Equal(1_500, taskManager.Rule.LaunchDelayMilliseconds);
        Assert.Equal(650, taskManager.Rule.ObservedFindMilliseconds);
        Assert.Equal(ForeignAppDpiAwareness.System, taskManager.Rule.DpiAwareness);
        Assert.Equal("UIPI: SetParent refused with ERROR_ACCESS_DENIED (5)", taskManager.Rule.Limitations);
        var verification = Assert.IsType<ForeignAppVerification>(taskManager.Rule.Verification);
        Assert.Equal(new DateOnly(2026, 9, 10), verification.Date);
        Assert.Equal("10.0.26200.0", verification.OperatingSystem);
        Assert.Equal("EMBED FAILED", verification.Result);
        Assert.False(verification.ResizeFollows);
        Assert.False(verification.MoveFollows);
        Assert.False(verification.DetachExact);

        var calculator = database.Select("ApplicationFrameHost.exe", "ApplicationFrameWindow");
        Assert.Equal(
            "SetParent refused with ERROR_INVALID_PARAMETER (87) — the window rejects reparenting",
            calculator.Rule.Limitations);

        var characterMap = database.Select("charmap.exe", "#32770");
        Assert.Equal(HostStrategy.Embed, characterMap.Rule.Strategy);
        Assert.Equal(ForeignWindowSelectionMode.Pid, characterMap.Rule.WindowSelection.Mode);
        Assert.Null(characterMap.Rule.Limitations);
        Assert.True(Assert.IsType<ForeignAppVerification>(characterMap.Rule.Verification).DetachExact);
    }

    [Fact]
    public void Executable_only_lookup_still_finds_a_measured_rule()
    {
        var database = ForeignAppQuirksDatabase.Load(FindMeasuredSeed());

        var result = database.Select(@"C:\Windows\explorer.exe");

        Assert.False(result.IsDefault);
        Assert.Equal("explorer.exe", result.Rule.Match.ExecutableImage);
        Assert.Equal("CabinetWClass", result.Rule.Match.WindowClass);
        Assert.Equal(ForeignWindowSelectionMode.ClassName, result.Rule.WindowSelection.Mode);
        Assert.Equal(2_000, result.Rule.LaunchDelayMilliseconds);
    }

    [Fact]
    public void Exact_executable_and_class_rule_beats_single_criterion_rules()
    {
        var database = ForeignAppQuirksDatabase.Parse(SpecificityDatabase);

        var result = database.Select(@"D:\Apps\tool.exe", "MainWindow");

        Assert.False(result.IsDefault);
        Assert.Equal(333, result.Rule.LaunchDelayMilliseconds);
        Assert.Equal(ForeignWindowSelectionMode.ClassName, result.Rule.WindowSelection.Mode);
    }

    [Fact]
    public void Equal_specificity_ties_keep_file_order()
    {
        var database = ForeignAppQuirksDatabase.Load(FindMeasuredSeed());

        var result = database.Select("ReparentSpike.exe", "WinMuxGuineaPig");

        Assert.False(result.IsDefault);
        Assert.Equal(ForeignAppDpiAwareness.Unaware, result.Rule.DpiAwareness);
        Assert.Equal(403, result.Rule.ObservedFindMilliseconds);
    }

    [Fact]
    public void Unknown_application_gets_an_explicit_unverified_embed_default()
    {
        var database = ForeignAppQuirksDatabase.Load(FindMeasuredSeed());

        var result = database.Select(@"D:\Unknown\NovelApp.exe", "NovelWindow");

        Assert.True(result.IsDefault);
        Assert.Equal(HostStrategy.Embed, result.Rule.Strategy);
        Assert.Equal(ForeignWindowSelectionMode.ClassName, result.Rule.WindowSelection.Mode);
        Assert.Equal("NovelWindow", result.Rule.WindowSelection.ClassName);
        Assert.Equal(ForeignAppQuirksDatabase.DefaultLaunchDelayMilliseconds, result.Rule.LaunchDelayMilliseconds);
        Assert.Equal(ForeignAppDpiAwareness.Unknown, result.Rule.DpiAwareness);
        Assert.Contains("unverified default", result.Rule.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Rule.Verification);
    }

    [Fact]
    public void Mismatched_known_window_class_does_not_apply_an_executable_rule_for_another_window()
    {
        var database = ForeignAppQuirksDatabase.Load(FindMeasuredSeed());

        var result = database.Select("Taskmgr.exe", "SomeOtherWindow");

        Assert.True(result.IsDefault);
        Assert.Equal(HostStrategy.Embed, result.Rule.Strategy);
    }

    [Fact]
    public void Default_window_selection_uses_the_best_identity_available()
    {
        var database = ForeignAppQuirksDatabase.Parse(ValidDatabase);

        var process = database.Select(@"D:\Apps\unknown.exe");
        var windowClass = database.Select(null, "UnknownWindow");
        var pid = database.Select(null, null);

        Assert.True(process.IsDefault);
        Assert.Equal(ForeignWindowSelectionMode.ProcessName, process.Rule.WindowSelection.Mode);
        Assert.Equal("unknown.exe", process.Rule.WindowSelection.ProcessName);
        Assert.Equal(ForeignWindowSelectionMode.ClassName, windowClass.Rule.WindowSelection.Mode);
        Assert.Equal("UnknownWindow", windowClass.Rule.WindowSelection.ClassName);
        Assert.Equal(ForeignWindowSelectionMode.Pid, pid.Rule.WindowSelection.Mode);
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Invalid_schema_is_rejected_with_a_specific_location(string json, string expectedMessage)
    {
        var exception = Assert.Throws<ForeignAppQuirksFormatException>(
            () => ForeignAppQuirksDatabase.Parse(json));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InvalidDocuments => new()
    {
        { ValidDatabase.Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal), "Unsupported foreign-app quirks version 2" },
        { ValidDatabase.Replace("\"strategy\": \"embed\"", "\"strategy\": \"hope\"", StringComparison.Ordinal), "entries[0].strategy" },
        { ValidDatabase.Replace("\"launchDelayMs\": 100", "\"launchDelayMs\": -1", StringComparison.Ordinal), "entries[0].launchDelayMs cannot be negative" },
        { ValidDatabase.Replace("\"date\": \"2026-09-10\"", "\"date\": \"September 10\"", StringComparison.Ordinal), "entries[0].verified.date" },
        { ValidDatabase.Replace("\"processName\": \"tool.exe\"", "\"processName\": \"\"", StringComparison.Ordinal), "entries[0].windowSelection.processName" },
        { ValidDatabase.Replace("\"detachExact\": true", "\"detachExact\": null", StringComparison.Ordinal), "entries[0].verified.detachExact" },
        { ValidDatabase.Replace("\"generatedBy\": \"test\"", "\"generatedBy\": \"test\", \"surprise\": true", StringComparison.Ordinal), "not valid version-1 JSON" },
    };

    private static string FindMeasuredSeed()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "spikes", "02-reparent", "quirks-seed.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("Could not locate spikes/02-reparent/quirks-seed.json from the test output directory.");
    }

    private const string ValidDatabase = """
        {
          "version": 1,
          "generatedBy": "test",
          "entries": [
            {
              "match": { "exe": "tool.exe", "windowClass": "" },
              "strategy": "embed",
              "windowSelection": { "mode": "ProcessName", "processName": "tool.exe", "className": "" },
              "launchDelayMs": 100,
              "observedFindMs": 50,
              "dpiAwareness": "PerMonitor",
              "limitations": null,
              "verified": {
                "date": "2026-09-10",
                "os": "10.0.0",
                "result": "OK",
                "resizeFollows": true,
                "moveFollows": true,
                "detachExact": true
              }
            }
          ]
        }
        """;

    private const string SpecificityDatabase = """
        {
          "version": 1,
          "generatedBy": "test",
          "entries": [
            {
              "match": { "exe": "tool.exe", "windowClass": "" },
              "strategy": "embed",
              "windowSelection": { "mode": "ProcessName", "processName": "tool.exe", "className": "" },
              "launchDelayMs": 111,
              "observedFindMs": 10,
              "dpiAwareness": "PerMonitor",
              "limitations": null,
              "verified": { "date": "2026-09-10", "os": "test", "result": "OK", "resizeFollows": true, "moveFollows": true, "detachExact": true }
            },
            {
              "match": { "exe": "", "windowClass": "MainWindow" },
              "strategy": "attach",
              "windowSelection": { "mode": "ClassName", "processName": "", "className": "MainWindow" },
              "launchDelayMs": 222,
              "observedFindMs": 20,
              "dpiAwareness": "System",
              "limitations": "class only",
              "verified": { "date": "2026-09-10", "os": "test", "result": "OK", "resizeFollows": true, "moveFollows": true, "detachExact": true }
            },
            {
              "match": { "exe": "tool.exe", "windowClass": "MainWindow" },
              "strategy": "embed",
              "windowSelection": { "mode": "ClassName", "processName": "", "className": "MainWindow" },
              "launchDelayMs": 333,
              "observedFindMs": 30,
              "dpiAwareness": "Unaware",
              "limitations": null,
              "verified": { "date": "2026-09-10", "os": "test", "result": "OK", "resizeFollows": true, "moveFollows": true, "detachExact": true }
            }
          ]
        }
        """;
}
