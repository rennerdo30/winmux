using System.Diagnostics;
using System.Reflection;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Shell.Actions;
using WinMux.Shell.Keymap;

namespace WinMux.Shell.Tests;

public sealed class AsyncSessionSaveTests
{
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
