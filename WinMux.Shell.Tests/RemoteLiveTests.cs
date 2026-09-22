using WinMux.Platform;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.FileBrowser.Remote;

namespace WinMux.Shell.Tests;

/// <summary>
/// SFTP and FTP against a server that is really running.
///
/// Everything else about the remote filesystems can be tested without one — path arithmetic, the
/// descriptor round trip, what happens when the host is unreachable — but none of that proves the
/// SSH.NET and FluentFTP calls are the right calls. Only a server does, and a wrong call here is
/// the kind that compiles.
///
/// <para>
/// Opt-in, because CI has no server: set <c>WINMUX_TEST_SFTP</c> to
/// <c>host:port:user:password</c> and these run; leave it unset and they skip. Any SFTP/FTP server
/// will do. The one used while writing this was SFTPGo in its portable mode, which serves both
/// protocols from one throwaway binary and needs no installation:
/// </para>
/// <code>
/// sftpgo portable -d &lt;dir&gt; -s 2022 --ftpd-port 2121 -u test -p &lt;password&gt; -g "*"
/// </code>
/// <para>
/// The FTP half reads <c>WINMUX_TEST_FTP</c> in the same shape. These tests create and delete only
/// inside a directory they make for themselves.
/// </para>
/// </summary>
public sealed class RemoteLiveTests
{
    private const string SftpVariable = "WINMUX_TEST_SFTP";
    private const string FtpVariable = "WINMUX_TEST_FTP";

    private static (RemoteFileBrowserTarget Target, StoredCredential Credential)? Settings(string variable, string scheme)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // host:port:user:password — password last so it may contain colons.
        var parts = raw.Split(':', 4);
        if (parts.Length != 4) return null;

        return (new RemoteFileBrowserTarget(scheme, parts[0], int.Parse(parts[1]), parts[2]),
                new StoredCredential(parts[2], parts[3]));
    }

    private static IFileBrowserFileSystem? Connect(string variable, string scheme)
    {
        if (Settings(variable, scheme) is not { } settings) return null;
        return RemoteFileSystemFactory.Create(settings.Target, settings.Credential);
    }

    [SkippableTheory]
    [InlineData(SftpVariable, RemoteFileBrowserTarget.Sftp)]
    [InlineData(FtpVariable, RemoteFileBrowserTarget.Ftp)]
    public void A_remote_filesystem_lists_creates_renames_copies_and_deletes(string variable, string scheme)
    {
        if (Connect(variable, scheme) is not { } filesystem)
        {
            throw new Xunit.SkipException(
                $"set {variable} to host:port:user:password to run this against a real server");
        }

        using var _ = (IDisposable)filesystem;

        // Listing the root at all proves the connection, the authentication and the listing call.
        var root = filesystem.EnumerateEntries("/").ToList();
        Assert.NotEmpty(root);

        var workspace = RemotePath.Combine("/", "winmux-live-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            filesystem.CreateDirectory(workspace);
            Assert.True(filesystem.DirectoryExists(workspace));
            Assert.False(filesystem.FileExists(workspace), "a directory must not report as a file");

            // A nested directory, so the recursive paths get exercised rather than only the flat ones.
            var nested = RemotePath.Combine(workspace, "nested");
            filesystem.CreateDirectory(nested);

            var listing = filesystem.EnumerateEntries(workspace).ToList();
            var found = Assert.Single(listing);
            Assert.Equal("nested", found.Name);
            Assert.True(found.IsDirectory);
            Assert.Equal(nested, found.Path);
            // The path the listing hands back must be usable as a path, not merely displayable.
            Assert.True(filesystem.DirectoryExists(found.Path));

            // Rename, which is the same call move-between-directories uses.
            var renamed = RemotePath.Combine(workspace, "renamed");
            filesystem.Move(nested, renamed, isDirectory: true);
            Assert.True(filesystem.DirectoryExists(renamed));
            Assert.False(filesystem.DirectoryExists(nested));

            // Copy a directory: neither protocol has a server-side copy, so this is the
            // download-and-upload path, and it is the one most likely to be wrong.
            var copy = RemotePath.Combine(workspace, "copy");
            filesystem.Copy(renamed, copy, isDirectory: true, CancellationToken.None);
            Assert.True(filesystem.DirectoryExists(copy));
            Assert.True(filesystem.DirectoryExists(renamed), "a copy must leave the original");

            Assert.Equal(2, filesystem.EnumerateEntries(workspace).Count());
        }
        finally
        {
            // Permanent, because neither protocol has anywhere recoverable to put it.
            filesystem.Delete(workspace, isDirectory: true, permanent: true);
        }

        Assert.False(filesystem.DirectoryExists(workspace));
    }

    [SkippableTheory]
    [InlineData(SftpVariable, RemoteFileBrowserTarget.Sftp)]
    [InlineData(FtpVariable, RemoteFileBrowserTarget.Ftp)]
    public void The_file_browser_model_drives_a_real_server(string variable, string scheme)
    {
        if (Connect(variable, scheme) is not { } filesystem)
        {
            throw new Xunit.SkipException(
                $"set {variable} to host:port:user:password to run this against a real server");
        }

        using var _ = (IDisposable)filesystem;

        // Through the real model, with the real non-overwriting rule, against a real server: this is
        // the whole feature end to end, minus the pixels.
        var pane = RemotePanes.At("/");
        var model = new FileBrowserModel(pane, RemotePath.Root, filesystem, new FileBrowserClipboard());

        Assert.Null(model.StatusMessage);
        Assert.NotEmpty(model.Entries);
        Assert.True(model.CanModify);
        Assert.False(model.CanRecoverDeletes, "no remote protocol here has a Recycle Bin");

        var folder = "winmux-model-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(model.CreateDirectory(folder), model.StatusMessage);

        try
        {
            Assert.Equal(folder, model.SelectedItem?.Name);

            // The collision rule, on a real server: a second folder of the same name becomes "(2)".
            Assert.True(model.CreateDirectory(folder), model.StatusMessage);
            Assert.Equal($"{folder} (2)", model.SelectedItem?.Name);

            // And an undoable delete is refused rather than silently made permanent.
            Assert.False(model.DeleteSelected(permanent: false));
            Assert.Contains("Recycle Bin", model.StatusMessage!, StringComparison.OrdinalIgnoreCase);

            Assert.True(model.DeleteSelected(permanent: true), model.StatusMessage);
        }
        finally
        {
            foreach (var name in new[] { folder, $"{folder} (2)" })
            {
                var path = RemotePath.Combine("/", name);
                try
                {
                    if (filesystem.DirectoryExists(path))
                        filesystem.Delete(path, isDirectory: true, permanent: true);
                }
                catch (IOException)
                {
                    // Best effort: the test has already reported whatever went wrong.
                }
            }
        }
    }

    [SkippableTheory]
    [InlineData(SftpVariable, RemoteFileBrowserTarget.Sftp)]
    [InlineData(FtpVariable, RemoteFileBrowserTarget.Ftp)]
    public void A_folder_goes_to_a_real_server_and_comes_back_byte_for_byte(string variable, string scheme)
    {
        if (Connect(variable, scheme) is not { } filesystem)
        {
            throw new Xunit.SkipException(
                $"set {variable} to host:port:user:password to run this against a real server");
        }

        using var _ = (IDisposable)filesystem;

        var local = Path.Combine(Path.GetTempPath(), "winmux-live-transfer-" + Guid.NewGuid().ToString("N")[..8]);
        var source = Directory.CreateDirectory(Path.Combine(local, "out")).FullName;
        var back = Directory.CreateDirectory(Path.Combine(local, "back")).FullName;
        var remote = RemotePath.Combine("/", "winmux-transfer-" + Guid.NewGuid().ToString("N")[..8]);

        // Binary, and bigger than one transfer block, so a text-mode FTP transfer or a short read
        // shows up as a mismatch rather than passing on a few ASCII bytes.
        var payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(source, "blob.bin"), payload);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "note.txt"), "nested");

        var disk = new SystemFileBrowserFileSystem(new NoTrash());
        try
        {
            filesystem.CreateDirectory(remote);

            Assert.Equal(2, FileBrowserTransfer.Copy(
                disk, source, filesystem, RemotePath.Combine(remote, "out"), isDirectory: true, CancellationToken.None));

            // Never overwrite, on the wire too: the server must refuse a name that is taken.
            Assert.Throws<IOException>(() => FileBrowserTransfer.Copy(
                disk, Path.Combine(source, "blob.bin"), filesystem, RemotePath.Combine(remote, "out/blob.bin"),
                isDirectory: false, CancellationToken.None));
            Assert.True(filesystem.FileExists(RemotePath.Combine(remote, "out/blob.bin")),
                "a refused write must not remove the file that was already there");

            Assert.Equal(2, FileBrowserTransfer.Copy(
                filesystem, RemotePath.Combine(remote, "out"), disk, Path.Combine(back, "out"), isDirectory: true, CancellationToken.None));

            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(back, "out", "blob.bin")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(back, "out", "sub", "note.txt")));
        }
        finally
        {
            try
            {
                if (filesystem.DirectoryExists(remote)) filesystem.Delete(remote, isDirectory: true, permanent: true);
            }
            catch (IOException)
            {
                // Best effort, as above.
            }

            Directory.Delete(local, recursive: true);
        }
    }

    private sealed class NoTrash : IFileTrash
    {
        public bool IsAvailable => false;

        public bool TrySend(string path, bool isDirectory, out string? error)
        {
            error = "not in tests";
            return false;
        }
    }
}

/// <summary>A file-browser pane sitting at a remote path, for the remote tests to drive.</summary>
internal static class RemotePanes
{
    public static WinMux.Core.Model.Pane At(string directory) =>
        new(WinMux.Core.Model.PaneId.New(),
            WinMux.Core.Model.PaneKind.FileBrowser,
            "remote",
            new WinMux.Core.Model.RestoreDescriptor
            {
                Kind = WinMux.Core.Model.PaneKind.FileBrowser,
                Title = "remote",
                Extras = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["current_directory"] = directory,
                },
            });
}
