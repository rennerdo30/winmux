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

    /// <summary>What startup has to say before anything else — an update's outcome, a moved session.</summary>
    public IReadOnlyList<string> StartupNotices { get; init; } = [];

    private Avalonia.Platform.PlatformColorValues? _platformColors;

    private void ApplyTheme()
    {
        if (_platformColors is not { } colors) return;
        Chrome.Palette.Apply(colors);
        RequestedThemeVariant = Chrome.Palette.IsDark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }

    public override void Initialize()
    {
        CrashLog.WatchDispatcher();
        Styles.Add(new FluentTheme());

        // Follow the system. An app that ignores the light/dark setting and picks its own accent
        // reads as foreign on Windows 11 before anyone looks at a single control.
        //
        // The variant is set *explicitly* rather than left at ThemeVariant.Default. Default did not
        // pick up the platform here: Fluent kept resolving its light control colours while our own
        // brushes painted dark, so every templated control — the active tab, the dialogs — came out
        // light-on-dark. Asking Windows and saying the answer out loud is unambiguous.
        Settings.ShellSettings.Load();
        Settings.ShellProfiles.Load();
        Chrome.Palette.Preference = Settings.ShellSettings.Current.Theme;

        var settings = PlatformSettings;
        if (settings is not null)
        {
            _platformColors = settings.GetColorValues();
            ApplyTheme();
            settings.ColorValuesChanged += (_, values) => Dispatcher.UIThread.Post(() =>
            {
                _platformColors = values;
                ApplyTheme();
            });
        }

        // A change to the theme preference has to repaint against the *platform's* colours, which
        // are the accent and the system variant — so they are kept rather than re-queried.
        Settings.ShellSettings.Changed += () =>
        {
            Chrome.Palette.Preference = Settings.ShellSettings.Current.Theme;
            ApplyTheme();
        };

        // After Fluent, so the chrome's own styles win where they overlap.
        Styles.Add(Chrome.Theme.Build());

    }

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
                        // Not a dialog. Restoring is the normal path and it worked, and Windows
                        // does not interrupt for success — it interrupts for decisions. A modal
                        // here made the first action of every session "dismiss a paragraph about
                        // TOML fields", which is exactly how people learn to click OK unread, and
                        // then miss the one notice that matters.
                        var panes = windows.Sum(w => w.PaneCount);
                        windows[0].ShowMessage(
                            $"Session restored · {panes} pane{(panes == 1 ? "" : "s")} · programs were restarted, "
                            + "so anything that was running has to be started again");
                    }

                    // After the restore line, so these are what is left on screen: each is something
                    // the user needs to know once, and the status line is where WinMux says things.
                    foreach (var notice in StartupNotices) windows[0].ShowMessage(notice);

                    await windows[0].ShowCwdIntegrationAsync(onlyIfUnseen: true);
                }
                catch (InvalidOperationException ex)
                {
                    windows[0].ShowMessage("startup notice could not be shown: " + ex.Message);
                }
            });
            desktop.Exit += (_, _) => session.Dispose();
            desktop.Exit += (_, _) => PlatformServices.ShutDownNotifications();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        CrashLog.Install();

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
        SessionLocation.Resolution location;
        try
        {
            location = SessionLocation.Resolve(
                arguments.SessionPath,
                [Environment.CurrentDirectory, AppContext.BaseDirectory]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStartupError("WinMux could not move the session to its new place", ex.Message);
            return 1;
        }

        string sessionPath = location.Path;
        var notices = new List<string>();
        if (location.MigratedFrom is { } oldPlace)
        {
            notices.Add($"Your session now lives in {sessionPath}; the old copy at {oldPlace} was left where it was");
        }

        if (Update.UpdateInstaller.CollectResult() is { } updated) notices.Add(updated);

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

        return BuildAvaloniaApp(snapshot, sessionPath, keymap, restored, notices).StartWithClassicDesktopLifetime(argv);
    }

    private static AppBuilder BuildAvaloniaApp(
        SessionSnapshot snapshot,
        string sessionPath,
        Keymap.KeymapConfiguration keymap,
        bool restored,
        IReadOnlyList<string> notices) =>
        AppBuilder.Configure(() => new App
            {
                Snapshot = snapshot, SessionPath = sessionPath, Keymap = keymap, Restored = restored, StartupNotices = notices,
            })
            .UsePlatformDetect()
            .LogToTrace();

    private static void ShowStartupError(string title, string message)
    {
        // Startup failed before Avalonia exists, so this cannot be a normal dialog. It still has to
        // say something: a WinExe that dies silently has written its error to a console nobody sees.
        if (Environment.UserInteractive) PlatformServices.Notifier.ShowError(title, message);
    }

    /// <summary>
    /// With no session file: a shell and the built-in file browser side by side on the current
    /// directory — the smallest layout that exercises Phase 4's terminal handoff workflow.
    /// </summary>
    private static SessionSnapshot DefaultSession()
    {
        var here = Environment.CurrentDirectory;
        var cwd = new WorkingDirectory(here, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow);

        var shell = Pane.Terminal("cmd", Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", cwd);

        var files = Pane.FileBrowser(here);

        var tree = new LayoutTree(shell);
        tree.Split(shell.Id, SplitDirection.Columns, files, ratio: 0.5);
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
