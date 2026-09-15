using WinMux.Core.Model;
using WinMux.Platform;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// What a remote filesystem does when the server is not there.
///
/// This is the case a user meets most often — a laptop off the VPN, a host that has moved, a
/// password that has been rotated — and it is testable without a server, which the happy path is
/// not. The requirement is narrow and important: the pane must report it, not die of it. SSH.NET
/// and FluentFTP raise their own exception families, and an unmapped one escapes through the
/// background operation and takes the pane with it.
/// </summary>
public sealed class RemoteFileSystemFailureTests
{
    /// <summary>Port 1 on the loopback: nothing listens there, and it fails immediately.</summary>
    private static RemoteFileBrowserTarget Unreachable(string scheme) => new(scheme, "127.0.0.1", 1, "nobody");

    private static IFileBrowserFileSystem Connect(string scheme) =>
        RemoteFileSystemFactory.Create(Unreachable(scheme), new StoredCredential("nobody", "nothing"));

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftp")]
    public void An_unreachable_server_raises_an_io_failure_not_a_protocol_exception(string scheme)
    {
        using var filesystem = (IDisposable)Connect(scheme);

        // IOException is what FileBrowserModel catches and reports. Anything else escapes.
        var failure = Assert.ThrowsAny<Exception>(
            () => ((IFileBrowserFileSystem)filesystem).EnumerateEntries("/").ToList());

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"a {scheme} failure surfaced as {failure.GetType().FullName}, which the model does not catch");
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftp")]
    public void The_model_turns_an_unreachable_server_into_a_status_message(string scheme)
    {
        // End to end through the real model: the pane shows words, and does not throw.
        var filesystem = Connect(scheme);
        try
        {
            var model = new FileBrowserModel(Pane(), RemotePath.Root, filesystem);

            Assert.NotNull(model.StatusMessage);
            Assert.Contains("unavailable", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(model.Entries);
        }
        finally
        {
            (filesystem as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftp")]
    public void A_remote_filesystem_offers_no_recycle_bin_and_admits_it(string scheme)
    {
        using var filesystem = (IDisposable)Connect(scheme);
        var remote = (IFileBrowserFileSystem)filesystem;

        Assert.True(remote.CanModify);
        Assert.False(remote.CanRecoverDeletes);

        // And a recoverable delete is refused outright rather than silently made permanent.
        var failure = Assert.Throws<IOException>(() => remote.Delete("/x", isDirectory: false, permanent: false));
        Assert.Contains("Recycle Bin", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("ftp")]
    public void Remote_paths_are_posix_whatever_the_host_os_is(string scheme)
    {
        using var filesystem = (IDisposable)Connect(scheme);
        var remote = (IFileBrowserFileSystem)filesystem;

        // Running on Windows, where Path.Combine would answer with a backslash.
        Assert.Equal("/srv/www", remote.Combine("/srv", "www"));
        Assert.Equal("/srv", remote.GetParentDirectory("/srv/www"));
        Assert.Null(remote.GetParentDirectory("/"));
        Assert.Equal("/srv/www", remote.GetFullPath("srv/www/"));
        Assert.Same(StringComparer.Ordinal, remote.PathComparer);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        // The pane disposes on close, and a close can arrive twice (PaneRemoved then DisposeAsync).
        var filesystem = (IDisposable)Connect("sftp");
        filesystem.Dispose();
        filesystem.Dispose();
    }

    private static Pane Pane() =>
        new(PaneId.New(), PaneKind.FileBrowser, "remote", new RestoreDescriptor
        {
            Kind = PaneKind.FileBrowser,
            Title = "remote",
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["current_directory"] = "/srv",
            },
        });
}
