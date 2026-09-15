using WinMux.Core.Update;

namespace WinMux.Tests;

/// <summary>
/// Version ordering, which an updater gets wrong silently or not at all.
/// </summary>
public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("0.6.0", 0, 6, 0, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("0.6.0.0", 0, 6, 0, "")]          // what an assembly version looks like
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    [InlineData("v2.0.0-beta", 2, 0, 0, "beta")]
    [InlineData("1.2.3+build.9", 1, 2, 3, "")]    // build metadata never affects precedence
    public void A_version_parses(string text, int major, int minor, int patch, string prerelease)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    [InlineData("1.2.3-")]
    public void Anything_else_does_not(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void Versions_order_by_number()
    {
        Assert.True(ReleaseVersion.Parse("0.7.0") > ReleaseVersion.Parse("0.6.9"));
        Assert.True(ReleaseVersion.Parse("1.0.0") > ReleaseVersion.Parse("0.99.99"));
        Assert.True(ReleaseVersion.Parse("0.6.10") > ReleaseVersion.Parse("0.6.9"));
    }

    [Fact]
    public void A_prerelease_precedes_the_release_it_leads_to()
    {
        // The whole reason this is not System.Version: 1.0.0-rc.1 must not read as newer than
        // 1.0.0, or every release candidate would be offered as an upgrade from the final build.
        Assert.True(ReleaseVersion.Parse("1.0.0-rc.1") < ReleaseVersion.Parse("1.0.0"));
        Assert.True(ReleaseVersion.Parse("1.0.0-rc.1") > ReleaseVersion.Parse("0.9.9"));
    }

    [Fact]
    public void Prereleases_order_among_themselves()
    {
        Assert.True(ReleaseVersion.Parse("1.0.0-alpha") < ReleaseVersion.Parse("1.0.0-beta"));
    }

    [Fact]
    public void A_version_round_trips_through_its_own_text()
    {
        foreach (var text in new[] { "0.6.0", "1.2.3", "1.0.0-rc.1" })
            Assert.Equal(text, ReleaseVersion.Parse(text).ToString());
    }
}

/// <summary>
/// Deciding whether a published release is an update.
///
/// The failure modes here are all quiet: offering a downgrade, installing a beta over a stable
/// build, or reporting "up to date" forever while a fix sits published.
/// </summary>
public sealed class UpdateCheckTests
{
    private static ReleaseAsset Package(string name = "WinMux-0.7.0-win-x64.zip") =>
        new(name, "https://example.invalid/" + name, 1024);

    private static ReleaseAsset Checksums() =>
        new("checksums.txt", "https://example.invalid/checksums.txt", 128);

    private static Release Release(string tag, bool prerelease = false, params ReleaseAsset[] assets) =>
        new(ReleaseVersion.Parse(tag), prerelease, "notes", "https://example.invalid/" + tag,
            assets.Length > 0 ? assets : [Package(), Checksums()]);

    [Fact]
    public void A_newer_release_is_an_update()
    {
        var decision = UpdateCheck.Decide([Release("v0.7.0")], ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(UpdateOutcome.UpdateAvailable, decision.Outcome);
        Assert.Equal(ReleaseVersion.Parse("0.7.0"), decision.Release!.Version);
        Assert.Equal("WinMux-0.7.0-win-x64.zip", decision.Package!.Value.Name);
        Assert.NotNull(decision.Checksums);
    }

    [Fact]
    public void The_same_version_is_not_an_update()
    {
        Assert.Equal(
            UpdateOutcome.UpToDate,
            UpdateCheck.Decide([Release("v0.6.0")], ReleaseVersion.Parse("0.6.0")).Outcome);
    }

    [Fact]
    public void An_older_release_is_never_offered()
    {
        // Running a build newer than anything published — a developer, or someone who has already
        // updated — must not be walked backwards.
        Assert.Equal(
            UpdateOutcome.UpToDate,
            UpdateCheck.Decide([Release("v0.5.0")], ReleaseVersion.Parse("0.6.0")).Outcome);
    }

    [Fact]
    public void The_newest_release_wins_regardless_of_order()
    {
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0"), Release("v0.9.0"), Release("v0.8.0")],
            ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(ReleaseVersion.Parse("0.9.0"), decision.Release!.Version);
    }

    [Fact]
    public void A_prerelease_is_invisible_on_the_stable_channel()
    {
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0-beta.1", prerelease: true)],
            ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(UpdateOutcome.UpToDate, decision.Outcome);
    }

    [Fact]
    public void A_prerelease_tag_counts_even_when_the_forge_does_not_say_so()
    {
        // Ticking "prerelease" on the release page is easy to forget; the tag is the honest signal.
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0-rc.1", prerelease: false)],
            ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(UpdateOutcome.UpToDate, decision.Outcome);
    }

    [Fact]
    public void A_prerelease_is_offered_on_the_prerelease_channel()
    {
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0-beta.1", prerelease: true)],
            ReleaseVersion.Parse("0.6.0"),
            UpdateChannel.Prerelease);

        Assert.Equal(UpdateOutcome.UpdateAvailable, decision.Outcome);
    }

    [Fact]
    public void A_stable_release_still_wins_over_a_newer_prerelease_of_the_same_version()
    {
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0"), Release("v0.7.0-rc.2", prerelease: true)],
            ReleaseVersion.Parse("0.6.0"),
            UpdateChannel.Prerelease);

        Assert.Equal(ReleaseVersion.Parse("0.7.0"), decision.Release!.Version);
    }

    [Fact]
    public void A_release_with_no_build_for_this_platform_says_so()
    {
        // Distinct from "up to date": an update exists and the user should know why they cannot
        // have it, rather than being told everything is fine.
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0", false, Package("WinMux-0.7.0-linux-x64.tar.gz"), Checksums())],
            ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(UpdateOutcome.UpdateNotInstallable, decision.Outcome);
        Assert.NotNull(decision.Release);
    }

    [Fact]
    public void A_release_with_no_checksums_is_still_offered_but_carries_none()
    {
        // The installer is what refuses to install it; the check does not hide that it exists.
        var decision = UpdateCheck.Decide(
            [Release("v0.7.0", false, Package())],
            ReleaseVersion.Parse("0.6.0"));

        Assert.Equal(UpdateOutcome.UpdateAvailable, decision.Outcome);
        Assert.Null(decision.Checksums);
    }

    [Fact]
    public void No_releases_at_all_is_up_to_date()
    {
        Assert.Equal(UpdateOutcome.UpToDate, UpdateCheck.Decide([], ReleaseVersion.Parse("0.6.0")).Outcome);
    }
}

/// <summary>Reading a sha256sum manifest, which is what stands between a download and trust.</summary>
public sealed class ChecksumManifestTests
{
    private const string Manifest = """
        # WinMux 0.7.0
        aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff0011223344  WinMux-0.7.0-linux-x64.tar.gz
        1111111111111111111111111111111111111111111111111111111111111111  WinMux-0.7.0-win-x64.zip
        """;

    [Fact]
    public void The_hash_for_a_named_file_is_found()
    {
        Assert.Equal(
            "1111111111111111111111111111111111111111111111111111111111111111",
            UpdateCheck.FindChecksum(Manifest, "WinMux-0.7.0-win-x64.zip"));
    }

    [Fact]
    public void A_file_that_is_not_listed_has_no_hash()
    {
        Assert.Null(UpdateCheck.FindChecksum(Manifest, "WinMux-0.8.0-win-x64.zip"));
    }

    [Fact]
    public void Binary_mode_asterisks_are_tolerated()
    {
        var manifest = "2222222222222222222222222222222222222222222222222222222222222222 *thing.zip";

        Assert.Equal(
            "2222222222222222222222222222222222222222222222222222222222222222",
            UpdateCheck.FindChecksum(manifest, "thing.zip"));
    }

    [Fact]
    public void A_hash_that_is_not_a_sha256_is_refused()
    {
        // Half a hash is not a hash, and accepting it would mean verifying against nothing.
        Assert.Null(UpdateCheck.FindChecksum("abc123  thing.zip", "thing.zip"));
    }

    [Fact]
    public void An_empty_manifest_yields_nothing()
    {
        Assert.Null(UpdateCheck.FindChecksum("", "thing.zip"));
        Assert.Null(UpdateCheck.FindChecksum(null, "thing.zip"));
    }
}
