using System.Buffers.Binary;
using System.Text.Json;

namespace WinMux.Shell;

internal sealed record LaunchRequest(string Directory)
{
    public int Version { get; init; } = 1;
    public Guid RequestId { get; init; } = Guid.NewGuid();

    public string? ValidationError() => Version != 1 ? "This WinMux launch protocol version is unsupported."
        : RequestId == Guid.Empty ? "The launch request has no request identifier."
        : string.IsNullOrWhiteSpace(Directory) || !Path.IsPathFullyQualified(Directory)
            ? "The launch directory must be an absolute folder path."
        : !System.IO.Directory.Exists(Directory) ? $"The launch folder is unavailable: {Directory}"
        : null;
}

internal sealed record LaunchReply(bool Success, string Message);

/// <summary>Bounded UTF-8 JSON frames on a dedicated pipe; the wmux command protocol is unchanged.</summary>
internal static class LaunchProtocol
{
    internal const int MaximumFrameBytes = 32768;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("The WinMux launch request is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("The WinMux launch frame size is invalid.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes)
            ?? throw new InvalidDataException("The WinMux launch request is empty.");
    }
}
