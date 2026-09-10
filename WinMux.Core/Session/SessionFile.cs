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
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, text);

        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
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
