using System.Globalization;

namespace WinMux.Connections;

/// <summary>
/// MobaXterm's saved sessions, from <c>MobaXterm.ini</c>.
///
/// <para>
/// Folders are sections and sessions are keys inside them: a section <c>[Bookmarks_2]</c> holds a
/// <c>SubRep</c> naming the folder, then one numbered key per session whose <em>value</em> is the
/// whole connection as a percent-separated list. Nested folders are a backslash inside
/// <c>SubRep</c>, so the hierarchy is built rather than read, as it is for WinSCP.
/// </para>
///
/// <para>
/// The value's shape is <c>name%kind%host%port%user%…</c> with dozens of positional fields, most of
/// them terminal preferences. Only the first handful are read, and the value is otherwise written
/// back byte for byte — a positional format is exactly the kind where a writer that reassembles
/// what it understood silently loses the rest.
/// </para>
///
/// <para>
/// MobaXterm's stored passwords are not here. It keeps them outside the ini, obfuscated against the
/// machine and the Windows account, and reading them is a separate piece of work
/// (ADR 0025); this source reports none rather than pretending.
/// </para>
/// </summary>
public sealed class MobaXtermSource : IConnectionSource
{
    /// <summary>Which session line each entry came from, so a save edits that line and no other.</summary>
    private readonly Dictionary<ConnectionNode, (string Section, string Key, string[] Fields)> _lines = [];

    private readonly string _path;
    private IniDocument? _document;

    public MobaXtermSource(string? path = null) => _path = path ?? DefaultPath();

    /// <summary>
    /// Where MobaXterm keeps it, asked of Windows rather than assembled from the user's profile.
    ///
    /// <para>
    /// <c>MyDocuments</c>, not <c>UserProfile</c> + "Documents". Those are the same folder only on
    /// an English install that nobody has redirected: this missed a real MobaXterm sitting in
    /// <c>…\OneDrive\Dokumente\MobaXterm</c>, wrong about the language and wrong about the
    /// redirection. Windows knows where Documents is and will say so.
    /// </para>
    /// </summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "MobaXterm",
        "MobaXterm.ini");

    /// <summary>
    /// Every place a MobaXterm.ini is likely to be, best first.
    ///
    /// <para>
    /// Documents is where the installed build keeps it, and Documents may be redirected to
    /// OneDrive. On the machine that turned this up, <c>MyDocuments</c> still answered with the
    /// local folder while the file was under <c>OneDrive\Dokumente</c> — so OneDrive's own folders
    /// are looked in as well.
    /// </para>
    ///
    /// <para>
    /// By looking, not by knowing the word for "Documents". A list of translations covers the
    /// languages somebody thought of and misses the rest; one directory listing covers all of them.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LikelyPaths()
    {
        var places = new List<string> { DefaultPath() };

        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive)
        {
            places.Add(Path.Combine(oneDrive, "MobaXterm", "MobaXterm.ini"));

            try
            {
                foreach (var folder in Directory.EnumerateDirectories(oneDrive))
                {
                    places.Add(Path.Combine(folder, "MobaXterm", "MobaXterm.ini"));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A OneDrive that cannot be listed is one fewer place to look, not a failure.
            }
        }

        places.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MobaXterm", "MobaXterm.ini"));

        return [.. places.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public string DisplayName => "MobaXterm";

    public string Location => _path;

    public bool Exists => File.Exists(_path);

    /// <summary>MobaXterm holds its ini and writes it on exit, so anything saved under it is lost.</summary>
    public bool CanWriteWhileOtherToolRuns => false;

    public ConnectionFolder Read()
    {
        if (!Exists) throw new ConnectionSourceException($"There is no MobaXterm configuration at {_path}.");

        _document = IniDocument.Load(_path);
        _lines.Clear();

        var bookmarks = _document.Sections
            .Where(name => name.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (bookmarks.Length == 0)
        {
            throw new ConnectionSourceException(
                $"{_path} has no Bookmarks section, so it is not a MobaXterm configuration.");
        }

        var root = new ConnectionFolder("MobaXterm");
        foreach (var section in bookmarks)
        {
            var values = _document.ReadAll(section);
            var folder = FolderFor(root, values.GetValueOrDefault("SubRep", string.Empty));

            foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                // SubRep names the folder and ImgNum is its icon; everything else numbered is a
                // session.
                if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase)) continue;

                var fields = value.Split('%');
                if (fields.Length < 3) continue;

                // The key is the session's name; the value is entirely settings.
                var entry = new ConnectionEntry(key.Trim(), SettingsOf(fields));
                _lines[entry] = (section, key, fields);
                folder.Add(entry);
            }
        }

        return root;
    }

    public void Write(ConnectionFolder root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var document = _document
            ?? throw new ConnectionSourceException("Read the MobaXterm sessions before writing them back.");

        foreach (var entry in root.Entries())
        {
            if (!_lines.TryGetValue(entry, out var line)) continue;

            // The original fields, with only the ones WinMux understands replaced. Everything past
            // them — colours, fonts, X11 forwarding, macros — goes back exactly as it was.
            var fields = (string[])line.Fields.Clone();
            if (fields.Length > HostField && entry.Settings.Host.TryGet(out var host)) fields[HostField] = host;
            if (fields.Length > PortField && entry.Settings.Port.TryGet(out var port))
            {
                fields[PortField] = port.ToString(CultureInfo.InvariantCulture);
            }

            if (fields.Length > UserField && entry.Settings.User.TryGet(out var user)) fields[UserField] = user;

            // A rename is a rename of the key, because that is where the name lives.
            var key = line.Key;
            if (!string.Equals(key, entry.Name, StringComparison.Ordinal) &&
                document.RenameKey(line.Section, key, entry.Name))
            {
                key = entry.Name;
            }

            document.Write(line.Section, key, string.Join('%', fields));
            _lines[entry] = line with { Key = key, Fields = fields };
        }

        Backup();
        document.Save(_path);
    }

    /// <summary>
    /// None. MobaXterm keeps its credentials outside this file and bound to the machine; a source
    /// that returned an empty list *because it had not looked* would be indistinguishable from one
    /// that looked and found nothing, so this says which it is here rather than in a comment
    /// nobody reads at the call site.
    /// </summary>
    public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root) => [];

    /// <summary>
    /// <c>SubRep</c> is a path of folders separated by backslashes, and empty for the top level.
    /// </summary>
    private static ConnectionFolder FolderFor(ConnectionFolder root, string subRep)
    {
        var parent = root;
        foreach (var name in subRep.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = parent.Children.OfType<ConnectionFolder>()
                .FirstOrDefault(folder => folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new ConnectionFolder(name);
                parent.Add(existing);
            }

            parent = existing;
        }

        return parent;
    }

    /// <summary>
    /// A session is <c>name=#109#0%host%port%user%…</c>: the <em>key</em> is the name, and the
    /// value starts with the kind — an icon number and then the protocol. MobaXterm's numbering
    /// puts SSH at 0, telnet at 1, RDP at 4, FTP at 6 and SFTP at 7.
    ///
    /// <para>
    /// This was read one field out until 2026-09-24, with the name taken from the value, so every
    /// bookmark was called "#109#0" and its host was its port. The sample it was written against
    /// was invented rather than taken from a real MobaXterm.ini — which is why the tests agreed
    /// with it.
    /// </para>
    /// </summary>
    private const int KindField = 0;
    private const int HostField = 1;
    private const int PortField = 2;
    private const int UserField = 3;

    private static ConnectionSettings SettingsOf(string[] fields)
    {
        var user = Field(fields, UserField);

        return new ConnectionSettings
        {
            Protocol = Inherited<ConnectionProtocol>.Of(ProtocolOf(Field(fields, KindField))),
            Host = Inherited<string>.Of(Field(fields, HostField)),
            Port = PortOf(Field(fields, PortField)),
            User = user.Length == 0 ? Inherited<string>.Inherit : Inherited<string>.Of(user),
        };
    }

    private static string Field(string[] fields, int index) =>
        index < fields.Length ? fields[index].Trim() : string.Empty;

    private static ConnectionProtocol ProtocolOf(string kind)
    {
        var parts = kind.Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol))
        {
            return ConnectionProtocol.Unknown;
        }

        return protocol switch
        {
            0 => ConnectionProtocol.Ssh,
            1 => ConnectionProtocol.Telnet,
            4 => ConnectionProtocol.Rdp,
            6 => ConnectionProtocol.Ftp,
            7 => ConnectionProtocol.Sftp,
            _ => ConnectionProtocol.Unknown,
        };
    }

    private static Inherited<int> PortOf(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? Inherited<int>.Of(port)
            : Inherited<int>.Inherit;

    private void Backup()
    {
        var backup = _path + ".winmux-backup";
        if (File.Exists(backup) || !File.Exists(_path)) return;

        try
        {
            File.Copy(_path, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionSourceException(
                $"{_path} was not written, because a backup could not be made first: {exception.Message}",
                exception);
        }
    }
}
