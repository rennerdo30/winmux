using WinMux.Core.Session;

namespace WinMux.Tests;

/// <summary>
/// Where the session lives. It used to follow the working directory into the installation, where an
/// update deleted it; these pin that it no longer does, and that the old copy is moved, not lost.
/// </summary>
public sealed class SessionLocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("winmux-session-").FullName;
    private string Home => Path.Combine(_root, "appdata", "WinMux", "session.toml");
    private string Install => Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
    private string Working => Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Without_a_session_anywhere_the_new_home_is_used()
    {
        var resolved = SessionLocation.Resolve(null, [Working, Install], Home);

        Assert.Equal(Home, resolved.Path);
        Assert.Null(resolved.MigratedFrom);
        Assert.False(File.Exists(Home), "nothing is created until there is something to save");
    }

    [Fact]
    public void A_session_left_in_the_installation_is_copied_home_and_kept()
    {
        var old = Path.Combine(Install, "session.toml");
        File.WriteAllText(old, "version = 1");

        var resolved = SessionLocation.Resolve(null, [Working, Install], Home);

        Assert.Equal(Home, resolved.Path);
        Assert.Equal(old, resolved.MigratedFrom);
        Assert.Equal("version = 1", File.ReadAllText(Home));
        Assert.True(File.Exists(old), "the original is the user's to delete");
    }

    [Fact]
    public void The_working_directory_is_looked_at_first()
    {
        File.WriteAllText(Path.Combine(Working, "session.toml"), "from working");
        File.WriteAllText(Path.Combine(Install, "session.toml"), "from install");

        SessionLocation.Resolve(null, [Working, Install], Home);

        Assert.Equal("from working", File.ReadAllText(Home));
    }

    [Fact]
    public void An_existing_home_session_is_never_overwritten_by_an_old_one()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Home)!);
        File.WriteAllText(Home, "current");
        File.WriteAllText(Path.Combine(Install, "session.toml"), "stale");

        var resolved = SessionLocation.Resolve(null, [Install], Home);

        Assert.Null(resolved.MigratedFrom);
        Assert.Equal("current", File.ReadAllText(Home));
    }

    [Fact]
    public void A_path_the_user_gave_always_wins()
    {
        var chosen = Path.Combine(_root, "mine.toml");

        var resolved = SessionLocation.Resolve(chosen, [Install], Home);

        Assert.Equal(chosen, resolved.Path);
        Assert.Null(resolved.MigratedFrom);
    }
}
