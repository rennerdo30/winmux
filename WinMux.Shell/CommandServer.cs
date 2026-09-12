using System.IO.Pipes;
using System.Text;

namespace WinMux.Shell;

/// <summary>
/// Receives one named action per local named-pipe connection.
///
/// Protocol (UTF-8, one line in each direction):
///   request:  action.name
///   success:  OK [message]
///   failure:  ERR message
/// </summary>
public sealed class CommandServer
{
    public const string DefaultPipeName = "winmux-command-v1";
    public const int MaximumActionNameLength = 256;

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _pipeName;
    private readonly Func<string, CancellationToken, ValueTask<string?>> _invoke;

    public CommandServer(
        Func<string, CancellationToken, ValueTask<string?>> invoke,
        string pipeName = DefaultPipeName)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _invoke = invoke;
        _pipeName = pipeName;
    }

    /// <summary>
    /// Listens until cancellation. A malformed request, disconnected client, or action callback
    /// failure is contained to that connection and does not stop the listener.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Application shutdown is the listener's normal completion path.
        }
    }

    private async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Utf8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        using var writer = new StreamWriter(stream, Utf8, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        string? action;
        try
        {
            action = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            await TryReplyAsync(writer, "ERR Request must be valid UTF-8.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (IOException)
        {
            return;
        }

        var validationError = ValidateActionName(action);
        if (validationError is not null)
        {
            await TryReplyAsync(writer, "ERR " + validationError, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var message = await _invoke(action!, cancellationToken).ConfigureAwait(false);
            await TryReplyAsync(writer, FormatResponse("OK", message), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await TryReplyAsync(writer, "ERR Action was canceled.", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TryReplyAsync(writer, FormatResponse("ERR", ex.Message), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static string? ValidateActionName(string? action)
    {
        if (action is null)
            return "No action name was received.";
        if (action.Length == 0)
            return "Action name cannot be empty.";
        if (action.Length > MaximumActionNameLength)
            return $"Action name cannot exceed {MaximumActionNameLength} characters.";
        if (action.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not ':'))
            return "Action name may contain only ASCII letters, digits, '.', '-', '_' and ':'.";

        return null;
    }

    private static string FormatResponse(string status, string? message)
    {
        var oneLineMessage = string.IsNullOrWhiteSpace(message)
            ? null
            : string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return oneLineMessage is null ? status : $"{status} {oneLineMessage}";
    }

    private static async Task TryReplyAsync(
        StreamWriter writer,
        string response,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client can time out or exit after dispatch. Its disappearance must not stop
            // the server after the action has already been handled.
        }
    }
}
