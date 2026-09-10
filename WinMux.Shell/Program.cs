using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using WinMux.Core.Layout;
using WinMux.Core.Model;
using WinMux.Core.Session;

namespace WinMux.Shell;

internal sealed class App : Application
{
    public LayoutTree Tree { get; init; } = null!;
    public string SessionPath { get; init; } = SessionFile.DefaultFileName;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Tree, SessionPath);
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        string sessionPath = argv.FirstOrDefault(a => !a.StartsWith('-')) ?? SessionFile.DefaultFileName;

        LayoutTree tree;
        try
        {
            tree = File.Exists(sessionPath)
                ? SessionMapper.FromSnapshot(SessionFile.Load(sessionPath).Windows[0])
                : DefaultSession();
        }
        catch (SessionFormatException ex)
        {
            // A session file is user data. Refusing loudly beats starting empty over a layout we
            // could not understand (CLAUDE.md sections 4 and 8).
            Console.Error.WriteLine();
            Console.Error.WriteLine("  " + ex.Message);
            Console.Error.WriteLine();
            return 1;
        }

        return BuildAvaloniaApp(tree, sessionPath).StartWithClassicDesktopLifetime(argv);
    }

    private static AppBuilder BuildAvaloniaApp(LayoutTree tree, string sessionPath) =>
        AppBuilder.Configure(() => new App { Tree = tree, SessionPath = sessionPath })
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// With no session file: a shell and File Explorer side by side on the current directory —
    /// the smallest layout that exercises both a terminal pane and a hosted foreign application.
    /// </summary>
    private static LayoutTree DefaultSession()
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
            Strategy = HostStrategy.Attach,
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
        return tree;
    }
}
