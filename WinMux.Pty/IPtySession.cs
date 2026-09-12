namespace WinMux.Pty;

/// <summary>
/// Owns one process running inside a pseudoterminal.
/// </summary>
public interface IPtySession : IDisposable
{
    /// <summary>Gets the operating-system process identifier of the terminal process.</summary>
    int ProcessId { get; }

    /// <summary>
    /// Gets the raw byte stream produced by the pseudoterminal.
    /// The stream is owned by this session and must not be disposed separately.
    /// </summary>
    Stream Output { get; }

    /// <summary>
    /// Gets a task that completes with the process exit code. If the session is disposed before
    /// the underlying provider reports an exit code, the task is canceled.
    /// </summary>
    Task<int> Exited { get; }

    /// <summary>Writes raw input bytes to the pseudoterminal.</summary>
    ValueTask WriteAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the pseudoterminal's character-cell dimensions.</summary>
    void Resize(int columns, int rows);
}
