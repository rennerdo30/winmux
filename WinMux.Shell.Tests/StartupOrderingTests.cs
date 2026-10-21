namespace WinMux.Shell.Tests;

public sealed class StartupOrderingTests
{
    [Theory]
    [InlineData("--no-prefix")]
    [InlineData("--keymap", "custom.json")]
    [InlineData("named.toml")]
    [InlineData("--open-directory", ".", "--no-prefix")]
    [InlineData("--open-directory", ".", "--keymap", "custom.json")]
    [InlineData("named.toml", "--keymap", "custom.json", "--no-prefix")]
    public void RejectedWarmStartupNeverInstallsCrashLog(params string[] argv)
    {
        var endpoint = "winmux-ordering-test-" + Guid.NewGuid().ToString("N");
        using var primary = SingleInstanceLaunch.Open(endpoint);
        Assert.True(primary.IsPrimary);
        var forwarded = 0;
        primary.SetHandler(_ => { Interlocked.Increment(ref forwarded); return Task.CompletedTask; });
        var crashLogInstalls = 0;
        var errors = new List<(string Title, string Message)>();

        // This is Main's production orchestration, including parsing, ownership, warm validation
        // and exit code. Substitute only the process hook, native error dialog and endpoint.
        var code = Program.Run(argv, () => crashLogInstalls++,
            (title, message) => errors.Add((title, message)), () => SingleInstanceLaunch.Open(endpoint));

        Assert.Equal(64, code);
        var error = Assert.Single(errors);
        Assert.Equal("WinMux is already running", error.Title);
        Assert.Contains("cannot be applied", error.Message);
        Assert.Equal(0, Volatile.Read(ref forwarded));
        Assert.Equal(0, crashLogInstalls); // No ProcessExit hook can later create/append/rotate crash.log.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedWarmStartupForwardsOnceWithoutInstallingPrimaryCrashLog(bool explicitFolder)
    {
        var endpoint = "winmux-ordering-test-" + Guid.NewGuid().ToString("N");
        using var primary = SingleInstanceLaunch.Open(endpoint);
        Assert.True(primary.IsPrimary);
        var forwarded = new List<LaunchRequest>();
        primary.SetHandler(request => { forwarded.Add(request); return Task.CompletedTask; });
        var crashLogInstalls = 0;
        var errors = new List<string>();
        var folder = Path.GetFullPath(Environment.CurrentDirectory);

        var code = Program.Run(explicitFolder ? ["--open-directory", folder] : [],
            () => crashLogInstalls++, (_, message) => errors.Add(message),
            () => SingleInstanceLaunch.Open(endpoint));

        Assert.Equal(0, code);
        Assert.Empty(errors);
        Assert.Equal(folder, Assert.Single(forwarded).Directory);
        Assert.Equal(0, crashLogInstalls);
    }
}
