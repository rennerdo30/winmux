namespace WinMux.Shell.Tests;

/// <summary>
/// Tests for the test harness, which sounds like navel-gazing and is not.
///
/// <para>
/// The headless helper shipped, briefly, in a state where a failing assertion inside an async test
/// body was silently discarded: every UI test passed unconditionally, including one with
/// <c>Assert.Fail</c> as its first statement. A suite that cannot fail is worse than no suite,
/// because it gets counted — and it would have been counted as the fix for exactly the gap that let
/// a broken pane ship in the first place.
/// </para>
///
/// <para>
/// The cause was that <c>HeadlessUnitTestSession.Dispatch</c>'s void-returning overload does not
/// await what the body returns. A synchronous throw surfaced, because it happened while the
/// delegate was being invoked; anything after the first <c>await</c> landed in a task nobody
/// looked at. <see cref="Headless.RunAsync"/> returns a value so that it binds to the overload that
/// does await, and these tests are what keep it that way.
/// </para>
/// </summary>
public sealed class HeadlessHarnessTests
{
    [Fact]
    public async Task A_synchronous_throw_propagates()
    {
        var thrown = await Record.ExceptionAsync(() => Headless.RunAsync(() =>
        {
            throw new InvalidOperationException("sync-probe");
        }));

        Assert.NotNull(thrown);
    }

    [Fact]
    public async Task A_throw_after_an_await_propagates()
    {
        // The one that was broken. The exception lands in the state machine's task rather than on
        // the call that started it, so a harness that ignores that task loses it.
        var ran = false;

        var thrown = await Record.ExceptionAsync(() => Headless.RunAsync(async () =>
        {
            ran = true;
            await Task.Yield();
            throw new InvalidOperationException("async-probe");
        }));

        Assert.True(ran, "the body never ran");
        Assert.NotNull(thrown);
        Assert.Contains("async-probe", thrown!.Message);
    }

    [Fact]
    public async Task A_failed_assertion_in_an_async_body_propagates()
    {
        // The same thing in the form it actually takes in a test: not a throw anyone wrote, but an
        // assertion that did not hold.
        var thrown = await Record.ExceptionAsync(() => Headless.RunAsync(async () =>
        {
            await Task.Yield();
            Assert.Fail("assert-probe");
        }));

        Assert.NotNull(thrown);
    }

    [Fact]
    public async Task A_failed_assertion_in_a_synchronous_body_propagates()
    {
        var thrown = await Record.ExceptionAsync(() => Headless.RunSync(() => Assert.Fail("sync-assert-probe")));

        Assert.NotNull(thrown);
    }

    [Fact]
    public Task The_body_runs_on_avalonia_s_ui_thread() => Headless.RunSync(() =>
    {
        // Controls may only be touched from this thread; if the harness ever stopped marshalling
        // onto it, every other test here would fail in a far more confusing way than this one.
        Assert.True(Avalonia.Threading.Dispatcher.UIThread.CheckAccess());
    });
}
