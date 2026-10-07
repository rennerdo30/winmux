using System.Buffers.Binary;
using System.IO.Pipes;
using WinMux.Shell;

namespace WinMux.Shell.Tests;

public sealed class SingleInstanceLaunchTests
{
    private static string Endpoint() => "winmux-launch-test-" + Guid.NewGuid().ToString("N");
    private static LaunchRequest Request() => new(Path.GetTempPath());

    [Fact]
    public async Task Cold_start_keeps_the_captured_directory_until_a_handler_is_ready()
    {
        using var primary = SingleInstanceLaunch.Open(Endpoint());
        var request = Request();
        var completion = primary.QueueInitial(request);
        Assert.True(primary.IsPrimary);
        Assert.False(completion.IsCompleted);
        LaunchRequest? received = null;
        primary.SetHandler(value => { received = value; return Task.CompletedTask; });
        Assert.True((await completion.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(request, received);
    }

    [Fact]
    public async Task A_secondary_launch_forwards_to_the_primary_and_waits_until_the_folder_opens()
    {
        var endpoint = Endpoint();
        using var primary = SingleInstanceLaunch.Open(endpoint);
        using var secondary = SingleInstanceLaunch.Open(endpoint);
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = new TaskCompletionSource<LaunchRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request();
        var result = secondary.ForwardAsync(request, TimeSpan.FromSeconds(5));
        primary.SetHandler(async value => { invoked.SetResult(value); await opened.Task; });
        Assert.Equal(request, await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(result.IsCompleted); // ACK cannot announce success before tab creation completes.
        opened.SetResult();
        Assert.True((await result).Success);
    }

    [Fact]
    public async Task Startup_failure_returns_an_explanation_to_a_connected_secondary()
    {
        var endpoint = Endpoint();
        using var primary = SingleInstanceLaunch.Open(endpoint);
        using var secondary = SingleInstanceLaunch.Open(endpoint);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SetHandler(async _ => { entered.SetResult(); await release.Task; });
        var reply = secondary.ForwardAsync(Request(), TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        primary.Dispose();
        var failure = await reply;
        Assert.False(failure.Success);
        Assert.Contains("stopped during startup", failure.Message);
        release.SetResult();
    }

    [Fact]
    public async Task A_launch_timeout_never_creates_a_second_primary()
    {
        var endpoint = Endpoint();
        using var primary = SingleInstanceLaunch.Open(endpoint);
        using var secondary = SingleInstanceLaunch.Open(endpoint);
        var reply = await secondary.ForwardAsync(Request(), TimeSpan.FromMilliseconds(150));
        Assert.False(reply.Success);
        Assert.Contains("No duplicate instance was started", reply.Message);
        Assert.False(secondary.IsPrimary);
        using var third = SingleInstanceLaunch.Open(endpoint);
        Assert.False(third.IsPrimary);
    }

    [Fact]
    public async Task Failed_handler_reports_the_error_and_the_next_launch_still_works()
    {
        using var primary = SingleInstanceLaunch.Open(Endpoint());
        var calls = 0;
        var failures = new List<string>();
        primary.LaunchFailed += failures.Add;
        primary.SetHandler(_ => ++calls == 1 ? Task.FromException(new IOException("folder access denied")) : Task.CompletedTask);
        var first = await primary.QueueInitial(Request()).WaitAsync(TimeSpan.FromSeconds(5));
        var second = await primary.QueueInitial(Request()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(first.Success);
        Assert.Contains("folder access denied", first.Message);
        Assert.Single(failures);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task Invalid_frame_is_contained_and_the_listener_accepts_the_next_client()
    {
        var endpoint = Endpoint();
        using var primary = SingleInstanceLaunch.Open(endpoint);
        primary.SetHandler(_ => Task.CompletedTask);
        await using (var malformed = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await malformed.ConnectAsync(deadline.Token);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, LaunchProtocol.MaximumFrameBytes + 1);
            await malformed.WriteAsync(header, deadline.Token);
            await malformed.FlushAsync(deadline.Token);
        }
        using var secondary = SingleInstanceLaunch.Open(endpoint);
        Assert.True((await secondary.ForwardAsync(Request(), TimeSpan.FromSeconds(5))).Success);
    }

    [Fact]
    public async Task Request_validation_rejects_relative_paths_and_unsupported_versions()
    {
        using var primary = SingleInstanceLaunch.Open(Endpoint());
        Assert.False((await primary.QueueInitial(new LaunchRequest("relative"))).Success);
        Assert.False((await primary.QueueInitial(Request() with { Version = 99 })).Success);
    }

    [Fact]
    public void All_handles_released_allow_the_next_process_to_be_primary()
    {
        var endpoint = Endpoint();
        using (var first = SingleInstanceLaunch.Open(endpoint))
        using (var second = SingleInstanceLaunch.Open(endpoint))
        {
            Assert.True(first.IsPrimary);
            Assert.False(second.IsPrimary);
        }
        using var replacement = SingleInstanceLaunch.Open(endpoint);
        Assert.True(replacement.IsPrimary);
    }
}
