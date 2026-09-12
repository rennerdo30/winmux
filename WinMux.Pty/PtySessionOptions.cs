namespace WinMux.Pty;

/// <summary>Describes a process to start inside a pseudoterminal.</summary>
public sealed class PtySessionOptions
{
    /// <summary>Gets the executable path or command name.</summary>
    public required string Application { get; init; }

    /// <summary>Gets the arguments passed to <see cref="Application"/>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    /// <summary>Gets the initial working directory.</summary>
    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;

    /// <summary>
    /// Gets environment overrides. An empty value removes that variable, following Porta.Pty's
    /// contract; variables not present here are inherited.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();

    /// <summary>Gets the initial width in character cells.</summary>
    public int Columns { get; init; } = 120;

    /// <summary>Gets the initial height in character cells.</summary>
    public int Rows { get; init; } = 30;

    /// <summary>Gets an optional provider-visible session name.</summary>
    public string? Name { get; init; } = "WinMux";

    /// <summary>
    /// Gets whether arguments are passed as a verbatim command line rather than individually
    /// quoted by the provider.
    /// </summary>
    public bool VerbatimCommandLine { get; init; }
}
