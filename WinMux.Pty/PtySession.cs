using PortaPtyOptions = Porta.Pty.PtyOptions;
using PortaPtyProvider = Porta.Pty.PtyProvider;

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

        var providerOptions = new PortaPtyOptions
        {
            Name = options.Name,
            App = options.Application,
            CommandLine = options.Arguments.ToArray(),
            Cwd = options.WorkingDirectory,
            Environment = new Dictionary<string, string>(options.Environment),
            Cols = options.Columns,
            Rows = options.Rows,
            VerbatimCommandLine = options.VerbatimCommandLine,
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
