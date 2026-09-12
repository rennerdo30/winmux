using System.Collections.Concurrent;
using System.Text;

namespace WinMux.Core.Session;

/// <summary>
/// Reading and writing the session file on disk.
///
/// Two rules from CLAUDE.md drive the design here, and both are about not losing user data:
/// saving is atomic, because priority 1 is that the session survives — a crash mid-write must not
/// leave a truncated file where a working one used to be; and a file that fails to load is
/// preserved alongside a clear error, never quietly replaced by an empty session.
/// </summary>
public static class SessionFile
{
    private static readonly ConcurrentDictionary<string, object> ReplaceLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public const string DefaultFileName = "session.toml";

    /// <summary>Serialize to TOML text.</summary>
    public static string Serialize(SessionSnapshot snapshot) => TomlSessionWriter.Write(snapshot);

    /// <summary>Parse TOML text. Throws <see cref="SessionFormatException"/> with a specific reason.</summary>
    public static SessionSnapshot Deserialize(string toml) => TomlSessionReader.Read(toml);

    /// <summary>
    /// Write atomically: a temporary file in the same directory, then a replace. Writing in place
    /// risks a torn file, and the session is the thing this product exists to keep.
    /// </summary>
    public static void Save(string path, SessionSnapshot snapshot)
    {
        var text = Serialize(snapshot);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The session path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        // Every writer gets its own sibling. A fixed `session.toml.tmp` lets two autosaves write
        // the same stream and then race to rename it, which can corrupt or lose both attempts.
        var temp = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temp,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 16 * 1024,
                    leaveOpen: true);
                writer.Write(text);
                writer.Flush();

                // The atomic rename protects the old file from a torn write; flushing the
                // temporary file first protects the newly renamed file from buffered data loss.
                stream.Flush(flushToDisk: true);
            }

            // The temp file is in the same directory, so this is one atomic replace operation and
            // works whether another writer created/replaced the destination in the meantime.
            lock (ReplaceLocks.GetOrAdd(fullPath, static _ => new object()))
            {
                ReplaceWithRetry(temp, fullPath);
            }
        }
        finally
        {
            // File.Delete is a no-op after the successful move. On failure, make the best possible
            // effort to avoid accumulating unique temporary files without masking the save error.
            try { File.Delete(temp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void ReplaceWithRetry(string temp, string destination)
    {
        const int attempts = 6;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < attempts - 1 && ex is IOException or UnauthorizedAccessException)
            {
                // A different WinMux process can be in its own atomic replace. Windows reports
                // that short collision as sharing/access denied; retain our complete temp file
                // and retry the rename rather than failing or rewriting it.
                Thread.Sleep(1 << attempt);
            }
        }
    }

    /// <summary>
    /// Load a session. On a malformed file the original is copied aside before the exception is
    /// thrown, and the exception says where the copy went, so a user can repair or report it.
    /// </summary>
    public static SessionSnapshot Load(string path)
    {
        var text = File.ReadAllText(path);
        try
        {
            return Deserialize(text);
        }
        catch (SessionFormatException ex)
        {
            var backup = QuarantinePath(path);
            try { File.Copy(path, backup, overwrite: true); }
            catch (IOException) { backup = "(could not be written)"; }
            catch (UnauthorizedAccessException) { backup = "(could not be written)"; }

            throw new SessionFormatException(
                $"{ex.Message}{Environment.NewLine}" +
                $"The file was left untouched and a copy saved to {backup}. " +
                "WinMux will not start an empty session over a layout it failed to understand.");
        }
    }

    /// <summary>Load if present; return null if the file simply does not exist yet.</summary>
    public static SessionSnapshot? LoadIfExists(string path) => File.Exists(path) ? Load(path) : null;

    internal static string QuarantinePath(string path) =>
        path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
}
