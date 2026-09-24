namespace WinMux.Core.Connections;

/// <summary>A secret found in another tool's file, and how much trouble it was to read.</summary>
/// <param name="Entry">The connection it belongs to.</param>
/// <param name="Secret">The password, in the clear.</param>
public readonly record struct FoundCredential(ConnectionEntry Entry, string Secret);

/// <summary>What a source could not do, in words for the user.</summary>
public sealed class ConnectionSourceException : Exception
{
    public ConnectionSourceException(string message) : base(message) { }
    public ConnectionSourceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Another tool's saved connections, read and written in place.
///
/// <para>
/// Not an importer. WinMux reads PuTTY's and FileZilla's and mRemoteNG's own files and writes
/// changes back to them, so those tools go on working on the same data — converting to WinMux's own
/// format is something the user asks for, not the price of seeing their servers (ADR 0025).
/// </para>
///
/// <para>
/// Which makes the rules below more important than the parsing. A source is editing a file it did
/// not write, full of settings it has never heard of, that another program may have open.
/// </para>
/// </summary>
public interface IConnectionSource
{
    /// <summary>What to call this in the UI — "FileZilla", "PuTTY".</summary>
    string DisplayName { get; }

    /// <summary>Where the connections live, for showing the user what is being read.</summary>
    string Location { get; }

    /// <summary>Whether there is anything here to read at all.</summary>
    bool Exists { get; }

    /// <summary>
    /// Whether WinMux may write while the other tool is running.
    ///
    /// False for anything that holds its file open and writes it on exit: saving underneath such a
    /// tool loses the change the moment it quits. The UI refuses rather than racing.
    /// </summary>
    bool CanWriteWhileOtherToolRuns { get; }

    /// <summary>
    /// Read the tree. Throws <see cref="ConnectionSourceException"/> rather than returning an empty
    /// tree, because "your two hundred servers are not here" and "this file is not valid" must not
    /// look the same.
    /// </summary>
    ConnectionFolder Read();

    /// <summary>
    /// Write the tree back, preserving everything in the document that WinMux does not model.
    ///
    /// The first write leaves a backup beside the original. A source that cannot write reports it
    /// rather than pretending to have saved.
    /// </summary>
    void Write(ConnectionFolder root);

    /// <summary>
    /// The passwords this file holds, in the clear, for offering to move them to the OS credential
    /// store. Separate from <see cref="Read"/> and never part of the tree: a secret is fetched when
    /// connecting, not carried around in memory with the settings (ADR 0020, ADR 0025).
    /// </summary>
    IReadOnlyList<FoundCredential> ReadCredentials(ConnectionFolder root);
}
