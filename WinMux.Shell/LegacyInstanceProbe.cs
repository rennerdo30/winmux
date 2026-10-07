using System.IO.Pipes;

namespace WinMux.Shell;

internal static class LegacyInstanceProbe
{
    /// <summary>Connect and disconnect without sending an action or changing the running session.</summary>
    public static async Task<bool> IsListeningAsync(string pipeName = CommandServer.DefaultPipeName)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(250).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
