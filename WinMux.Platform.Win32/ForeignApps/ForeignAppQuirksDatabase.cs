using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinMux.Core.Model;

namespace WinMux.Platform.Win32.ForeignApps;

/// <summary>How the pane host should locate the application's adoptable top-level window.</summary>
public enum ForeignWindowSelectionMode
{
    Pid,
    ProcessName,
    ClassName,
}

/// <summary>The DPI-awareness value measured by the compatibility spike.</summary>
public enum ForeignAppDpiAwareness
{
    Unknown,
    Unaware,
    System,
    PerMonitor,
}

/// <summary>Executable image and window-class criteria for one compatibility rule.</summary>
public sealed record ForeignAppQuirkMatch(string? ExecutableImage, string? WindowClass);

/// <summary>Configuration used to find the application's real top-level window.</summary>
public sealed record ForeignWindowSelection(
    ForeignWindowSelectionMode Mode,
    string? ProcessName,
    string? ClassName);

/// <summary>The exact environment and result recorded by the compatibility spike.</summary>
public sealed record ForeignAppVerification(
    DateOnly Date,
    string OperatingSystem,
    string Result,
    bool ResizeFollows,
    bool MoveFollows,
    bool DetachExact);

/// <summary>A validated, platform-API-free rule from the measured Win32 compatibility seed.</summary>
public sealed record ForeignAppQuirkRule(
    ForeignAppQuirkMatch Match,
    HostStrategy Strategy,
    ForeignWindowSelection WindowSelection,
    int LaunchDelayMilliseconds,
    int ObservedFindMilliseconds,
    ForeignAppDpiAwareness DpiAwareness,
    string? Limitations,
    ForeignAppVerification? Verification);

/// <summary>
/// The selected compatibility rule. Defaults are explicit so an unmeasured application is never
/// confused with an application verified by the spike.
/// </summary>
public sealed record ForeignAppQuirkSelection(ForeignAppQuirkRule Rule, bool IsDefault);

/// <summary>Thrown when a quirks file does not satisfy the measured version-1 seed schema.</summary>
public sealed class ForeignAppQuirksFormatException : Exception
{
    public ForeignAppQuirksFormatException(string message)
        : base(message)
    {
    }

    public ForeignAppQuirksFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Loads, validates, and selects rules from the measured foreign-application compatibility seed.
/// Matching is case-insensitive because Windows executable images and window classes are treated
/// case-insensitively by this compatibility layer.
/// </summary>
public sealed class ForeignAppQuirksDatabase
{
    public const int CurrentVersion = 1;
    public const int DefaultLaunchDelayMilliseconds = 1_500;

    private const string DefaultLimitation =
        "No measured compatibility rule matches this application. Embed is an unverified default; " +
        "the application may refuse embedding and require attach mode.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private ForeignAppQuirksDatabase(
        int version,
        string generatedBy,
        IReadOnlyList<ForeignAppQuirkRule> entries)
    {
        Version = version;
        GeneratedBy = generatedBy;
        Entries = entries;
    }

    public int Version { get; }

    public string GeneratedBy { get; }

    public IReadOnlyList<ForeignAppQuirkRule> Entries { get; }

    /// <summary>Loads and validates a quirks database from disk.</summary>
    public static ForeignAppQuirksDatabase Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates a version-1 quirks database.</summary>
    public static ForeignAppQuirksDatabase Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        DatabaseDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DatabaseDocument>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ForeignAppQuirksFormatException(
                "The foreign-app quirks database is not valid version-1 JSON: " + exception.Message,
                exception);
        }

        if (document is null)
        {
            throw new ForeignAppQuirksFormatException("The foreign-app quirks database is empty.");
        }

        if (document.Version != CurrentVersion)
        {
            throw new ForeignAppQuirksFormatException(
                $"Unsupported foreign-app quirks version {document.Version?.ToString(CultureInfo.InvariantCulture) ?? "(missing)"}; " +
                $"this build understands version {CurrentVersion}.");
        }

        var generatedBy = Required(document.GeneratedBy, "generatedBy");
        if (document.Entries is null)
        {
            throw new ForeignAppQuirksFormatException("The foreign-app quirks database has no entries array.");
        }

        var entries = document.Entries
            .Select((entry, index) => ValidateEntry(entry, index))
            .ToArray();
        return new ForeignAppQuirksDatabase(CurrentVersion, generatedBy, entries);
    }

    /// <summary>
    /// Selects the most specific compatible rule. An executable-and-class match beats either
    /// single criterion; equal-specificity ties retain file order, including the seed's two
    /// measurements of the same guinea-pig window under different DPI awareness modes.
    /// </summary>
    public ForeignAppQuirkSelection Select(string? executableImage, string? windowClass = null)
    {
        var image = NormalizeExecutableImage(executableImage);
        var window = NullIfWhiteSpace(windowClass);
        ForeignAppQuirkRule? best = null;
        var bestSpecificity = -1;

        foreach (var entry in Entries)
        {
            var specificity = MatchSpecificity(entry.Match, image, window);
            if (specificity > bestSpecificity)
            {
                best = entry;
                bestSpecificity = specificity;
            }
        }

        return best is null
            ? new ForeignAppQuirkSelection(CreateDefault(image, window), IsDefault: true)
            : new ForeignAppQuirkSelection(best, IsDefault: false);
    }

    private static ForeignAppQuirkRule ValidateEntry(EntryDocument? entry, int index)
    {
        var location = $"entries[{index}]";
        if (entry is null)
        {
            throw new ForeignAppQuirksFormatException($"{location} is null.");
        }

        if (entry.Match is null)
        {
            throw new ForeignAppQuirksFormatException($"{location}.match is required.");
        }

        var executable = NullIfWhiteSpace(entry.Match.ExecutableImage);
        var windowClass = NullIfWhiteSpace(entry.Match.WindowClass);
        if (executable is null && windowClass is null)
        {
            throw new ForeignAppQuirksFormatException(
                $"{location}.match must specify exe, windowClass, or both.");
        }

        var strategy = entry.Strategy switch
        {
            "embed" => HostStrategy.Embed,
            "attach" => HostStrategy.Attach,
            _ => throw new ForeignAppQuirksFormatException(
                $"{location}.strategy must be 'embed' or 'attach'."),
        };

        if (entry.WindowSelection is null)
        {
            throw new ForeignAppQuirksFormatException($"{location}.windowSelection is required.");
        }

        var selectionMode = entry.WindowSelection.Mode switch
        {
            "Pid" => ForeignWindowSelectionMode.Pid,
            "ProcessName" => ForeignWindowSelectionMode.ProcessName,
            "ClassName" => ForeignWindowSelectionMode.ClassName,
            _ => throw new ForeignAppQuirksFormatException(
                $"{location}.windowSelection.mode must be Pid, ProcessName, or ClassName."),
        };
        var processName = NullIfWhiteSpace(entry.WindowSelection.ProcessName);
        var selectionClass = NullIfWhiteSpace(entry.WindowSelection.ClassName);
        if (selectionMode == ForeignWindowSelectionMode.ProcessName && processName is null)
        {
            throw new ForeignAppQuirksFormatException(
                $"{location}.windowSelection.processName is required for ProcessName mode.");
        }
        if (selectionMode == ForeignWindowSelectionMode.ClassName && selectionClass is null)
        {
            throw new ForeignAppQuirksFormatException(
                $"{location}.windowSelection.className is required for ClassName mode.");
        }

        var launchDelay = RequiredNonNegative(entry.LaunchDelayMilliseconds, $"{location}.launchDelayMs");
        var observedFind = RequiredNonNegative(entry.ObservedFindMilliseconds, $"{location}.observedFindMs");
        var dpiAwareness = entry.DpiAwareness switch
        {
            "Unaware" => ForeignAppDpiAwareness.Unaware,
            "System" => ForeignAppDpiAwareness.System,
            "PerMonitor" => ForeignAppDpiAwareness.PerMonitor,
            _ => throw new ForeignAppQuirksFormatException(
                $"{location}.dpiAwareness must be Unaware, System, or PerMonitor."),
        };

        if (entry.Verified is null)
        {
            throw new ForeignAppQuirksFormatException($"{location}.verified is required.");
        }

        if (!DateOnly.TryParseExact(
                entry.Verified.Date,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var verifiedDate))
        {
            throw new ForeignAppQuirksFormatException(
                $"{location}.verified.date must use yyyy-MM-dd.");
        }

        var verification = new ForeignAppVerification(
            verifiedDate,
            Required(entry.Verified.OperatingSystem, $"{location}.verified.os"),
            Required(entry.Verified.Result, $"{location}.verified.result"),
            Required(entry.Verified.ResizeFollows, $"{location}.verified.resizeFollows"),
            Required(entry.Verified.MoveFollows, $"{location}.verified.moveFollows"),
            Required(entry.Verified.DetachExact, $"{location}.verified.detachExact"));

        return new ForeignAppQuirkRule(
            new ForeignAppQuirkMatch(executable, windowClass),
            strategy,
            new ForeignWindowSelection(selectionMode, processName, selectionClass),
            launchDelay,
            observedFind,
            dpiAwareness,
            NullIfWhiteSpace(entry.Limitations),
            verification);
    }

    private static int MatchSpecificity(
        ForeignAppQuirkMatch match,
        string? executableImage,
        string? windowClass)
    {
        var specificity = 0;
        if (executableImage is not null && match.ExecutableImage is not null)
        {
            if (!string.Equals(executableImage, match.ExecutableImage, StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
            specificity += 2;
        }
        else if (executableImage is null && windowClass is null)
        {
            return -1;
        }

        if (windowClass is not null && match.WindowClass is not null)
        {
            if (!string.Equals(windowClass, match.WindowClass, StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }
            specificity++;
        }

        return specificity == 0 ? -1 : specificity;
    }

    private static ForeignAppQuirkRule CreateDefault(string? executableImage, string? windowClass)
    {
        var selection = windowClass is not null
            ? new ForeignWindowSelection(ForeignWindowSelectionMode.ClassName, null, windowClass)
            : executableImage is not null
                ? new ForeignWindowSelection(ForeignWindowSelectionMode.ProcessName, executableImage, null)
                : new ForeignWindowSelection(ForeignWindowSelectionMode.Pid, null, null);

        return new ForeignAppQuirkRule(
            new ForeignAppQuirkMatch(executableImage, windowClass),
            HostStrategy.Embed,
            selection,
            DefaultLaunchDelayMilliseconds,
            ObservedFindMilliseconds: 0,
            ForeignAppDpiAwareness.Unknown,
            DefaultLimitation,
            Verification: null);
    }

    private static string? NormalizeExecutableImage(string? value)
    {
        value = NullIfWhiteSpace(value);
        if (value is null)
        {
            return null;
        }

        var separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private static string Required(string? value, string location) =>
        NullIfWhiteSpace(value)
        ?? throw new ForeignAppQuirksFormatException($"{location} is required.");

    private static bool Required(bool? value, string location) =>
        value ?? throw new ForeignAppQuirksFormatException($"{location} is required.");

    private static int RequiredNonNegative(int? value, string location)
    {
        if (value is null)
        {
            throw new ForeignAppQuirksFormatException($"{location} is required.");
        }
        if (value < 0)
        {
            throw new ForeignAppQuirksFormatException($"{location} cannot be negative.");
        }
        return value.Value;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class DatabaseDocument
    {
        [JsonPropertyName("version")]
        public int? Version { get; init; }

        [JsonPropertyName("generatedBy")]
        public string? GeneratedBy { get; init; }

        [JsonPropertyName("entries")]
        public EntryDocument?[]? Entries { get; init; }
    }

    private sealed class EntryDocument
    {
        [JsonPropertyName("match")]
        public MatchDocument? Match { get; init; }

        [JsonPropertyName("strategy")]
        public string? Strategy { get; init; }

        [JsonPropertyName("windowSelection")]
        public WindowSelectionDocument? WindowSelection { get; init; }

        [JsonPropertyName("launchDelayMs")]
        public int? LaunchDelayMilliseconds { get; init; }

        [JsonPropertyName("observedFindMs")]
        public int? ObservedFindMilliseconds { get; init; }

        [JsonPropertyName("dpiAwareness")]
        public string? DpiAwareness { get; init; }

        [JsonPropertyName("limitations")]
        public string? Limitations { get; init; }

        [JsonPropertyName("verified")]
        public VerificationDocument? Verified { get; init; }
    }

    private sealed class MatchDocument
    {
        [JsonPropertyName("exe")]
        public string? ExecutableImage { get; init; }

        [JsonPropertyName("windowClass")]
        public string? WindowClass { get; init; }
    }

    private sealed class WindowSelectionDocument
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; init; }

        [JsonPropertyName("processName")]
        public string? ProcessName { get; init; }

        [JsonPropertyName("className")]
        public string? ClassName { get; init; }
    }

    private sealed class VerificationDocument
    {
        [JsonPropertyName("date")]
        public string? Date { get; init; }

        [JsonPropertyName("os")]
        public string? OperatingSystem { get; init; }

        [JsonPropertyName("result")]
        public string? Result { get; init; }

        [JsonPropertyName("resizeFollows")]
        public bool? ResizeFollows { get; init; }

        [JsonPropertyName("moveFollows")]
        public bool? MoveFollows { get; init; }

        [JsonPropertyName("detachExact")]
        public bool? DetachExact { get; init; }
    }
}
