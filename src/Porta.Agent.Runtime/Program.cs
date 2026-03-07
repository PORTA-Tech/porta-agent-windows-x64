using System;

internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine(
            "Porta.Agent.Runtime legacy capture host is disabled. "
            + "Run AriAgent for the core service and Porta.UserAgent for session-bound input capture.");
        return 1;
    }
}
