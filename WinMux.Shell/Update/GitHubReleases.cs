using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WinMux.Core.Update;

namespace WinMux.Shell.Update;

/// <summary>
/// Reading published releases from GitHub.
///
/// The transport half of the updater; every judgement about what a release *means* lives in
/// <see cref="UpdateCheck"/> in Core, where it can be tested without a network.
///
/// Unauthenticated, which is deliberate: WinMux has no business holding a token to read a public
/// repository's releases. That caps the caller at sixty requests an hour per IP, which is why the
/// shell checks on a timer measured in hours rather than minutes.
/// </summary>
internal sealed class GitHubReleases(HttpClient http, string owner, string repository)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly string _owner = owner;
    private readonly string _repository = repository;

    /// <summary>How many releases back to look. More than enough to find the newest stable one.</summary>
    private const int PageSize = 20;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // A check that hangs must not keep a dialog open or a timer blocked.
            Timeout = TimeSpan.FromSeconds(20),
        };

        // GitHub rejects requests with no user agent outright.
        var version = typeof(GitHubReleases).Assembly.GetName().Version?.ToString(3) ?? "0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinMux", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>
    /// Every recent release, newest first. Throws nothing the caller has to catch beyond the usual
    /// network exceptions — a failed check is a message, not an error state.
    /// </summary>
    public async Task<IReadOnlyList<Release>> ListAsync(CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{_owner}/{_repository}/releases?per_page={PageSize}";
        var payload = await _http.GetFromJsonAsync(url, GitHubJson.Default.GitHubReleaseArray, cancellationToken)
                      ?? [];

        var releases = new List<Release>(payload.Length);
        foreach (var item in payload)
        {
            if (item.Draft) continue;

            // A tag that is not a version is somebody else's tag, not a release of ours.
            if (!ReleaseVersion.TryParse(item.TagName, out var version)) continue;

            var assets = (item.Assets ?? [])
                .Select(asset => new ReleaseAsset(asset.Name ?? "", asset.BrowserDownloadUrl ?? "", asset.Size))
                .Where(asset => asset.Name.Length > 0 && asset.Url.Length > 0)
                .ToArray();

            releases.Add(new Release(version, item.Prerelease, item.Body ?? "", item.HtmlUrl ?? "", assets));
        }

        return releases;
    }

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken = default) =>
        await _http.GetStringAsync(url, cancellationToken);
}

// The JSON shapes, named as GitHub names them. Source-generated so the updater keeps working if
// the app is ever published trimmed.
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("assets")] public GitHubAsset[]? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
}

[JsonSerializable(typeof(GitHubRelease[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class GitHubJson : JsonSerializerContext;
