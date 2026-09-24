using Avalonia.VisualTree;
using Avalonia.Controls;
using WinMux.Core.Model;
using WinMux.Core.Settings;
using WinMux.Panes;
using WinMux.Platform;
using WinMux.Shell.FileBrowser;
using WinMux.Shell.Panes;

namespace WinMux.Shell.Tests;

/// <summary>
/// Every pane provider, actually constructed.
///
/// <para>
/// This is the gap that produced most of one day's bugs. Nothing in the suite built a provider, so
/// anything in the wiring between a provider and its controls could only fail in front of a person:
/// a <c>KeyGesture.Parse("Del")</c> that throws made the file-browser pane impossible to create at
/// all and shipped; the empty pane's launcher lost half the product; two text-layout faults went
/// out. All of it compiled.
/// </para>
///
/// <para>
/// The bar here is deliberately low and deliberately broad — <em>can this be built, and does it say
/// what it promises</em> — because that is the class of failure that was escaping. A pane whose view
/// cannot be constructed is not a subtle bug, and it should not take a screenshot to find.
/// </para>
/// </summary>
public sealed class PaneProviderConstructionTests
{
    private static PaneProviderContext ContextFor(PaneKind kind, IReadOnlyDictionary<string, string>? extras = null) =>
        new(PaneId.New(), "test", new RestoreDescriptor
        {
            Kind = kind,
            Title = "test",
            Extras = extras ?? new Dictionary<string, string>(StringComparer.Ordinal),
        });

    private static EmptyPaneCommands NoCommands() => new(
        (_, _) => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, (_, _) => { }, _ => { }, () => { });

    private static readonly LaunchProfile[] SomeProfiles =
    [
        new() { Id = "cmd", Name = "Command Prompt", Program = @"C:\WINDOWS\system32\cmd.exe" },
        new() { Id = "explorer", Name = "File Explorer", Program = @"C:\WINDOWS\explorer.exe", Kind = ProfileKind.Application },
        new() { Id = "box", Name = "the build box", Program = "", Kind = ProfileKind.Sftp, Host = "build.example.com" },
    ];

    /// <summary>Icons are a platform call; a test must not depend on what this machine has installed.</summary>
    private sealed class NoIcons : IAppIconSource
    {
        public bool IsAvailable => false;
        public byte[]? GetIconPng(string programPath, int size = 32) => null;
    }

    // ---- the one that shipped broken -------------------------------------------------

    [Fact]
    public Task A_file_browser_pane_can_be_created() => Headless.RunAsync(async () =>
    {
        // KeyGesture.Parse("Del") threw out of here, so the pane could not be created at all and the
        // shell reported "could not create file-browser pane: Requested value 'Del' was not found".
        // One construction is all it would have taken.
        var provider = new FileBrowserPaneProvider(() => Path.GetTempPath());

        var runtime = await provider.CreateAsync(ContextFor(PaneKind.FileBrowser));

        Assert.NotNull(runtime.View);
        Assert.Equal(PaneKind.FileBrowser, runtime.Kind);
        await runtime.DisposeAsync();
    });

    [Fact]
    public Task Clicking_the_empty_part_of_a_file_browser_focuses_it() => Headless.RunAsync(async () =>
    {
        // Pane focus follows keyboard focus into a pane's view. A click below the last row of the
        // list landed on nothing focusable, so the pane never became the focused one and the Ctrl+V
        // that followed went to the pane on the other side of the window. Seen on screen, 2026-09-23.
        // Its own empty directory: the shared temp folder fills up with other tests' files while the
        // suite runs, and a row under the pointer would make this a different test.
        var empty = Directory.CreateTempSubdirectory("winmux-focus-").FullName;
        var provider = new FileBrowserPaneProvider(() => empty);
        var runtime = await provider.CreateAsync(ContextFor(PaneKind.FileBrowser));
        var elsewhere = new TextBox();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        Grid.SetColumn(runtime.View, 1);
        grid.Children.Add(elsewhere);
        grid.Children.Add(runtime.View);
        var window = new Window { Width = 800, Height = 600, Content = grid };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        elsewhere.Focus();
        Assert.False(runtime.View.IsKeyboardFocusWithin);

        // Near the bottom of the right-hand column: below any row, above the status line.
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(
            window, new Avalonia.Point(600, 520), Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(
            window, new Avalonia.Point(600, 520), Avalonia.Input.MouseButton.Left);

        var list = runtime.View.GetVisualDescendants().OfType<ListBox>().First();
        Assert.True(runtime.View.IsKeyboardFocusWithin,
            $"focusable={list.Focusable} focused={window.FocusManager?.GetFocusedElement()}");
        window.Close();
        await runtime.DisposeAsync();
        Directory.Delete(empty, recursive: true);
    });

    [Fact]
    public Task A_file_browser_pane_can_be_restored() => Headless.RunAsync(async () =>
    {
        // Restore is a separate entry point, taking a descriptor that has been round-tripped.
        var provider = new FileBrowserPaneProvider(() => Path.GetTempPath());
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["current_directory"] = Path.GetTempPath(),
        };

        var runtime = await provider.RestoreAsync(ContextFor(PaneKind.FileBrowser, extras));

        Assert.NotNull(runtime.View);
        await runtime.DisposeAsync();
    });

    // ---- the launcher ----------------------------------------------------------------

    [Fact]
    public Task An_empty_pane_can_be_created() => Headless.RunAsync(async () =>
    {
        var provider = new EmptyPaneProvider(NoCommands(), () => SomeProfiles, new NoIcons(), () => []);

        var runtime = await provider.CreateAsync(ContextFor(PaneKind.Empty));

        Assert.NotNull(runtime.View);
        await runtime.DisposeAsync();
    });

    [Fact]
    public Task The_launcher_offers_every_profile_and_every_connection_kind() => Headless.RunAsync(async () =>
    {
        // The launcher lost the file browser, then every provider kind, then the connections — each
        // time silently. Reading the text back is the cheapest guard against the next one.
        var provider = new EmptyPaneProvider(
            NoCommands(),
            () => SomeProfiles,
            new NoIcons(),
            () => [(PaneKind.FileBrowser, "File browser"), (PaneKind.Create("com.example.clock"), "Clock")]);

        var runtime = await provider.CreateAsync(ContextFor(PaneKind.Empty));
        var text = AllText(runtime.View);

        foreach (var profile in SomeProfiles)
        {
            Assert.Contains(profile.Name, text);
        }

        Assert.Contains("File browser", text);
        Assert.Contains("Clock", text);

        // Every connection kind, by the name a user would look for.
        Assert.Contains("SSH", text);
        Assert.Contains("Remote Desktop", text);
        Assert.Contains("SFTP", text);
        Assert.Contains("FTP", text);

        Assert.Contains("Choose an application", text);
        Assert.Contains("Attach an open window", text);

        await runtime.DisposeAsync();
    });

    [Fact]
    public Task A_provider_kind_is_shown_under_the_name_it_gives_itself() => Headless.RunAsync(async () =>
    {
        // DisplayName exists so an external provider is not listed as "com.example.clock".
        var provider = new EmptyPaneProvider(
            NoCommands(),
            () => [],
            new NoIcons(),
            () => [(PaneKind.Create("com.example.clock"), "Clock")]);

        var runtime = await provider.CreateAsync(ContextFor(PaneKind.Empty));

        Assert.Contains("Clock", AllText(runtime.View));
        await runtime.DisposeAsync();
    });

    [Fact]
    public Task An_empty_pane_with_no_profiles_at_all_still_builds() => Headless.RunAsync(async () =>
    {
        // A first run, before anything has been seeded. No sections must not mean no screen.
        var provider = new EmptyPaneProvider(NoCommands(), () => [], new NoIcons(), () => []);

        var runtime = await provider.CreateAsync(ContextFor(PaneKind.Empty));

        Assert.NotNull(runtime.View);
        Assert.Contains("SSH", AllText(runtime.View));
        await runtime.DisposeAsync();
    });

    [Fact]
    public Task A_restored_empty_pane_shows_the_reason_it_is_empty() => Headless.RunAsync(async () =>
    {
        var extras = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["note"] = "the window this pane held could not be found",
        };

        var provider = new EmptyPaneProvider(NoCommands(), () => [], new NoIcons(), () => []);
        var runtime = await provider.CreateAsync(ContextFor(PaneKind.Empty, extras));

        Assert.Contains("could not be found", AllText(runtime.View));
        await runtime.DisposeAsync();
    });

    // ---- dialogs ---------------------------------------------------------------------

    [Fact]
    public Task The_application_picker_can_be_built() => Headless.RunSync(() =>
    {
        var window = new AppPickerWindow(new EmptyCatalog(), new NoIcons());
        Assert.NotNull(window.Content);
    });

    [Fact]
    public Task The_profile_editor_can_be_built_for_every_kind() => Headless.RunSync(() =>
    {
        // Each kind shows a different set of fields, and the SFTP and FTP entries were missing from
        // this dropdown for a whole feature because nobody opened it.
        foreach (var kind in Enum.GetValues<ProfileKind>())
        {
            var editor = new ProfileEditorWindow(new LaunchProfile
            {
                Id = "x", Name = "x", Program = "", Kind = kind, Host = "host.example.com",
            });

            Assert.NotNull(editor.Content);
        }
    });

    [Fact]
    public Task The_credential_dialog_can_be_built() => Headless.RunSync(() =>
    {
        var window = new CredentialWindow("sftp://user@host.example.com", "user", canRemember: true);
        Assert.NotNull(window.Content);
    });

    private sealed class EmptyCatalog : IAppCatalog
    {
        public IReadOnlyList<InstalledApp> List(out string? error)
        {
            error = null;
            return [];
        }

        public InstalledApp? Resolve(string path, out string? error)
        {
            error = null;
            return null;
        }
    }

    /// <summary>Every piece of text in a control tree, so a test can ask what a screen says.</summary>
    private static string AllText(Control root)
    {
        var found = new List<string>();
        Walk(root);
        return string.Join(" | ", found);

        void Walk(object? node)
        {
            switch (node)
            {
                case TextBlock { Text: { Length: > 0 } text }:
                    found.Add(text);
                    break;
                case ContentControl { Content: string label }:
                    found.Add(label);
                    break;
            }

            switch (node)
            {
                case ContentControl content:
                    Walk(content.Content);
                    break;
                case Decorator decorator:
                    Walk(decorator.Child);
                    break;
                case Panel panel:
                    foreach (var child in panel.Children) Walk(child);
                    break;
            }
        }
    }
}
