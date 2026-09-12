using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Tests;

public sealed class SessionFileAtomicSaveTests
{
    [Fact]
    public async Task Concurrent_writers_leave_one_complete_session_and_no_temporary_files()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, SessionFile.DefaultFileName);
            var snapshots = Enumerable.Range(0, 24)
                .Select(index => Snapshot($"writer-{index}", index))
                .ToArray();
            using var start = new ManualResetEventSlim(initialState: false);
            var writes = snapshots.Select(snapshot => Task.Run(() =>
            {
                start.Wait();
                SessionFile.Save(path, snapshot);
            })).ToArray();

            start.Set();
            await Task.WhenAll(writes);

            var restored = SessionFile.Load(path);
            Assert.Contains(restored.Windows.Single().Title, snapshots.Select(s => s.Windows.Single().Title));
            Assert.Single(SessionMapper.FromSnapshot(restored.Windows.Single()).Panes);
            Assert.Empty(TemporaryFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Failed_replace_preserves_original_bytes_and_cleans_up_temporary_file()
    {
        var directory = NewTempDirectory();
        try
        {
            var path = Path.Combine(directory, SessionFile.DefaultFileName);
            SessionFile.Save(path, Snapshot("original", 0));
            var original = File.ReadAllBytes(path);

            // WinMux currently targets Windows. Omitting FileShare.Delete makes replacement fail
            // after the unique temporary file has been fully written and flushed.
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failure = Record.Exception(() => SessionFile.Save(path, Snapshot("replacement", 1)));
                Assert.True(
                    failure is IOException or UnauthorizedAccessException,
                    $"Expected an I/O or access failure, found {failure?.GetType().FullName ?? "no exception"}.");
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal("original", SessionFile.Load(path).Windows.Single().Title);
            Assert.Empty(TemporaryFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionSnapshot Snapshot(string title, int sequence)
    {
        var pane = new Pane(
            PaneId.New(),
            PaneKind.Terminal,
            title,
            new RestoreDescriptor
            {
                Kind = PaneKind.Terminal,
                Title = title,
                Program = "cmd.exe",
                Cwd = new WorkingDirectory(
                    @"C:\work",
                    CwdSource.LaunchDirectory,
                    DateTimeOffset.UnixEpoch.AddSeconds(sequence)),
            });
        var tree = new LayoutTree(pane);
        return new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            Windows = [SessionMapper.ToSnapshot(tree, title)],
        };
    }

    private static string[] TemporaryFiles(string directory) =>
        Directory.GetFiles(directory, $".{SessionFile.DefaultFileName}.*.tmp");

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "winmux-session-save-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
