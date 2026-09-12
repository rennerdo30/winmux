using System.Text;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Cli;

/// <summary>
/// `winmux` — the command line surface.
///
/// CLAUDE.md section 6 wants every action addressable from a CLI, because that is what makes the
/// product scriptable and testable. Session commands work on files; action commands are sent to
/// the running desktop shell over a current-user-only named pipe.
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
                "action" => InvokeAction(rest),
                "split" => DirectionalAction(rest, "split", "split-columns", "split-rows"),
                "focus" => FourWayAction(rest, "focus"),
                "resize" => FourWayAction(rest, "resize"),
                "close" or "close-pane" => InvokeNamedAction("close-pane"),
                "new-tab" => InvokeNamedAction("new-tab"),
                "next-tab" => InvokeNamedAction("next-tab"),
                "previous-tab" or "prev-tab" => InvokeNamedAction("previous-tab"),
                "save" or "save-session" => InvokeNamedAction("save-session"),
                "palette" => InvokeNamedAction("show-palette"),
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
        Console.WriteLine("  Note: this command only inspects the file; it does not change the running shell.");
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

    private static int InvokeAction(string[] args)
    {
        if (args.Length != 1)
        {
            Error("Usage: winmux action ACTION-NAME");
            return 64;
        }
        return InvokeNamedAction(args[0]);
    }

    private static int DirectionalAction(string[] args, string command, string columns, string rows)
    {
        if (args.Length != 1)
        {
            Error($"Usage: winmux {command} -h|-v");
            return 64;
        }

        return args[0].ToLowerInvariant() switch
        {
            "-h" or "--horizontal" or "columns" => InvokeNamedAction(columns),
            "-v" or "--vertical" or "rows" => InvokeNamedAction(rows),
            _ => DirectionError(command),
        };
    }

    private static int FourWayAction(string[] args, string prefix)
    {
        if (args.Length != 1)
        {
            Error($"Usage: winmux {prefix} left|right|up|down");
            return 64;
        }

        var direction = args[0].ToLowerInvariant();
        if (direction is not ("left" or "right" or "up" or "down"))
            return DirectionError(prefix);
        return InvokeNamedAction($"{prefix}-{direction}");
    }

    private static int DirectionError(string command)
    {
        Error($"Unknown direction for {command}.");
        return 64;
    }

    private static int InvokeNamedAction(string actionName)
    {
        var result = RemoteCommands.InvokeAsync(actionName).GetAwaiter().GetResult();
        if (result.Succeeded) Console.WriteLine(result.Message);
        else Error(result.Message);
        return result.ExitCode;
    }

    private static int Help()
    {
        Console.WriteLine("""

            winmux — window multiplexer for Windows

            USAGE
              winmux show [FILE]        draw the layout in a session file and list its panes
              winmux validate [FILE]    parse a session file; exit 0 if it loads, 1 with the reason
              winmux new [FILE]         write a starter session file
              winmux split -h|-v        split the focused pane in the running shell
              winmux focus DIRECTION    move focus: left, right, up or down
              winmux resize DIRECTION   resize the focused pane toward a direction
              winmux new-tab            add a tab beside the focused pane
              winmux next-tab           select the next tab
              winmux previous-tab       select the previous tab
              winmux close-pane         close the focused pane
              winmux save-session       write the running session
              winmux palette            open the command palette
              winmux action NAME        invoke any registered action by its stable name
              winmux help

            FILE defaults to session.toml in the current directory.

            Action commands require WinMux.exe to be running for the current user.

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
