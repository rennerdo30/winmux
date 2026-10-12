using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;
using WinMux.Shell.Actions;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

public sealed class AsyncSessionSaveTests
{
    [Fact]
    public async Task Shutdown_deadline_bounds_an_active_write_and_keeps_the_old_file_complete()
    {
        var directory = Path.Combine(WinMux.Core.WinMuxDataDirectory.LocalPath,
            "shutdown-save-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.toml");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var old = SessionMapper.ToSnapshot(new LayoutTree(Pane.Empty()), "old");
        var next = SessionMapper.ToSnapshot(new LayoutTree(Pane.Empty()), "next");
        SessionFile.Save(path, new SessionSnapshot { Windows = [old] });
        try
        {
            await using var saver = new SessionAutosaver(path, TimeSpan.FromMinutes(1), (target, snapshot) =>
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test writer stalled");
                SessionFile.Save(target, snapshot);
            });
            saver.RequestSave(new SessionSnapshot { Windows = [next] });
            using var deadline = new CancellationTokenSource();
            var pending = saver.FlushAsync(deadline.Token);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
            deadline.CancelAfter(TimeSpan.FromMilliseconds(80));

            var result = await SessionController.AwaitShutdownSaveAsync(pending, deadline.Token)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(result.Succeeded);
            Assert.IsType<TimeoutException>(result.Error);
            Assert.Equal("old", SessionFile.Load(path).Windows.Single().Title);

            release.Set();
            Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Succeeded);
            Assert.Equal("next", SessionFile.Load(path).Windows.Single().Title);
        }
        finally
        {
            release.Set();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Slow_explicit_save_leaves_the_UI_dispatcher_available()
    {
        await Headless.RunAsync(async () =>
        {
            using var writeStarted = new ManualResetEventSlim();
            using var releaseWrite = new ManualResetEventSlim();
            var path = Path.Combine(WinMux.Core.WinMuxDataDirectory.LocalPath, "slow-save-test.toml");
            var session = new SessionController(path, (_, _) =>
            {
                writeStarted.Set();
                releaseWrite.Wait(TimeSpan.FromSeconds(3));
            });
            var window = new MainWindow(new LayoutTree(Pane.Empty()), session,
                KeymapConfiguration.TmuxDefaults(), "slow save test");
            try
            {
                var timer = Stopwatch.StartNew();
                var pending = window.DispatchNamedActionAsync(ShellActionNames.SaveSession);
                Assert.True(timer.ElapsedMilliseconds < 200, "Save action blocked before its first await.");
                Assert.False(pending.IsCompleted);
                Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(2)));

                var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Dispatcher.UIThread.Post(() => dispatched.SetResult(), DispatcherPriority.Input);
                await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));

                releaseWrite.Set();
                Assert.True((await pending).Succeeded);
            }
            finally
            {
                releaseWrite.Set();
                session.Dispose();
                var handler = (Action<WinMux.Platform.ForegroundWindow>)Delegate.CreateDelegate(
                    typeof(Action<WinMux.Platform.ForegroundWindow>), window, "OnForegroundWindowChanged");
                PlatformServices.Foreground.Changed -= handler;
                ((IDisposable)typeof(MainWindow).GetField("_foreignProvider",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Dispose();
                typeof(MainWindow).GetField("_shutdownComplete",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                window.Close();
            }
        });
    }
}
