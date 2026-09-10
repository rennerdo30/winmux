using System.Text;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Cli;

/// <summary>
/// `winmux` — the command line surface.
///
/// CLAUDE.md section 6 wants every action addressable from a CLI, because that is what makes the
/// product scriptable and testable. Today it can inspect and validate session files; it cannot
/// open panes, because there is no shell yet. It says so rather than implying otherwise.
/// </summary>
internal static class Program
{
    private static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var command = argv.Length > 0 ? argv[0].ToLowerInvariant() : "help";
        var rest = argv.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "show" => Show(rest),
                "validate" or "check" => Validate(rest),
                "new" or "demo" => New(rest),
                "help" or "--help" or "-h" or "/?" => Help(),
                _ => Unknown(command),
            };
        }
        catch (SessionFormatException ex)
        {
            // The whole point of the format work: say what is wrong, not "could not load".
            Error(ex.Message);
            return 1;
        }
        catch (FileNotFoundException ex)
        {
            Error("No such file: " + ex.FileName);
            return 2;
        }
        catch (DirectoryNotFoundException)
        {
            Error("That directory does not exist.");
            return 2;
        }
    }

    // ---------------- commands ----------------

    private static int Show(string[] args)
    {
        var path = args.FirstOrDefault() ?? SessionFile.DefaultFileName;
        var snapshot = SessionFile.Load(path);

        Console.WriteLine();
        Console.WriteLine($"  {Path.GetFullPath(path)}");
        Console.WriteLine($"  version {snapshot.Version}, saved {Stamp(snapshot.SavedAt)}, " +
                          $"{snapshot.Windows.Count} window{(snapshot.Windows.Count == 1 ? "" : "s")}");

        var width = Math.Clamp(SafeWindowWidth() - 4, 48, 100);

        foreach (var (window, index) in snapshot.Windows.Select((w, i) => (w, i)))
        {
            var tree = SessionMapper.FromSnapshot(window);
            var height = Math.Clamp(width * window.Bounds.Height / Math.Max(1, window.Bounds.Width) / 2, 8, 24);

            Console.WriteLine();
            Console.WriteLine($"  window {index}: \"{window.Title}\"   {window.Bounds}");
            Console.WriteLine();
            foreach (var line in AsciiLayout.Render(tree, width, height).TrimEnd().Split('\n'))
                Console.WriteLine("  " + line.TrimEnd());

            Console.WriteLine();
            PrintPanes(tree);
        }

        Console.WriteLine();
        Console.WriteLine("  Note: this inspects the file. WinMux cannot open these panes yet —");
        Console.WriteLine("  there is no shell. See HANDOFF.md for where that stands.");
        Console.WriteLine();
        return 0;
    }

    private static void PrintPanes(LayoutTree tree)
    {
        var arranged = tree.Arrange(new Rect(0, 0, tree.Bounds.Width, tree.Bounds.Height));

        foreach (var pane in tree.Panes)
        {
            var marker = pane.Id == tree.Focused ? "*" : " ";
            var visible = arranged.IsVisible(pane.Id) ? arranged[pane.Id].ToString() : "hidden (inactive tab)";
            Console.WriteLine($"  {marker} {pane.Title,-14} {pane.Kind,-12} {visible}");

            var r = pane.Restore;
            if (!string.IsNullOrEmpty(r.Program))
                Console.WriteLine($"      program  {r.Program}{(r.Args.Count > 0 ? " " + string.Join(" ", r.Args) : "")}");
            if (r.Cwd.IsKnown)
                Console.WriteLine($"      cwd      {r.Cwd.Path}   ({Describe(r.Cwd.Source)})");
            if (pane.Kind == PaneKind.ForeignApp)
                Console.WriteLine($"      hosting  {r.Strategy.ToString().ToLowerInvariant()}" +
                                  (r.Extras.TryGetValue("window_class", out var wc) ? $", window class {wc}" : ""));
        }
    }

    /// <summary>Plain words for how a working directory was obtained — ADR 0004's whole point.</summary>
    private static string Describe(CwdSource source) => source switch
    {
        CwdSource.ShellReported => "reported by the shell, most reliable",
        CwdSource.ProcessDeepest => "read from the deepest child process",
        CwdSource.ProcessRoot => "read from the pane process; stale for PowerShell",
        CwdSource.LaunchDirectory => "where it was launched; wrong once the user moves",
        _ => "unknown",
    };

    private static int Validate(string[] args)
    {
        var path = args.FirstOrDefault() ?? SessionFile.DefaultFileName;

        // Deserialize, not Load: quarantining a copy is right when the app is starting up and
        // about to lose a session, and wrong when the user has deliberately asked "is this valid?".
        // Checking a file should not litter the directory with .corrupt- copies.
        if (!File.Exists(path)) throw new FileNotFoundException(null, path);
        var snapshot = SessionFile.Deserialize(File.ReadAllText(path));

        var panes = snapshot.Windows.Sum(w => SessionMapper.FromSnapshot(w).Panes.Count());
        Console.WriteLine($"OK  {Path.GetFullPath(path)}");
        Console.WriteLine($"    version {snapshot.Version}, {snapshot.Windows.Count} window(s), {panes} pane(s)");
        return 0;
    }

    private static int New(string[] args)
    {
        var path = args.FirstOrDefault() ?? SessionFile.DefaultFileName;
        if (File.Exists(path))
        {
            Error($"{path} already exists. Refusing to overwrite a session file.");
            return 3;
        }

        var cwd = new WorkingDirectory(Directory.GetCurrentDirectory(), CwdSource.LaunchDirectory, DateTimeOffset.UtcNow);
        var shell = Pane.Terminal("shell", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", cwd);
        var second = Pane.Terminal("second", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", cwd);

        var tree = new LayoutTree(shell) { Bounds = new Rect(0, 0, 1920, 1040) };
        tree.Split(shell.Id, SplitDirection.Columns, second);
        tree.Focus(shell.Id);

        SessionFile.Save(path, new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, "new session")],
        });

        Console.WriteLine($"wrote {Path.GetFullPath(path)}");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""

            winmux — window multiplexer for Windows

            USAGE
              winmux show [FILE]        draw the layout in a session file and list its panes
              winmux validate [FILE]    parse a session file; exit 0 if it loads, 1 with the reason
              winmux new [FILE]         write a starter session file
              winmux help

            FILE defaults to session.toml in the current directory.

            WHAT THIS CANNOT DO YET
              Open panes. There is no shell, no pty and no window hosting — those are the next
              pieces of Phase 1. Today the CLI reads and writes the session format only.

            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Error($"Unknown command \"{command}\".");
        Help();
        return 64;
    }

    // ---------------- plumbing ----------------

    private static void Error(string message)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine("  " + message.Replace("\n", "\n  ", StringComparison.Ordinal));
            Console.Error.WriteLine();
        }
        finally { Console.ForegroundColor = previous; }
    }

    private static string Stamp(DateTimeOffset when) =>
        when == default ? "(never)" : when.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Console width, tolerating a redirected or absent console.</summary>
    private static int SafeWindowWidth()
    {
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 100; }
        catch (IOException) { return 100; }
        catch (PlatformNotSupportedException) { return 100; }
    }
}
