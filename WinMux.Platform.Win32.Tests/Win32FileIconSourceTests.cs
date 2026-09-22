using WinMux.Platform.Win32.Windows;

namespace WinMux.Platform.Win32.Tests;

/// <summary>
/// File icons against the real shell.
///
/// The property that matters most is the first: a file that does not exist on this machine still gets
/// its type's icon, because that is every file on an SFTP server.
/// </summary>
public sealed class Win32FileIconSourceTests
{
    private readonly Win32FileIconSource _icons = new();

    private static bool IsPng(byte[]? bytes) =>
        bytes is { Length: > 8 } && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    [Fact]
    public void A_file_that_exists_nowhere_still_gets_its_type_icon()
    {
        var png = _icons.GetIconPng("report-" + Guid.NewGuid().ToString("N") + ".txt", isDirectory: false, localPath: null);

        Assert.True(IsPng(png));
    }

    [Fact]
    public void A_folder_gets_the_folder_icon_and_it_differs_from_a_file()
    {
        var folder = _icons.GetIconPng("anything", isDirectory: true, localPath: null);
        var file = _icons.GetIconPng("anything.txt", isDirectory: false, localPath: null);

        Assert.True(IsPng(folder));
        Assert.NotEqual(folder, file);
    }

    [Fact]
    public void One_type_is_looked_up_once()
    {
        // The browser decodes each distinct array once, keyed by reference; a fresh array per call
        // would decode the same picture for every row.
        var first = _icons.GetIconPng("a.txt", isDirectory: false, localPath: null);
        var second = _icons.GetIconPng("b.txt", isDirectory: false, localPath: null);

        Assert.Same(first, second);
    }

    [Fact]
    public void Types_have_the_names_the_shell_gives_them()
    {
        // The exact words are localised, so assert only that a folder and a text file are named and
        // named differently — and that a remote file, which does not exist here, is named too.
        var folder = _icons.GetTypeName("anything", isDirectory: true);
        var text = _icons.GetTypeName("nowhere-" + Guid.NewGuid().ToString("N") + ".txt", isDirectory: false);

        Assert.False(string.IsNullOrWhiteSpace(folder));
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.NotEqual(folder, text);
    }

    [Fact]
    public void A_program_on_disk_gets_its_own_icon()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        Assert.True(File.Exists(notepad));

        var own = _icons.GetIconPng("notepad.exe", isDirectory: false, localPath: notepad);
        var generic = _icons.GetIconPng("notepad.exe", isDirectory: false, localPath: null);

        Assert.True(IsPng(own));
        Assert.NotEqual(own, generic);
    }
}
