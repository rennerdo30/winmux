namespace WinMux.Core.Update;

/// <summary>Which releases a user is willing to be offered.</summary>
public enum UpdateChannel
{
    /// <summary>Tagged releases with no prerelease suffix. The default.</summary>
    Stable,

    /// <summary>Everything, prereleases included.</summary>
    Prerelease,
}

/// <summary>One downloadable file attached to a release.</summary>
public readonly record struct ReleaseAsset(string Name, string Url, long Size);

/// <summary>A published release, as much of one as an updater needs.</summary>
/// <param name="Version">Parsed from the tag.</param>
/// <param name="IsPrerelease">What the forge says, which may disagree with the tag; both are honoured.</param>
/// <param name="Notes">Release notes, shown before anything is downloaded.</param>
/// <param name="Url">The human page, for "what changed?".</param>
/// <param name="Assets">The files published with it.</param>
public sealed record Release(
    ReleaseVersion Version,
    bool IsPrerelease,
    string Notes,
    string Url,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>What an update check concluded.</summary>
public enum UpdateOutcome
{
    /// <summary>Running the newest release the channel allows.</summary>
    UpToDate,

    /// <summary>A newer release exists and can be installed.</summary>
    UpdateAvailable,

    /// <summary>A newer release exists but ships nothing this machine can install.</summary>
    UpdateNotInstallable,
}

/// <param name="Outcome">What to do about it.</param>
/// <param name="Release">The release in question, or null when up to date.</param>
/// <param name="Package">The asset to download, when there is one.</param>
/// <param name="Checksums">The checksum manifest, when the release published one.</param>
public sealed record UpdateDecision(
    UpdateOutcome Outcome,
    Release? Release = null,
    ReleaseAsset? Package = null,
    ReleaseAsset? Checksums = null)
{
    public static readonly UpdateDecision UpToDate = new(UpdateOutcome.UpToDate);
}

/// <summary>
/// Deciding whether a published release is an update, and which file to fetch.
///
/// Pure, and in Core, because it is the part worth testing and the part most likely to be quietly
/// wrong. An updater that misjudges this does not throw — it offers a downgrade, or silently
/// installs a beta over a stable release, or does nothing forever while a fix sits published. None
/// of those look like failures from the outside.
/// </summary>
public static class UpdateCheck
{
    /// <summary>The checksum manifest a release is expected to publish.</summary>
    public const string ChecksumsFileName = "checksums.txt";

    /// <summary>
    /// Decide what <paramref name="releases"/> means for someone running <paramref name="current"/>.
    /// </summary>
    /// <param name="releases">Published releases, in any order.</param>
    /// <param name="current">The running version.</param>
    /// <param name="channel">Which releases the user accepts.</param>
    /// <param name="packageSuffix">
    /// How this platform's archive is named — <c>-win-x64.zip</c>. A release carrying only assets
    /// for other platforms is an update that cannot be installed, which is a different answer from
    /// "up to date" and must not be reported as one.
    /// </param>
    public static UpdateDecision Decide(
        IEnumerable<Release> releases,
        ReleaseVersion current,
        UpdateChannel channel = UpdateChannel.Stable,
        string packageSuffix = "-win-x64.zip")
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSuffix);

        var candidate = releases
            .Where(release => release is not null)
            .Where(release => channel == UpdateChannel.Prerelease || !IsPrerelease(release))
            .Where(release => release.Version > current)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();

        if (candidate is null) return UpdateDecision.UpToDate;

        var package = candidate.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase));

        // Name is empty for `default`, which is what FirstOrDefault gives for a struct.
        if (string.IsNullOrEmpty(package.Name))
            return new UpdateDecision(UpdateOutcome.UpdateNotInstallable, candidate);

        var checksums = candidate.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, ChecksumsFileName, StringComparison.OrdinalIgnoreCase));

        return new UpdateDecision(
            UpdateOutcome.UpdateAvailable,
            candidate,
            package,
            string.IsNullOrEmpty(checksums.Name) ? null : checksums);
    }

    /// <summary>
    /// A release counts as prerelease if either the forge says so or its tag carries a suffix.
    ///
    /// Either alone is routinely wrong: a tag can be `v1.0.0-rc.1` while the release was published
    /// without ticking the box, and a stable tag can be marked prerelease by hand.
    /// </summary>
    public static bool IsPrerelease(Release release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return release.IsPrerelease || release.Version.IsPrerelease;
    }

    /// <summary>
    /// Find a file's expected hash in a <c>sha256sum</c>-style manifest.
    ///
    /// Lines are "&lt;hex&gt;  &lt;name&gt;". Anything unparseable is skipped rather than fatal:
    /// a manifest listing files for several platforms is normal, and one malformed line should not
    /// stop a valid entry being found.
    /// </summary>
    public static string? FindChecksum(string? manifest, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (string.IsNullOrWhiteSpace(manifest)) return null;

        foreach (var line in manifest.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // The name may carry a leading "*" for binary mode, which sha256sum writes.
            var name = parts[^1].TrimStart('*');
            if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;

            var hash = parts[0].Trim();
            return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
        }

        return null;
    }
}
