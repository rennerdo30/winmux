using System.Text;

namespace WinMux.Connections;

/// <summary>
/// An INI file, kept as it was written so it can be given back that way.
///
/// <para>
/// WinSCP and MobaXterm both keep their sessions in one, with a section per connection. Every line
/// is retained in order — comments, blank lines, settings WinMux has never heard of — because these
/// are other people's configuration files and a writer that emitted only what it understood would
/// delete the rest of them (ADR 0025).
/// </para>
///
/// <para>
/// Not a general INI parser, and not trying to be: no quoting rules, no escapes, no continuation
/// lines. These two formats write <c>Key=Value</c> and nothing else, and inventing rules they do
/// not use is how a round trip stops being one.
/// </para>
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;

    private IniDocument(List<string> lines) => _lines = lines;

    public static IniDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new IniDocument([.. text.Split('\n').Select(line => line.TrimEnd('\r'))]);
    }

    public static IniDocument Load(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionSourceException($"{path} could not be read: {exception.Message}", exception);
        }
    }

    /// <summary>The section names, in the order the file has them.</summary>
    public IReadOnlyList<string> Sections =>
        [.. _lines.Select(NameOfSection).Where(name => name is not null).Select(name => name!)];

    /// <summary>A value in a section, or null when either is absent.</summary>
    public string? Read(string section, string key)
    {
        foreach (var line in LinesOf(section))
        {
            if (Split(line) is { } pair && pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>Every key and value in a section, for the settings a source passes through whole.</summary>
    public IReadOnlyDictionary<string, string> ReadAll(string section)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in LinesOf(section))
        {
            if (Split(line) is { } pair) values[pair.Key] = pair.Value;
        }

        return values;
    }

    /// <summary>
    /// Set a value, adding the key at the end of its section when it is not already there. A
    /// section that does not exist is added at the end of the file.
    /// </summary>
    public void Write(string section, string key, string value)
    {
        var bounds = BoundsOf(section);
        if (bounds is not { } range)
        {
            if (_lines.Count > 0 && _lines[^1].Length > 0) _lines.Add(string.Empty);
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        for (var i = range.Start; i < range.End; i++)
        {
            if (Split(_lines[i]) is { } pair && pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"{pair.Key}={value}";
                return;
            }
        }

        // After the last line that carries something, so a new key does not land after the blank
        // line that separates this section from the next.
        var at = range.End;
        while (at > range.Start && _lines[at - 1].Trim().Length == 0) at--;
        _lines.Insert(at, $"{key}={value}");
    }

    /// <summary>Rename a section, keeping everything in it. Returns false when it is not there.</summary>
    public bool RenameSection(string from, string to)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (NameOfSection(_lines[i]) is { } name && name.Equals(from, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = $"[{to}]";
                return true;
            }
        }

        return false;
    }

    public override string ToString() => string.Join(Environment.NewLine, _lines);

    public void Save(string path)
    {
        try
        {
            File.WriteAllText(path, ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConnectionSourceException($"{path} could not be written: {exception.Message}", exception);
        }
    }

    private IEnumerable<string> LinesOf(string section)
    {
        if (BoundsOf(section) is not { } range) yield break;
        for (var i = range.Start; i < range.End; i++) yield return _lines[i];
    }

    /// <summary>The lines after a section's header, up to the next header or the end of the file.</summary>
    private (int Start, int End)? BoundsOf(string section)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (NameOfSection(_lines[i]) is not { } name) continue;
            if (!name.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;

            var end = i + 1;
            while (end < _lines.Count && NameOfSection(_lines[end]) is null) end++;
            return (i + 1, end);
        }

        return null;
    }

    private static string? NameOfSection(string line)
    {
        var text = line.Trim();
        return text.Length > 1 && text[0] == '[' && text[^1] == ']' ? text[1..^1] : null;
    }

    private static (string Key, string Value)? Split(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text[0] is ';' or '#' or '[') return null;

        var at = text.IndexOf('=', StringComparison.Ordinal);
        return at <= 0 ? null : (text[..at].Trim(), text[(at + 1)..].Trim());
    }
}
