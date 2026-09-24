namespace WinMux.Connections;

/// <summary>
/// Why a source file could not be read, in words for the user.
///
/// <para>
/// Shared because the answer is the same whichever format failed, and because one of these answers
/// is not obvious from the exception at all: a file under a redirected Documents folder may be a
/// OneDrive placeholder that has never been downloaded, and Windows reports that as "the cloud file
/// provider is not running". Handing that sentence to somebody looking for their saved sessions
/// explains nothing.
/// </para>
/// </summary>
internal static class SourceFile
{
    /// <summary>ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING, and its neighbours in the same family.</summary>
    private const int CloudProviderNotRunning = unchecked((int)0x8007016A);
    private const int CloudFileNotInSync = unchecked((int)0x80070179);
    private const int CloudFileUnsuccessful = unchecked((int)0x8007016E);

    /// <summary>A sentence naming the file and saying what stopped it being read.</summary>
    public static string Describe(string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.HResult switch
        {
            CloudProviderNotRunning or CloudFileNotInSync or CloudFileUnsuccessful =>
                $"{path} is stored in the cloud and is not on this machine. Open the folder in " +
                "File Explorer and choose \"Always keep on this device\", then try again.",

            _ => $"{path} could not be read: {exception.Message}",
        };
    }
}
