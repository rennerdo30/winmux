namespace WinMux.Core.Connections;

/// <summary>
/// A node in a connection tree: a folder that groups and states defaults, or an entry that is a
/// machine you can open.
///
/// <para>
/// Folders are not decoration. In mRemoteNG and Remote Desktop Connection Manager a folder is where
/// the username, the domain, the gateway and the credentials live, and the two hundred hosts under
/// it state only what differs — which is the reason those tools are used instead of a list, and the
/// reason WinMux grew this instead of flattening on import (ADR 0025).
/// </para>
/// </summary>
public abstract class ConnectionNode
{
    private protected ConnectionNode(string name, ConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Name = name ?? string.Empty;
        Settings = settings;
    }

    public string Name { get; set; }

    /// <summary>What this node states for itself. Everything else comes from its parent.</summary>
    public ConnectionSettings Settings { get; set; }

    /// <summary>Null for the root of a tree.</summary>
    public ConnectionFolder? Parent { get; internal set; }

    /// <summary>This node and every folder above it, nearest first.</summary>
    public IEnumerable<ConnectionNode> UpToRoot()
    {
        for (ConnectionNode? node = this; node is not null; node = node.Parent) yield return node;
    }

    /// <summary>The path of names from the root, for saying where a value came from.</summary>
    public string Path => string.Join(" / ", UpToRoot().Reverse().Select(node => node.Name));
}

/// <summary>A folder of connections, which may also state settings its children inherit.</summary>
public sealed class ConnectionFolder : ConnectionNode
{
    private readonly List<ConnectionNode> _children = [];

    public ConnectionFolder(string name, ConnectionSettings? settings = null)
        : base(name, settings ?? ConnectionSettings.Nothing) { }

    public IReadOnlyList<ConnectionNode> Children => _children;

    public ConnectionFolder Add(ConnectionNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this) || child.UpToRoot().Contains(this) && child.Parent is not null)
            throw new InvalidOperationException("A folder cannot contain itself.");

        child.Parent?.Remove(child);
        child.Parent = this;
        _children.Add(child);
        return this;
    }

    public bool Remove(ConnectionNode child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!_children.Remove(child)) return false;
        child.Parent = null;
        return true;
    }

    /// <summary>Every entry under this folder, at any depth, in the order they appear.</summary>
    public IEnumerable<ConnectionEntry> Entries()
    {
        foreach (var child in _children)
        {
            if (child is ConnectionEntry entry) yield return entry;
            else if (child is ConnectionFolder folder)
            {
                foreach (var nested in folder.Entries()) yield return nested;
            }
        }
    }

    /// <summary>This folder and every folder under it.</summary>
    public IEnumerable<ConnectionFolder> Folders()
    {
        yield return this;
        foreach (var nested in _children.OfType<ConnectionFolder>().SelectMany(folder => folder.Folders()))
            yield return nested;
    }
}

/// <summary>One machine, which may be opened in a pane.</summary>
public sealed class ConnectionEntry : ConnectionNode
{
    public ConnectionEntry(string name, ConnectionSettings? settings = null)
        : base(name, settings ?? ConnectionSettings.Nothing) { }
}
