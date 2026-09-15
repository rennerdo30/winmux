using System.Runtime.InteropServices;
using System.Text;

namespace WinMux.Platform.Win32.Windows;

/// <summary>
/// The Start Menu, resolved.
///
/// Windows has no API that says "list the installed applications" — the Start Menu is the closest
/// thing to an answer, and it is a tree of <c>.lnk</c> files that have to be opened through COM to
/// learn what they point at. Both Start Menu roots are read: the machine-wide one under ProgramData
/// and the per-user one under AppData, because plenty of things install to only one of them.
///
/// Shortcuts that resolve to nothing runnable are dropped rather than shown: a picker offering an
/// entry that cannot start is worse than a shorter list. Uninstallers and help files are dropped
/// for the same reason — nobody wants to host an uninstaller in a pane.
/// </summary>
public sealed class Win32AppCatalog : IAppCatalog
{
    private static readonly string[] SkipWords =
        ["uninstall", "uninstaller", "readme", "read me", "release notes", "help", "documentation", "website", "home page"];

    public IReadOnlyList<InstalledApp> List(out string? error)
    {
        var problems = new List<string>();
        var found = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, source) in Roots())
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{root}: {ex.Message}");
                continue;
            }

            foreach (var shortcut in Safely(shortcuts, problems))
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (ShouldSkip(name)) continue;

                var app = ReadShortcut(shortcut, name, source);
                if (app is not { } resolved) continue;

                // Keyed by target so the same program reached from both Start Menus appears once.
                var key = resolved.Program + "|" + resolved.Arguments;
                if (!found.ContainsKey(key)) found[key] = resolved;
            }
        }

        error = problems.Count == 0 ? null : string.Join("; ", problems);
        return found.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public InstalledApp? Resolve(string path, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path)) { error = "no path given"; return null; }

        var name = Path.GetFileNameWithoutExtension(path);
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) { error = $"{path} does not exist"; return null; }
            return new InstalledApp(name, Path.GetFullPath(path), string.Empty, string.Empty, "chosen");
        }

        var app = ReadShortcut(path, name, "chosen");
        if (app is null) error = $"{path} does not point at a program WinMux can launch";
        return app;
    }

    private static IEnumerable<(string Root, string Source)> Roots()
    {
        yield return (Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), "Start Menu");
        yield return (Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), "Start Menu (you)");
    }

    /// <summary>
    /// Enumerate lazily but survive a directory that denies access partway through, which
    /// <c>EnumerateFiles</c> signals by throwing mid-iteration rather than up front.
    /// </summary>
    private static IEnumerable<string> Safely(IEnumerable<string> files, List<string> problems)
    {
        using var enumerator = files.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext()) yield break;
                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(ex.Message);
                yield break;
            }

            yield return current;
        }
    }

    private static bool ShouldSkip(string name) =>
        SkipWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static InstalledApp? ReadShortcut(string shortcutPath, string name, string source)
    {
        try
        {
            var link = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            ((NativeMethods.IPersistFile)link).Load(shortcutPath, 0);

            var target = new StringBuilder(NativeMethods.MaxPath);
            var data = new NativeMethods.Win32FindDataW();
            link.GetPath(target, target.Capacity, ref data, NativeMethods.SlgpRawPath);
            var program = Environment.ExpandEnvironmentVariables(target.ToString()).Trim();

            // A Start Menu entry may point at a document, a URL, or a Store app with no path at all.
            // Only an executable can be hosted in a pane.
            if (program.Length == 0 || !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(program)) return null;

            var arguments = new StringBuilder(NativeMethods.MaxPath);
            link.GetArguments(arguments, arguments.Capacity);

            var workingDirectory = new StringBuilder(NativeMethods.MaxPath);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

            return new InstalledApp(
                name,
                program,
                Environment.ExpandEnvironmentVariables(arguments.ToString()).Trim(),
                Environment.ExpandEnvironmentVariables(workingDirectory.ToString()).Trim(),
                source);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            // One unreadable shortcut is not worth losing the catalogue over.
            return null;
        }
    }

    private static class NativeMethods
    {
        internal const int MaxPath = 260;

        /// <summary>Read the path exactly as stored, without letting the shell "fix" it for us.</summary>
        internal const uint SlgpRawPath = 0;

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        internal class ShellLink
        {
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellLinkW
        {
            void GetPath(
                [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
                int maxPath,
                ref Win32FindDataW data,
                uint flags);

            void GetIDList(out nint list);
            void SetIDList(nint list);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(nint window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPersistFile
        {
            void GetClassID(out Guid classId);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Win32FindDataW
        {
            internal uint FileAttributes;
            internal long CreationTime;
            internal long LastAccessTime;
            internal long LastWriteTime;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint Reserved0;
            internal uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
            internal string FileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            internal string AlternateFileName;
        }
    }
}
