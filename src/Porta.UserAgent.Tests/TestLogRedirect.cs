using System;
using System.IO;
using System.Runtime.CompilerServices;
using Porta.UserAgent;

namespace Porta.UserAgent.Tests;

/// <summary>
/// Maintenance item (b), 2026-08-12 — keeps this test assembly out of the live agent log.
///
/// <para>Every test run used to append to <c>%ProgramData%\PortaAgent\user-agent.log</c>, the same
/// file the field is diagnosed from. Test output has already been mistaken for field events once,
/// during an investigation where that log was the only instrument.</para>
///
/// <para>A module initializer runs before ANY code in this assembly, including xUnit's discovery of
/// the first test, so the redirect is in place before a single line can be written. A fixture or a
/// collection would not be early enough: the host classes log during construction.</para>
/// </summary>
internal static class TestLogRedirect
{
    [ModuleInitializer]
    internal static void RedirectAgentLogAwayFromTheRealOne()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "porta-test-logs");
            Directory.CreateDirectory(dir);
            UserAgentFileLog.RedirectTo(Path.Combine(dir, $"user-agent-tests-{Environment.ProcessId}.log"));
        }
        catch
        {
            // Never fail the run over logging. Worst case the old behaviour returns for this run,
            // which is what every run did before this existed.
        }
    }
}
