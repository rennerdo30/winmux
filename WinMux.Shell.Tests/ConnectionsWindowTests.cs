using Avalonia.Controls;
using Avalonia.VisualTree;
using WinMux.Connections;
using WinMux.Core.Settings;
using WinMux.Shell.Connections;

namespace WinMux.Shell.Tests;

/// <summary>
/// The window that makes the six readers reachable.
///
/// Until it existed they were a capability with no interface, which CLAUDE.md section 5a says is not
/// a feature but a note to the author — and this suite exists because a dialog that compiles and
/// shows nothing is the same note with extra steps.
/// </summary>
public sealed class ConnectionsWindowTests
{
    /// <summary>A source that answers from an in-memory tree, standing in for a real tool.</summary>
    private sealed class FakeSource(ConnectionFolder? root, string? problem = null) : IConnectionSource
    {
        public string DisplayName => "Pretend";
        public string Location => @"C:\pretend\sessions.xml";
        public bool Exists => true;
        public bool CanWriteWhileOtherToolRuns => true;
        public ConnectionFolder Read() => root ?? throw new ConnectionSourceException(problem ?? "no");
        public void Write(ConnectionFolder tree) { }
        public IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder tree) => [];
    }

    /// <summary>Production states the credentials; web-01 inherits them and states only its host.</summary>
    private static ConnectionSourceResult WithTree()
    {
        var root = new ConnectionFolder("Pretend");
        var production = new ConnectionFolder("Production", new ConnectionSettings
        {
            User = Inherited<string>.Of("svc-deploy"),
            Domain = Inherited<string>.Of("CORP"),
            Protocol = Inherited<ConnectionProtocol>.Of(ConnectionProtocol.Rdp),
            Port = Inherited<int>.Of(3389),
        });
        production.Add(new ConnectionEntry("web-01", new ConnectionSettings
        {
            Host = Inherited<string>.Of("10.0.0.11"),
        }));
        root.Add(production);

        return new ConnectionSourceResult(new FakeSource(root), root, null);
    }

    private static IReadOnlyList<ListBoxItem> RowsOf(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<ListBox>().First().ItemsSource!.Cast<ListBoxItem>()
    ];

    private static Window Show(ConnectionsWindow window)
    {
        window.Show();
        window.Measure(new Avalonia.Size(900, 620));
        window.Arrange(new Avalonia.Rect(0, 0, 900, 620));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    [Fact]
    public Task The_tree_is_shown_with_its_folders_and_hosts() => Headless.RunSync(() =>
    {
        var window = Show(new ConnectionsWindow([WithTree()]));

        var labels = RowsOf(window)
            .Select(item => ((TextBlock)((StackPanel)((Border)item.Content!).Child!).Children[0]).Text ?? "")
            .ToArray();

        Assert.Equal(3, labels.Length);
        Assert.Contains(labels, text => text.Contains("Pretend", StringComparison.Ordinal));
        Assert.Contains(labels, text => text.Contains("Production", StringComparison.Ordinal));
        Assert.Contains(labels, text => text.Contains("web-01", StringComparison.Ordinal));
    });

    [Fact]
    public Task A_source_that_could_not_be_read_says_so_instead_of_showing_nothing() => Headless.RunSync(() =>
    {
        // "You have no sessions" and "your sessions could not be read" must never look the same.
        var broken = new ConnectionSourceResult(new FakeSource(null), null, "the file is not valid");

        var window = Show(new ConnectionsWindow([broken]));

        Assert.Single(RowsOf(window));
        Assert.Contains(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text is not null && text.Text.Contains("not valid", StringComparison.Ordinal));
    });

    [Fact]
    public Task An_inherited_value_is_shown_with_the_folder_it_came_from() => Headless.RunSync(() =>
    {
        // The reason the tree keeps inheritance rather than flattening it: a value you cannot
        // locate is a value you cannot change.
        var window = Show(new ConnectionsWindow([WithTree()]));
        var rows = RowsOf(window);
        window.GetVisualDescendants().OfType<ListBox>().First().SelectedItem =
            rows.First(item => ((Row(item))?.Name ?? "") == "web-01");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var shown = window.GetVisualDescendants().OfType<SelectableTextBlock>()
            .Select(text => text.Text ?? "").ToArray();

        Assert.Contains("svc-deploy (from Production)", shown);
        Assert.Contains("CORP (from Production)", shown);
        Assert.Contains("10.0.0.11", shown);
    });

    private static ConnectionNode? Row(ListBoxItem item) =>
        item.Tag?.GetType().GetProperty("Node")?.GetValue(item.Tag) as ConnectionNode;

    [Fact]
    public void A_connection_becomes_something_that_can_be_opened()
    {
        // With the folder's settings resolved into it, which is what makes an inherited username
        // arrive at the pane rather than an empty one.
        var entry = (ConnectionEntry)((ConnectionFolder)WithTree().Root!.Children[0]).Children[0];

        var profile = ConnectionCatalog.ToProfile(entry);

        Assert.NotNull(profile);
        Assert.Equal(ProfileKind.Rdp, profile!.Kind);
        Assert.Equal("10.0.0.11", profile.Host);
        Assert.Equal(3389, profile.Port);
        Assert.Equal(@"CORP\svc-deploy", profile.User);
    }

    [Fact]
    public void A_connection_with_no_host_cannot_be_opened() =>
        Assert.Null(ConnectionCatalog.ToProfile(new ConnectionEntry("empty")));

    [Fact]
    public void A_protocol_winmux_does_not_speak_cannot_be_opened()
    {
        // Serial and telnet are read and shown, because hiding them would make the list disagree
        // with the tool it came from — but they cannot be put in a pane, and saying so beats a
        // button that does nothing.
        var serial = new ConnectionEntry("switch", new ConnectionSettings
        {
            Host = Inherited<string>.Of("COM3"),
            Protocol = Inherited<ConnectionProtocol>.Of(ConnectionProtocol.Serial),
        });

        Assert.Null(ConnectionCatalog.ToProfile(serial));
    }

    [Fact]
    public void Importing_takes_a_copy_and_leaves_the_other_tool_alone()
    {
        var folder = (ConnectionFolder)WithTree().Root!.Children[0];

        var profiles = folder.Entries().Select(entry => ConnectionCatalog.ToProfile(entry, "pretend-")).ToArray();

        Assert.All(profiles, profile => Assert.NotNull(profile));
        Assert.All(profiles, profile => Assert.Equal("Pretend / Production / web-01", profile!.Source));
    }
}
