using PortaPtyOptions = Porta.Pty.PtyOptions;
using PortaPtyProvider = Porta.Pty.PtyProvider;
using System.Text;

namespace WinMux.Pty;

/// <summary>Starts pseudoterminal sessions backed by the configured provider.</summary>
public static class PtySession
{
    /// <summary>Starts a process and returns its pseudoterminal session.</summary>
    public static async Task<IPtySession> StartAsync(
        PtySessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var quoteWindowsArguments = OperatingSystem.IsWindows() && !options.VerbatimCommandLine;
        var providerOptions = new PortaPtyOptions
        {
            Name = options.Name,
            App = options.Application,
            // Porta.Pty 2.2.2's generic quoting uses POSIX single quotes. CreateProcess does not
            // treat those as delimiters, so restored arguments containing spaces arrive with a
            // literal leading quote. Own the Windows quoting at our seam and ask the provider to
            // pass the resulting command-line tokens verbatim.
            CommandLine = quoteWindowsArguments
                ? options.Arguments.Select(QuoteWindowsArgument).ToArray()
                : options.Arguments.ToArray(),
            Cwd = options.WorkingDirectory,
            Environment = new Dictionary<string, string>(options.Environment),
            Cols = options.Columns,
            Rows = options.Rows,
            VerbatimCommandLine = options.VerbatimCommandLine || quoteWindowsArguments,
            UseAsyncIo = true,
        };

        var connection = await PortaPtyProvider
            .SpawnAsync(providerOptions, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return new PortaPtySession(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static void ValidateDimensions(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
    }

    internal static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character =>
                char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void Validate(PtySessionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Application);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(options.Arguments);
        ArgumentNullException.ThrowIfNull(options.Environment);
        ValidateDimensions(options.Columns, options.Rows);

        if (options.Arguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Arguments cannot contain null values.", nameof(options));
        }

        if (options.Environment.Any(static pair => pair.Key is null || pair.Value is null))
        {
            throw new ArgumentException(
                "Environment overrides cannot contain null keys or values.",
                nameof(options));
        }
    }
}
