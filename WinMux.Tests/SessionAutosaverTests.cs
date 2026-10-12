using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public sealed class SessionAutosaverTests
{
    [Fact]
    public async Task Rapid_changes_are_coalesced_and_the_latest_snapshot_wins()
    {
        var writes = new List<string>();
        var completion = new TaskCompletionSource<SessionSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var saver = new SessionAutosaver("ignored", TimeSpan.FromMilliseconds(40),
            (_, snapshot) => writes.Add(snapshot.Windows.Single().Title));
        saver.SaveCompleted += result => completion.TrySetResult(result);

        saver.RequestSave(Snapshot("first"));
        saver.RequestSave(Snapshot("second"));
        saver.RequestSave(Snapshot("final"));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Succeeded);
        Assert.Equal(["final"], writes);
    }

    [Fact]
    public async Task Flush_writes_immediately_and_reports_success()
    {
        var writes = 0;
        await using var saver = new SessionAutosaver("ignored", TimeSpan.FromMinutes(1),
            (_, _) => writes++);
        saver.RequestSave(Snapshot("now"));

        var result = await saver.FlushAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task Repeated_identical_intent_does_not_rewrite_the_session()
    {
        var writes = 0;
        var completed = 0;
        await using var saver = new SessionAutosaver("ignored", TimeSpan.FromMinutes(1),
            (_, _) => writes++);
        saver.SaveCompleted += _ => completed++;
        var original = Snapshot("unchanged");

        saver.RequestSave(original);
        Assert.True((await saver.FlushAsync()).Succeeded);
        saver.RequestSave(original with { SavedAt = original.SavedAt.AddMinutes(10) });
        Assert.True((await saver.FlushAsync()).Succeeded);
        Assert.Equal(1, writes);
        Assert.Equal(1, completed);

        saver.RequestSave(original with
        {
            Windows = [original.Windows.Single() with { Title = "changed" }],
        });
        Assert.True((await saver.FlushAsync()).Succeeded);
        Assert.Equal(2, writes);
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task Unchanged_intent_recreates_a_removed_session_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "winmux-save-review-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.toml");
        try
        {
            await using var saver = new SessionAutosaver(path, TimeSpan.FromMinutes(1));
            var snapshot = Snapshot("keep this layout");
            saver.RequestSave(snapshot);
            Assert.True((await saver.FlushAsync()).Succeeded);
            File.Delete(path);

            saver.RequestSave(snapshot);
            Assert.True((await saver.FlushAsync()).Succeeded);
            Assert.Equal("keep this layout", SessionFile.Load(path).Windows.Single().Title);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Background_write_failure_is_observable()
    {
        var completion = new TaskCompletionSource<SessionSaveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var saver = new SessionAutosaver("ignored", TimeSpan.Zero,
            (_, _) => throw new IOException("disk full"));
        saver.SaveCompleted += result => completion.TrySetResult(result);

        saver.RequestSave(Snapshot("failure"));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(result.Succeeded);
        Assert.Contains("disk full", result.Error!.Message, StringComparison.Ordinal);
        Assert.Same(result, saver.LastResult);
    }

    [Fact]
    public async Task Change_during_an_in_flight_write_is_saved_before_flush_returns()
    {
        using var firstWriteStarted = new ManualResetEventSlim();
        using var releaseFirstWrite = new ManualResetEventSlim();
        var writes = new List<string>();
        var writeNumber = 0;
        await using var saver = new SessionAutosaver("ignored", TimeSpan.Zero, (_, snapshot) =>
        {
            lock (writes) writes.Add(snapshot.Windows.Single().Title);
            if (Interlocked.Increment(ref writeNumber) == 1)
            {
                firstWriteStarted.Set();
                Assert.True(releaseFirstWrite.Wait(TimeSpan.FromSeconds(2)));
            }
        });

        saver.RequestSave(Snapshot("first"));
        Assert.True(firstWriteStarted.Wait(TimeSpan.FromSeconds(2)));
        saver.RequestSave(Snapshot("latest"));
        releaseFirstWrite.Set();

        var result = await saver.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Succeeded);
        lock (writes) Assert.Equal(["first", "latest"], writes);
    }

    [Fact]
    public async Task Dispose_waits_for_an_in_flight_write_and_does_not_duplicate_it()
    {
        using var writeStarted = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        var writes = 0;
        var saver = new SessionAutosaver("ignored", TimeSpan.Zero, (_, _) =>
        {
            Interlocked.Increment(ref writes);
            writeStarted.Set();
            Assert.True(releaseWrite.Wait(TimeSpan.FromSeconds(2)));
        });
        saver.RequestSave(Snapshot("last"));
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(2)));

        var disposing = saver.DisposeAsync().AsTask();
        releaseWrite.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, writes);
    }

    private static SessionSnapshot Snapshot(string title)
    {
        var tree = new LayoutTree(Pane.Terminal("test"));
        return new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, title)],
        };
    }
}
