using System.Globalization;

namespace WinMux.Core.Update;

/// <summary>
/// A release version, as it appears on a git tag and in the assembly.
///
/// Not <see cref="System.Version"/>, for one reason that matters: a prerelease suffix.
/// <c>v0.7.0-beta.1</c> has to sort *below* <c>v0.7.0</c>, and `System.Version` cannot express the
/// suffix at all, so an updater built on it would happily offer a beta as an upgrade from the
/// stable release it precedes.
///
/// Only as much SemVer as this project uses: three numbers and an optional dash-suffix. Build
/// metadata after "+" is ignored for ordering, as SemVer requires.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string Prerelease)
    : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();

        // Tags are conventionally "v0.7.0"; assembly versions are not. Accept both.
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        // Build metadata never affects precedence.
        var plus = value.IndexOf('+');
        if (plus >= 0) value = value[..plus];

        var prerelease = "";
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0) return false;
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 4) return false;

        if (!TryNumber(parts[0], out var major) || !TryNumber(parts[1], out var minor)) return false;

        var patch = 0;
        if (parts.Length >= 3 && !TryNumber(parts[2], out patch)) return false;

        // A fourth component is what .NET assembly versions carry (0.6.0.0). Accepted and dropped:
        // releases are named by three.
        if (parts.Length == 4 && !TryNumber(parts[3], out _)) return false;

        version = new ReleaseVersion(major, minor, patch, prerelease);
        return true;
    }

    public static ReleaseVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a version: '{text}'.");

    public int CompareTo(ReleaseVersion other)
    {
        var byNumber = Major.CompareTo(other.Major);
        if (byNumber != 0) return byNumber;

        byNumber = Minor.CompareTo(other.Minor);
        if (byNumber != 0) return byNumber;

        byNumber = Patch.CompareTo(other.Patch);
        if (byNumber != 0) return byNumber;

        // SemVer §11: a version with a prerelease suffix precedes the one without.
        if (IsPrerelease && !other.IsPrerelease) return -1;
        if (!IsPrerelease && other.IsPrerelease) return 1;

        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPrerelease
            ? $"{Major}.{Minor}.{Patch}-{Prerelease}"
            : $"{Major}.{Minor}.{Patch}";

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
