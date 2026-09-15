using WinMux.Core.Model;
using WinMux.Core.Settings;

namespace WinMux.Shell.Panes;

/// <summary>
/// Turns a profile into a pane.
///
/// The one place that knows how a <see cref="LaunchProfile"/> becomes a <see cref="Pane"/>, so the
/// toolbar, the palette, an empty pane's launcher and the CLI all produce exactly the same pane for
/// the same profile. A profile selects a provider and tells it what to run; it is never a new pane
/// kind (CLAUDE.md section 5a).
/// </summary>
internal static class ProfilePaneFactory
{
    public static Pane Create(LaunchProfile profile, WorkingDirectory? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var cwd = !string.IsNullOrWhiteSpace(profile.WorkingDirectory)
            ? new WorkingDirectory(profile.WorkingDirectory, CwdSource.LaunchDirectory, DateTimeOffset.UtcNow)
            : inherited ?? WorkingDirectory.None;

        // A connection is resolved into a program and arguments first, so everything below treats
        // it exactly like any other profile — one code path for launching, whatever the source.
        if (RemoteConnection.IsConnection(profile.Kind))
        {
            var command = RemoteConnection.Resolve(profile);
            profile = profile with { Program = command.Program, Args = command.Args };
        }

        return profile.PaneKind == PaneKind.Terminal
            ? Terminal(profile, cwd)
            : Application(profile, cwd);
    }

    private static Pane Terminal(LaunchProfile profile, WorkingDirectory cwd)
    {
        var resolved = ExecutablePathResolver.Resolve(profile.Program);
        return new Pane(PaneId.New(), PaneKind.Terminal, profile.Name, new RestoreDescriptor
        {
            Kind = PaneKind.Terminal,
            Title = profile.Name,
            Program = resolved.Succeeded ? resolved.AbsolutePath : profile.Program,
            Args = profile.Args,
            Cwd = cwd,
        });
    }

    private static Pane Application(LaunchProfile profile, WorkingDirectory cwd)
    {
        var resolved = ExecutablePathResolver.Resolve(profile.Program);

        // The window-selection rules travel in Extras, which is where the foreign-app provider
        // already looks (ADR 0011). A profile without them falls back to the shipped quirks
        // database, which is the right default for anything the database already knows.
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(profile.WindowClass))
        {
            extras["window_class"] = profile.WindowClass;
            extras["window_match"] = "class";
        }
        if (!string.IsNullOrWhiteSpace(profile.TitleContains))
            extras["window_title_contains"] = profile.TitleContains;

        return new Pane(PaneId.New(), PaneKind.ForeignApp, profile.Name, new RestoreDescriptor
        {
            Kind = PaneKind.ForeignApp,
            Title = profile.Name,
            Program = resolved.Succeeded ? resolved.AbsolutePath : profile.Program,
            Args = profile.Args,
            Cwd = cwd,
            Strategy = profile.Strategy,
            Extras = extras,
        });
    }
}
