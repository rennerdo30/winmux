using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// POSIX path arithmetic for remote filesystems.
///
/// Worth testing properly because it is the part of SFTP and FTP support that can be tested without
/// a server, and because the failure mode is quiet: <c>System.IO.Path</c> would answer every one of
/// these questions with a plausible Windows-shaped string, which reaches the server and creates a
/// directory with a backslash in its name.
/// </summary>
public sealed class RemotePathTests
{
    [Theory]
    [InlineData("/srv/www", "/srv/www")]
    [InlineData("srv/www", "/srv/www")]              // relative is anchored, not left dangling
    [InlineData("/srv/www/", "/srv/www")]            // no trailing separator
    [InlineData("//srv///www", "/srv/www")]          // collapsed
    [InlineData("/srv/./www", "/srv/www")]
    [InlineData("/srv/data/../www", "/srv/www")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    public void Paths_normalise_to_an_absolute_posix_form(string input, string expected) =>
        Assert.Equal(expected, RemotePath.Normalize(input));

    [Fact]
    public void A_windows_separator_is_treated_as_a_separator_not_a_name()
    {
        // Someone will paste a Windows path in. Turning it into one remote segment called
        // "srv\www" would create a directory with a backslash in its name on a Unix server.
        Assert.Equal("/srv/www", RemotePath.Normalize(@"\srv\www"));
    }

    [Fact]
    public void Walking_above_the_root_stays_at_the_root()
    {
        // As `cd ..` in `/` does. The alternative is a path like "/.." reaching the server.
        Assert.Equal("/", RemotePath.Normalize("/.."));
        Assert.Equal("/", RemotePath.Normalize("/srv/../.."));
    }

    [Theory]
    [InlineData("/srv", "www", "/srv/www")]
    [InlineData("/", "srv", "/srv")]                 // not "//srv"
    [InlineData("/srv/", "www", "/srv/www")]
    [InlineData("/srv", "/www/", "/srv/www")]
    public void Combining_uses_a_forward_slash(string directory, string name, string expected) =>
        Assert.Equal(expected, RemotePath.Combine(directory, name));

    [Fact]
    public void Combining_never_produces_a_backslash()
    {
        // The specific thing Path.Combine would do on Windows, and the reason this type exists.
        Assert.DoesNotContain('\\', RemotePath.Combine("/srv", "www"));
    }

    [Theory]
    [InlineData("/srv/www/logs", "/srv/www")]
    [InlineData("/srv", "/")]
    [InlineData("/", null)]
    public void The_parent_of_the_root_is_nothing(string path, string? expected) =>
        Assert.Equal(expected, RemotePath.Parent(path));

    [Theory]
    [InlineData("/srv/www/index.html", "index.html")]
    [InlineData("/srv", "srv")]
    [InlineData("/", "/")]
    public void The_name_is_the_last_segment(string path, string expected) =>
        Assert.Equal(expected, RemotePath.Name(path));

    [Theory]
    [InlineData("/srv", "/srv", true)]
    [InlineData("/srv", "/srv/www", true)]
    [InlineData("/srv", "/srv/www/deep", true)]
    [InlineData("/", "/anything", true)]
    [InlineData("/srv", "/other", false)]
    [InlineData("/srv/data", "/srv/data2", false)]   // the trailing-separator trap
    [InlineData("/srv/www", "/srv", false)]
    public void Containment_does_not_confuse_a_prefix_with_a_parent(string root, string candidate, bool expected) =>
        Assert.Equal(expected, RemotePath.IsSelfOrDescendant(root, candidate));
}
