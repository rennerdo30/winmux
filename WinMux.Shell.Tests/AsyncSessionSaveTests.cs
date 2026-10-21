using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Shell.Actions;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

[Collection(AsyncSessionSaveCollection.Name)]
public sealed class AsyncSessionSaveTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public AsyncSessionSaveTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public Task Shutdown_deadline_bounds_an_active_write_and_keeps_the_old_file_complete() =>
        VerifyShutdownDeadlineAsync(SessionController.AwaitShutdownSaveAsync);

    [Fact]
    public async Task Shutdown_probe_rejects_an_await_that_ignores_the_deadline()
    {
        var error = await Record.ExceptionAsync(() => VerifyShutdownDeadlineAsync((save, _) => save));
        var failure = Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("Shutdown deadline did not return while the writer was held.", failure.Message);
        _output.WriteLine("Negative control rejected unbounded shutdown: " + failure.Message);
    }

    private static async Task VerifyShutdownDeadlineAsync(
        Func<Task<SessionSaveResult>, CancellationToken, Task<SessionSaveResult>> awaitShutdown)
    {
        var directory = Path.Combine(WinMux.Core.WinMuxDataDirectory.LocalPath,
            "shutdown-save-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.toml");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var old = SessionMapper.ToSnapshot(new LayoutTree(Pane.Empty()), "old");
        var next = SessionMapper.ToSnapshot(new LayoutTree(Pane.Empty()), "next");
        SessionFile.Save(path, new SessionSnapshot { Windows = [old] });
        var saver = new SessionAutosaver(path, TimeSpan.FromMinutes(1), (target, snapshot) =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test writer stalled");
            SessionFile.Save(target, snapshot);
        });
        Task<SessionSaveResult>? pending = null;
        await RunWithCleanupAsync(async () =>
        {
            saver.RequestSave(new SessionSnapshot { Windows = [next] });
            using var deadline = new CancellationTokenSource();
            pending = saver.FlushAsync(deadline.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);

            var bounded = awaitShutdown(pending, deadline.Token);
            // Expire the simulated deadline only after the synchronous writer is inside its gate.
            // A thread-pool timer racing another timer measures scheduling, not this contract.
            deadline.Cancel();
            SessionSaveResult result;
            try { result = await bounded.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException)
            {
                Assert.Fail("Shutdown deadline did not return while the writer was held.");
                throw;
            }
            Assert.False(result.Succeeded);
            Assert.IsType<TimeoutException>(result.Error);
            Assert.False(pending.IsCompleted);
            Assert.Equal("old", SessionFile.Load(path).Windows.Single().Title);

            release.Set();
            Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            Assert.Equal("next", SessionFile.Load(path).Windows.Single().Title);
        }, async () =>
        {
            release.Set();
            if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }, async () =>
        {
            // Release and join even when an assertion fails, before DisposeAsync can flush again.
            await saver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }, () =>
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task Slow_explicit_save_leaves_the_UI_dispatcher_available() => VerifyResponsiveSaveAsync();

    [Fact]
    public async Task Responsiveness_probe_rejects_a_synchronously_blocking_save()
    {
        // Model the old UI-thread wait without an unbounded GetResult or CPU load. The writer
        // stays gated, so this bounded synchronous wait must trip the pre-await responsiveness guard.
        var error = await Record.ExceptionAsync(() => VerifyResponsiveSaveAsync(
            pending => pending.Wait(TimeSpan.FromMilliseconds(250))));
        var failure = Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(error);
        Assert.Contains("Save action blocked before its first await.", failure.Message);
        _output.WriteLine("Negative control rejected blocking save: " + failure.Message);
    }

    private static async Task VerifyResponsiveSaveAsync(Action<Task<ActionDispatchResult>>? blockBeforeYield = null)
    {
        await Headless.RunAsync(async () =>
        {
            var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseWrite = new ManualResetEventSlim();
            var path = Path.Combine(WinMux.Core.WinMuxDataDirectory.LocalPath,
                "slow-save-" + Guid.NewGuid().ToString("N") + ".toml");
            var session = new SessionController(path, (_, _) =>
            {
                writeStarted.TrySetResult();
                if (!releaseWrite.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("test writer stalled");
            });
            var window = new MainWindow(new LayoutTree(Pane.Empty()), session,
                KeymapConfiguration.TmuxDefaults(), "slow save test");
            Task<ActionDispatchResult>? pending = null;
            await RunWithCleanupAsync(async () =>
            {
                var timer = Stopwatch.StartNew();
                pending = window.DispatchNamedActionAsync(ShellActionNames.SaveSession).AsTask();
                blockBeforeYield?.Invoke(pending);
                Assert.True(timer.ElapsedMilliseconds < 200, "Save action blocked before its first await.");
                Assert.False(pending.IsCompleted);
                await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                // This signal is completed on the UI thread, where its continuation belongs.
                var dispatched = new TaskCompletionSource();
                Dispatcher.UIThread.Post(() => dispatched.SetResult(), DispatcherPriority.Input);
                await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.False(releaseWrite.IsSet);
                Assert.False(pending.IsCompleted, "Input must run while the save is still blocked.");

                releaseWrite.Set();
                Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            }, async () =>
            {
                releaseWrite.Set();
                if (pending is not null) await pending.WaitAsync(TimeSpan.FromSeconds(2));
            }, () =>
            {
                session.Dispose();
                var handler = (Action<WinMux.Platform.ForegroundWindow>)Delegate.CreateDelegate(
                    typeof(Action<WinMux.Platform.ForegroundWindow>), window, "OnForegroundWindowChanged");
                PlatformServices.Foreground.Changed -= handler;
                ((IDisposable)typeof(MainWindow).GetField("_foreignProvider",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Dispose();
                typeof(MainWindow).GetField("_shutdownComplete",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Close();
                return Task.CompletedTask;
            });
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Probe_cleanup_preserves_assertions_and_cleanup_failures(bool failProbe, bool failCleanup)
    {
        var primary = new Xunit.Sdk.XunitException("probe assertion");
        var cleanup = new TimeoutException("cleanup deadline");
        var finalCleanupRan = false;
        var error = await Record.ExceptionAsync(() => RunWithCleanupAsync(
            () => failProbe ? Task.FromException(primary) : Task.CompletedTask,
            () => failCleanup ? Task.FromException(cleanup) : Task.CompletedTask,
            () => { finalCleanupRan = true; return Task.CompletedTask; }));

        Assert.True(finalCleanupRan);
        if (failProbe && failCleanup)
        {
            var both = Assert.IsType<AggregateException>(error);
            Assert.Collection(both.InnerExceptions,
                failure => Assert.Same(primary, failure), failure => Assert.Same(cleanup, failure));

            // Deterministic reproduction of the old finally masking, without scheduler load.
            var masked = await Record.ExceptionAsync(async () =>
            {
                try { await Task.FromException(primary); }
                finally { await Task.FromException(cleanup); }
            });
            Assert.Same(cleanup, masked);
            var rejected = Record.Exception(() => Assert.IsType<AggregateException>(masked));
            Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(rejected);
            _output.WriteLine("Cleanup control rejected old finally masking of the primary assertion.");
        }
        else if (failProbe) Assert.Same(primary, error);
        else if (failCleanup) Assert.Same(cleanup, error);
        else Assert.Null(error);
    }

    private static async Task RunWithCleanupAsync(Func<Task> probe, params Func<Task>[] cleanup)
    {
        // A finally exception used to replace the assertion that a negative control must see.
        // Preserve each failure; a cleanup timeout must still fail, even alongside an assertion.
        var errors = new Xunit.Sdk.ExceptionAggregator();
        await errors.RunAsync(probe);
        foreach (var step in cleanup) await errors.RunAsync(step);
        if (errors.ToException() is { } error) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
