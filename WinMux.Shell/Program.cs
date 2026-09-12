using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Shell;

internal sealed class App : Application
{
    public SessionSnapshot Snapshot { get; init; } = null!;
    public string SessionPath { get; init; } = SessionFile.DefaultFileName;
    public WinMux.Shell.Keymap.KeymapConfiguration Keymap { get; init; } = WinMux.Shell.Keymap.KeymapConfiguration.TmuxDefaults();
    public bool Restored { get; init; }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = new SessionController(SessionPath);
            var windows = Snapshot.Windows
                .Select(saved => new MainWindow(SessionMapper.FromSnapshot(saved), session, Keymap, saved.Title))
                .ToArray();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = windows[0];
            foreach (var window in windows.Skip(1)) window.Show();
            session.StartCommandServer();
            windows[0].Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (Restored)
                    {
                        await new NoticeWindow(
                            "WinMux session restored",
                            "The saved windows, panes, programs, arguments, environment overrides, and working directories were restored. " +
                            "Processes were started again; running jobs and in-memory TUI state cannot be resumed.")
                            .ShowDialog(windows[0]);
                    }

                    await windows[0].ShowCwdIntegrationAsync(onlyIfUnseen: true);
                }
                catch (InvalidOperationException ex)
                {
                    windows[0].ShowMessage("startup notice could not be shown: " + ex.Message);
                }
            });
            desktop.Exit += (_, _) => session.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        ShellArguments arguments;
        try
        {
            arguments = ShellArguments.Parse(argv);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 64;
        }
        string sessionPath = arguments.SessionPath ?? SessionFile.DefaultFileName;

        SessionSnapshot snapshot;
        bool restored;
        try
        {
            snapshot = SessionFile.Load(sessionPath);
            restored = true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Missing is the only safe reason to create a new session. File.Exists also returns
            // false for access failures, which could let a later autosave overwrite real data.
            snapshot = DefaultSession();
            restored = false;
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // A session file is user data. Refusing loudly beats starting empty over a layout we
            // could not understand (CLAUDE.md sections 4 and 8).
            Console.Error.WriteLine();
            Console.Error.WriteLine("  " + ex.Message);
            Console.Error.WriteLine();
            ShowStartupError("WinMux could not restore the session", ex.Message);
            return 1;
        }

        Keymap.KeymapConfiguration keymap;
        try
        {
            keymap = arguments.NoPrefix
                ? Keymap.KeymapConfiguration.NoPrefixDefaults()
                : arguments.KeymapPath is { } path
                    ? Keymap.KeymapConfiguration.Load(path)
                    : Keymap.KeymapConfiguration.TmuxDefaults();
            _ = new Keymap.KeyBindingTable(keymap);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not load keymap: {ex.Message}");
            ShowStartupError("WinMux could not load the keymap", ex.Message);
            return 2;
        }

        return BuildAvaloniaApp(snapshot, sessionPath, keymap, restored).StartWithClassicDesktopLifetime(argv);
    }

    private static AppBuilder BuildAvaloniaApp(
        SessionSnapshot snapshot,
        string sessionPath,
        Keymap.KeymapConfiguration keymap,
        bool restored) =>
        AppBuilder.Configure(() => new App { Snapshot = snapshot, SessionPath = sessionPath, Keymap = keymap, Restored = restored })
            .UsePlatformDetect()
            .LogToTrace();

    private static void ShowStartupError(string title, string message)
    {
        if (OperatingSystem.IsWindows() && Environment.UserInteractive)
        {
            _ = Win32Interop.MessageBoxW(IntPtr.Zero, message, title, Win32Interop.MB_OK | Win32Interop.MB_ICONERROR);
        }
    }

    /// <summary>
    /// With no session file: a shell and File Explorer side by side on the current directory —
    /// the smallest layout that exercises both a terminal pane and a hosted foreign application.
    /// </summary>
    private static SessionSnapshot DefaultSession()
    {
        var here = Environment.CurrentDirectory;
        var cwd = new WorkingDirectory(here, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow);

        var shell = Pane.Terminal("cmd", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", cwd);

        var explorer = new Pane(PaneId.New(), PaneKind.ForeignApp, "Explorer", new RestoreDescriptor
        {
            Kind = PaneKind.ForeignApp,
            Title = "Explorer",
            Program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            Args = [here],
            Cwd = cwd,
            Strategy = HostStrategy.Embed,
            Extras = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // Measured in spike 2: explorer.exe exits at once and the window belongs to the
                // already-running shell, so it is found by class, never by the launched pid.
                ["window_class"] = "CabinetWClass",
                ["launch_delay_ms"] = "1500",
            },
        });

        var tree = new LayoutTree(shell);
        tree.Split(shell.Id, SplitDirection.Columns, explorer, ratio: 0.5);
        tree.Focus(shell.Id);
        return new SessionSnapshot
        {
            SavedAt = DateTimeOffset.UtcNow,
            Windows = [SessionMapper.ToSnapshot(tree, "WinMux")],
        };
    }

    private sealed record ShellArguments(string? SessionPath, string? KeymapPath, bool NoPrefix)
    {
        public static ShellArguments Parse(string[] args)
        {
            string? session = null;
            string? keymap = null;
            var noPrefix = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--no-prefix", StringComparison.OrdinalIgnoreCase))
                {
                    noPrefix = true;
                }
                else if (args[index].Equals("--keymap", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= args.Length) throw new ArgumentException("--keymap requires a JSON file path.");
                    keymap = args[index];
                }
                else if (!args[index].StartsWith('-') && session is null)
                {
                    session = args[index];
                }
            }
            return new ShellArguments(session, keymap, noPrefix);
        }
    }
}
