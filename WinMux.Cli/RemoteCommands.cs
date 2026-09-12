using System.IO.Pipes;
using System.Text;

namespace WinMux.Cli;

/// <summary>Result of asking the running WinMux shell to invoke a named action.</summary>
public sealed record RemoteCommandResult(bool Succeeded, string Message)
{
    public int ExitCode => Succeeded ? 0 : 1;
}

/// <summary>Client for the shell's one-request-per-connection command protocol.</summary>
public static class RemoteCommands
{
    public const string DefaultPipeName = "winmux-command-v1";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<RemoteCommandResult> InvokeAsync(
        string actionName,
        string pipeName = DefaultPipeName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actionName))
            return Failure("Action name cannot be empty.");
        if (actionName.Contains('\r') || actionName.Contains('\n'))
            return Failure("Action name must be a single line.");
        if (string.IsNullOrWhiteSpace(pipeName))
            return Failure("Pipe name cannot be empty.");

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            return Failure("Timeout must be greater than zero.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await pipe.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);

            using var reader = new StreamReader(
                pipe,
                Utf8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            using var writer = new StreamWriter(pipe, Utf8, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            await writer.WriteLineAsync(actionName.AsMemory(), timeoutSource.Token)
                .ConfigureAwait(false);

            var response = await reader.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
            return ParseResponse(response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"Timed out after {effectiveTimeout.TotalMilliseconds:0} ms waiting for WinMux.");
        }
        catch (OperationCanceledException)
        {
            return Failure("Command was canceled.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure("Cannot access the WinMux command pipe: " + ex.Message);
        }
        catch (IOException ex)
        {
            return Failure("Could not communicate with WinMux: " + ex.Message);
        }
    }

    private static RemoteCommandResult ParseResponse(string? response)
    {
        if (response is null)
            return Failure("WinMux closed the connection without returning a result.");
        if (response == "OK")
            return Success("Action invoked.");
        if (response.StartsWith("OK ", StringComparison.Ordinal))
            return Success(response[3..]);
        if (response == "ERR")
            return Failure("WinMux rejected the action.");
        if (response.StartsWith("ERR ", StringComparison.Ordinal))
            return Failure(response[4..]);

        return Failure("WinMux returned an invalid command response.");
    }

    private static RemoteCommandResult Success(string message) => new(true, message);
    private static RemoteCommandResult Failure(string message) => new(false, message);
}
