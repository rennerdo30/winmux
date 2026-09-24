using WinMux.Core.Connections;

namespace WinMux.Tests;

/// <summary>
/// Folders that carry settings, and hosts that take them. This is the part of ADR 0025 that the
/// parsers exist to fill: two hundred servers under one folder, each stating only what differs.
/// </summary>
public class ConnectionInheritanceTests
{
    /// <summary>All / Production / web-01, with settings spread over the three.</summary>
    private static (ConnectionFolder Root, ConnectionFolder Production, ConnectionEntry Web) Tree()
    {
        var root = new ConnectionFolder("All", new ConnectionSettings
        {
            User = Inherited<string>.Of("administrator"),
            Domain = Inherited<string>.Of("CORP"),
            Protocol = Inherited<ConnectionProtocol>.Of(ConnectionProtocol.Rdp),
        });

        var production = new ConnectionFolder("Production", new ConnectionSettings
        {
            User = Inherited<string>.Of("svc-deploy"),
            Gateway = Inherited<string>.Of("gw.corp.example"),
        });

        var web = new ConnectionEntry("web-01", new ConnectionSettings
        {
            Host = Inherited<string>.Of("10.0.0.11"),
        });

        root.Add(production);
        production.Add(web);
        return (root, production, web);
    }

    [Fact]
    public void A_host_takes_what_it_does_not_state()
    {
        var (_, _, web) = Tree();

        Assert.Equal("CORP", ConnectionResolver.Domain(web).Value);
        Assert.Equal(ConnectionProtocol.Rdp, ConnectionResolver.Protocol(web).Value);
        Assert.Equal("gw.corp.example", ConnectionResolver.Gateway(web).Value);
    }

    [Fact]
    public void The_nearest_folder_wins()
    {
        // Both the root and Production state a user. Without "nearest wins" a folder could never
        // override anything, and overriding is the reason for folders.
        var (_, _, web) = Tree();

        Assert.Equal("svc-deploy", ConnectionResolver.User(web).Value);
    }

    [Fact]
    public void A_host_overrides_its_folders()
    {
        var (_, _, web) = Tree();
        web.Settings = web.Settings with { User = Inherited<string>.Of("root") };

        Assert.Equal("root", ConnectionResolver.User(web).Value);
    }

    [Fact]
    public void A_resolved_value_says_where_it_came_from()
    {
        // The half that makes inheritance usable: a host connecting as the wrong user is no help
        // unless you can see which folder says so.
        var (root, production, web) = Tree();

        Assert.Same(production, ConnectionResolver.User(web).From);
        Assert.Same(root, ConnectionResolver.Domain(web).From);
        Assert.Same(web, ConnectionResolver.Host(web).From);
    }

    [Fact]
    public void And_says_so_in_words()
    {
        var (_, _, web) = Tree();

        Assert.Equal("svc-deploy (from Production)", ConnectionResolver.User(web).Describe(web));
        Assert.Equal("10.0.0.11", ConnectionResolver.Host(web).Describe(web));
        Assert.Equal("(not set)", ConnectionResolver.Identity(web).Describe(web));
    }

    [Fact]
    public void Stating_nothing_is_not_the_same_as_stating_empty()
    {
        // "Log in with no username" and "use whatever the folder says" are different instructions,
        // and a tree that cannot tell them apart makes every child carry a copy of its parent.
        var (_, _, web) = Tree();
        web.Settings = web.Settings with { User = Inherited<string>.Of("") };

        Assert.Equal("", ConnectionResolver.User(web).Value);
        Assert.Same(web, ConnectionResolver.User(web).From);
    }

    [Fact]
    public void A_setting_nobody_states_resolves_to_nothing()
    {
        var (_, _, web) = Tree();

        var identity = ConnectionResolver.Identity(web);

        Assert.False(identity.IsSet);
        Assert.Null(identity.Value);
    }

    [Fact]
    public void A_folder_resolves_like_anything_else()
    {
        var (root, production, _) = Tree();

        Assert.Equal("CORP", ConnectionResolver.Domain(production).Value);
        Assert.Same(root, ConnectionResolver.Domain(production).From);
    }

    [Fact]
    public void Moving_a_host_changes_what_it_inherits()
    {
        // The thing a flattened import throws away: drag a host to another folder and its settings
        // follow the folder, rather than staying whatever they were when it was imported.
        var (root, _, web) = Tree();
        var staging = new ConnectionFolder("Staging", new ConnectionSettings
        {
            User = Inherited<string>.Of("tester"),
        });
        root.Add(staging);

        staging.Add(web);

        Assert.Equal("tester", ConnectionResolver.User(web).Value);
        Assert.Equal("All / Staging / web-01", web.Path);
    }

    [Fact]
    public void Every_host_under_a_folder_is_found_at_any_depth()
    {
        var (root, production, _) = Tree();
        var inner = new ConnectionFolder("Databases");
        inner.Add(new ConnectionEntry("db-01"));
        production.Add(inner);

        Assert.Equal(["web-01", "db-01"], root.Entries().Select(entry => entry.Name));
        Assert.Equal(["All", "Production", "Databases"], root.Folders().Select(folder => folder.Name));
    }

    [Fact]
    public void A_folder_cannot_contain_itself()
    {
        var root = new ConnectionFolder("All");

        Assert.Throws<InvalidOperationException>(() => root.Add(root));
    }
}
