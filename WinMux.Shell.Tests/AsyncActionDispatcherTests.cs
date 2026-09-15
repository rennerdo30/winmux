using WinMux.Shell.Actions;

namespace WinMux.Shell.Tests;

public sealed class AsyncActionDispatcherTests
{
    [Fact]
    public async Task Async_action_is_awaited_by_async_callers()
    {
        var dispatcher = new ActionDispatcher();
        var completed = false;
        dispatcher.RegisterAsync("wait", async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            completed = true;
        });

        var result = await dispatcher.DispatchAsync("wait");

        Assert.True(result.Succeeded);
        Assert.True(completed);
    }

    [Fact]
    public async Task Async_action_failure_is_returned_to_cli_style_callers()
    {
        var dispatcher = new ActionDispatcher();
        dispatcher.RegisterAsync("fail", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("late failure");
        });

        var result = await dispatcher.DispatchAsync("fail");

        Assert.False(result.Succeeded);
        Assert.Contains("late failure", result.Error);
    }
}
