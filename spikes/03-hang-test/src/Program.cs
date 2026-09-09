namespace HangSpike;

internal static class Program
{
    private static int Main(string[] argv)
    {
        var a = new Args(argv);
        var role = a.Str("role", "shell").ToLowerInvariant();
        try
        {
            return role switch
            {
                "hangapp" => HangApp.Run(a),
                "panehost" => PaneHost.Run(a),
                "shell" => Shell.Run(a),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            Log.Line("UNHANDLED " + e);
            return 3;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            HangSpike — WinMux Phase 0, spike 3 (hang test)

              --role shell --scenario T1..T6 [--logdir DIR] [--hangms N] [--asyncpos]
              --role panehost --logdir DIR [--x --y --w --h]
              --role hangapp  --logdir DIR [--x --y --w --h]

            Scenarios:
              T1  attach-mode baseline, no SetParent anywhere
              T2  in-process embed: shell reparents the app into its own window
              T3  OOP host, and the shell reparents the host (tests transitivity)
              T4  OOP host, shell only repositions the host's top-level window
              T5  lifecycle: host killed hard, does the app survive?
              T6  lifecycle: host detaches and quits cleanly, is the app restored?
            """);
        return 64;
    }
}
