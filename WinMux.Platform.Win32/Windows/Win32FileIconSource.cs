using System.Runtime.InteropServices;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// File and folder icons from the shell's system image lists — the same pictures Explorer draws.
///
/// <para>
/// Almost every answer comes from the name alone: <c>SHGetFileInfo</c> with
/// <c>SHGFI_USEFILEATTRIBUTES</c> is told "a file called <c>x.pdf</c>" or "a folder" and returns the
/// registered icon for that type without opening anything. That is what makes an SFTP listing look
/// like a local one, and it is why the cache is keyed by extension: a folder of a thousand
/// photographs costs one lookup, not a thousand.
/// </para>
///
/// <para>
/// The exception is a file that carries its own picture — a program, a shortcut, an icon file. For
/// those only the file itself can answer, so a local path is asked about directly and cached by path.
/// </para>
/// </summary>
public sealed class Win32FileIconSource : IFileIconSource
{
    private const uint UseFileAttributes = 0x10;
    private const uint DirectoryAttribute = 0x10;
    private const uint NormalAttribute = 0x80;

    /// <summary>Types whose icon is inside each file rather than registered for the type.</summary>
    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".appref-ms",
    };

    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _typeNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[]? GetIconPng(string name, bool isDirectory, string? localPath, int size = 32)
    {
        if (!IsAvailable || string.IsNullOrEmpty(name)) return null;

        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var ownIcon = !isDirectory && localPath is not null && OwnIcon.Contains(extension);
        var key = ownIcon ? $"path|{localPath}|{size}" : $"{(isDirectory ? "<dir>" : extension)}|{size}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var png = ownIcon
            ? Extract(localPath!, 0, 0, size) ?? ForType(extension, isDirectory, size)
            : ForType(extension, isDirectory, size);

        lock (_gate)
        {
            _cache[key] = png;
        }

        return png;
    }

    public string? GetTypeName(string name, bool isDirectory)
    {
        if (!IsAvailable || string.IsNullOrEmpty(name)) return null;

        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        var key = isDirectory ? "type|<dir>" : $"type|{extension}";
        lock (_gate)
        {
            if (_typeNames.TryGetValue(key, out var cached)) return cached;
        }

        string? typeName;
        try
        {
            typeName = Win32AppIconSource.TypeName(
                isDirectory ? "folder" : "file" + extension,
                isDirectory ? DirectoryAttribute : NormalAttribute);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException)
        {
            typeName = null;
        }

        lock (_gate)
        {
            _typeNames[key] = typeName;
        }

        return typeName;
    }

    private static byte[]? ForType(string extension, bool isDirectory, int size) =>
        // The name only has to carry the extension; the shell never looks for the file.
        Extract(
            isDirectory ? "folder" : "file" + extension,
            isDirectory ? DirectoryAttribute : NormalAttribute,
            UseFileAttributes,
            size);

    private static byte[]? Extract(string path, uint attributes, uint flags, int size)
    {
        try
        {
            foreach (var list in Win32AppIconSource.PreferredLists(size))
            {
                if (Win32AppIconSource.TryFromImageList(path, list, attributes, flags, out var png)) return png;
            }

            return null;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or ArgumentException or ExternalException)
        {
            // A missing icon is an ordinary answer; the browser draws a glyph instead.
            return null;
        }
    }
}
