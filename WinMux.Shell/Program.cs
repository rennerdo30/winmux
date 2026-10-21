using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using WinMux.Core.Layout;
using WinMux.Core;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Shell;

internal sealed class App : Application
{
    public DateTime ProcessStartUtc { get; init; }
    public bool RegisterAppPathsAfterOpen { get; init; }
    public SessionSnapshot Snapshot { get; init; } = null!;
    public string SessionPath { get; init; } = SessionFile.DefaultFileName;
    public WinMux.Shell.Keymap.KeymapConfiguration Keymap { get; init; } = WinMux.Shell.Keymap.KeymapConfiguration.TmuxDefaults();
    public bool Restored { get; init; }
    public SingleInstanceLaunch? LaunchService { get; init; }

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
            if (LaunchService is { } launches)
            {
                launches.SetHandler(request => session.DispatchLaunchAsync(request.Directory));
                launches.LaunchFailed += message => Dispatcher.UIThread.Post(() => windows[0].ShowMessage(message));
            }
            windows[0].Opened += (_, _) => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var startupMs = (DateTime.UtcNow - ProcessStartUtc).TotalMilliseconds;
                    _ = Task.Run(() => CrashLog.Write($"startup first window opened in {startupMs:0} ms", null));
                    if (RegisterAppPathsAfterOpen) _ = RegisterAppPathsAsync(windows[0]);
                    if (Restored)
                    {
                        // Not a dialog. Restoring is the normal path and it worked, and Windows
                        // does not interrupt for success — it interrupts for decisions. A modal
                        // here made the first action of every session "dismiss a paragraph about
                        // TOML fields", which is exactly how people learn to click OK unread, and
                        // then miss the one notice that matters.
                        var panes = windows.Sum(w => w.PaneCount);
                        windows[0].ShowMessage(
                            $"Session restored · {panes} pane{(panes == 1 ? "" : "s")} · terminal processes restarted; "
                            + "surviving application windows may have reattached");
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

    private static async Task RegisterAppPathsAsync(MainWindow window)
    {
        string? notice;
        try
        {
            notice = await Task.Run(() => ApplicationRegistration.RegisterCurrentExecutable(
                Environment.ProcessPath, PlatformServices.ApplicationRegistration));
        }
        catch (Exception ex)
        {
            notice = "WinMux could not register its app path: " + ex.Message;
        }
        if (notice is not null) window.ShowMessage(notice);
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] argv) =>
        Run(argv, CrashLog.Install, ShowStartupError, () => SingleInstanceLaunch.Open());

    // Keep startup ordering testable without installing process-wide hooks or showing native UI.
    internal static int Run(string[] argv, Action installCrashLog,
        Action<string, string> showStartupError, Func<SingleInstanceLaunch> openLaunch)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var processStartUtc = self.StartTime.ToUniversalTime();
        var launchDirectory = Environment.CurrentDirectory;

        ShellArguments arguments;
        try
        {
            arguments = ShellArguments.Parse(argv, launchDirectory);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            showStartupError("WinMux startup arguments are invalid", ex.Message);
            return 64;
        }
        SingleInstanceLaunch launch;
        try
        {
            launch = openLaunch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Threading.WaitHandleCannotBeOpenedException)
        {
            showStartupError("WinMux could not establish launch ownership", ex.Message);
            return 1;
        }
        using var launchLifetime = launch;
        if (launch.IsPrimary && !WinMuxDataDirectory.IsIsolated &&
            LegacyInstanceProbe.IsListeningAsync().GetAwaiter().GetResult())
        {
            showStartupError("An older WinMux is already running",
                "This running version does not support folder-launch forwarding. Close its windows safely " +
                "before starting the updated WinMux. Your existing session was left alone; no duplicate was started.");
            return 1;
        }
        if (!launch.IsPrimary)
        {
            LaunchRequest request;
            try
            {
                request = arguments.SecondaryRequest(launchDirectory);
            }
            catch (ArgumentException ex)
            {
                showStartupError("WinMux is already running", ex.Message);
                return 64;
            }
            var reply = launch.ForwardAsync(request).GetAwaiter().GetResult();
            if (!reply.Success) showStartupError("WinMux could not open this folder", reply.Message);
            return reply.Success ? 0 : 1;
        }
        // Only an accepted primary owns crash logging. A rejected warm invocation must not
        // install a ProcessExit hook that creates, appends or rotates user data after returning.
        installCrashLog();

        SessionLocation.Resolution location;
        try
        {
            location = SessionLocation.Resolve(
                arguments.SessionPath,
                [Environment.CurrentDirectory, AppContext.BaseDirectory]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            showStartupError("WinMux could not move the session to its new place", ex.Message);
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
            (snapshot, restored) = LoadStartupSession(sessionPath, arguments, launchDirectory);
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // A session file is user data. Refusing loudly beats starting empty over a layout we
            // could not understand (CLAUDE.md sections 4 and 8).
            Console.Error.WriteLine();
            Console.Error.WriteLine("  " + ex.Message);
            Console.Error.WriteLine();
            showStartupError("WinMux could not restore the session", ex.Message);
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
            showStartupError("WinMux could not load the keymap", ex.Message);
            return 2;
        }

        // Avalonia chooses its graphics backend before App.Initialize runs.
        Settings.ShellSettings.Load();
        _ = QueueStartup(arguments, restored, launch);
        return BuildAvaloniaApp(snapshot, sessionPath, keymap, restored, notices, launch,
            processStartUtc, !WinMuxDataDirectory.IsIsolated).StartWithClassicDesktopLifetime(argv);
    }

    private static AppBuilder BuildAvaloniaApp(
        SessionSnapshot snapshot,
        string sessionPath,
        Keymap.KeymapConfiguration keymap,
        bool restored,
        IReadOnlyList<string> notices,
        SingleInstanceLaunch launch,
        DateTime processStartUtc,
        bool registerAppPathsAfterOpen) =>
        AppBuilder.Configure(() => new App
            {
                Snapshot = snapshot, SessionPath = sessionPath, Keymap = keymap, Restored = restored, StartupNotices = notices,
                LaunchService = launch,
                ProcessStartUtc = processStartUtc,
                RegisterAppPathsAfterOpen = registerAppPathsAfterOpen,
            })
            .UsePlatformDetect()
            .With(RenderingOptions.Create(Settings.ShellSettings.Current.Rendering))
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
    private static SessionSnapshot DefaultSession(string here)
    {
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

    internal static Task<LaunchReply>? QueueStartup(
        ShellArguments arguments, bool restored, SingleInstanceLaunch launch)
    {
        // Restore alone is not a folder-open request. A fresh layout already contains one CMD.
        if (restored && arguments.OpenDirectory is { } directory)
            return launch.QueueInitial(new LaunchRequest(directory));
        return null;
    }

    internal static (SessionSnapshot Snapshot, bool Restored) LoadStartupSession(
        string sessionPath, ShellArguments arguments, string launchDirectory)
    {
        try { return (SessionFile.Load(sessionPath), true); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only absence permits a default layout; other failures must never overwrite user data.
            return (DefaultSession(arguments.OpenDirectory ?? launchDirectory), false);
        }
    }

    internal sealed record ShellArguments(string? SessionPath, string? KeymapPath, bool NoPrefix,
        string? OpenDirectory)
    {
        public LaunchRequest SecondaryRequest(string launchDirectory)
        {
            if (SessionPath is not null || KeymapPath is not null || NoPrefix)
                throw new ArgumentException(
                    "Session and keymap startup arguments cannot be applied to an existing WinMux. " +
                    "Open the session or settings inside WinMux, or close all WinMux windows before launching with these arguments.");
            return new LaunchRequest(OpenDirectory ?? launchDirectory);
        }

        public static ShellArguments Parse(string[] args, string? launchDirectory = null)
        {
            string? session = null;
            string? keymap = null;
            string? directory = null;
            var noPrefix = false;
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index].Equals("--no-prefix", StringComparison.OrdinalIgnoreCase))
                {
                    noPrefix = true;
                }
                else if (args[index].Equals("--keymap", StringComparison.OrdinalIgnoreCase))
                {
                    if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--keymap requires a JSON file path.");
                    keymap = args[index];
                }
                else if (args[index].Equals("--open-directory", StringComparison.OrdinalIgnoreCase))
                {
                    if (directory is not null) throw new ArgumentException("Specify --open-directory only once.");
                    if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--open-directory requires a folder path.");
                    directory = args[index];
                    if (string.IsNullOrWhiteSpace(directory))
                        throw new ArgumentException("--open-directory requires a non-empty folder path.");
                    try { directory = Path.GetFullPath(directory, launchDirectory ?? Environment.CurrentDirectory); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        throw new ArgumentException("--open-directory has an invalid folder path: " + ex.Message);
                    }
                    if (directory.StartsWith(@"\\", StringComparison.Ordinal))
                        throw new ArgumentException("--open-directory cannot use a UNC folder as CMD's working directory. Use a mapped drive.");
                    if (new LaunchRequest(directory).ValidationError() is { } error)
                        throw new ArgumentException(error);
                }
                else if (!args[index].StartsWith('-'))
                {
                    if (session is not null) throw new ArgumentException("Specify only one session file.");
                    session = args[index];
                }
                else throw new ArgumentException("Unknown startup argument: " + args[index]);
            }
            if (session is not null && directory is not null)
                throw new ArgumentException("--open-directory cannot be combined with an explicit session file.");
            return new ShellArguments(session, keymap, noPrefix, directory);
        }
    }
}
