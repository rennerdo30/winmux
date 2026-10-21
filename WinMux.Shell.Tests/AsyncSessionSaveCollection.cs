namespace WinMux.Shell.Tests;

// These probes deliberately hold writers and briefly block the shared headless dispatcher.
// Keep other collections off that runtime until the probes and their cleanup have completed.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AsyncSessionSaveCollection
{
    public const string Name = "Async session save probes";
}
