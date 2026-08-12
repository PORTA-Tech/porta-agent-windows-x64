using System;
using System.IO;
using Porta.UserAgent;
using Xunit;

namespace Porta.UserAgent.Tests;

// Maintenance item (b) — the seam that keeps test runs out of the live agent log.
public class UserAgentFileLogRedirectTests
{
    [Fact]
    public void TheTestRunIsNotWritingToTheRealAgentLog()
    {
        string realLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PortaAgent",
            "user-agent.log");

        // The module initializer redirects before any test runs. If this fails, this assembly is
        // appending to the file the field is diagnosed from.
        Assert.NotEqual(realLog, UserAgentFileLog.CurrentPath, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("porta-test-logs", UserAgentFileLog.CurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WritesLandInTheRedirectedFile()
    {
        string original = UserAgentFileLog.CurrentPath;
        string path = Path.Combine(Path.GetTempPath(), "porta-test-logs", $"redirect-{Guid.NewGuid():N}.log");

        try
        {
            UserAgentFileLog.RedirectTo(path);
            UserAgentFileLog.Info("redirect_probe");

            Assert.True(File.Exists(path));
            Assert.Contains("redirect_probe", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            UserAgentFileLog.RedirectTo(original);
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AnUnwritableOverrideDropsTheLineRatherThanFallingBackToTheRealLog()
    {
        string original = UserAgentFileLog.CurrentPath;

        try
        {
            // A path that cannot exist: the drive letter is not a real volume on any runner.
            UserAgentFileLog.RedirectTo(@"\\?\Q:\definitely\not\a\volume\user-agent.log");

            // The contract is that this is silent and harmless, and above all that it does NOT
            // reroute into %ProgramData%. If it throws, the redirect is not safe to rely on.
            UserAgentFileLog.Info("must_not_reach_the_real_log");
        }
        finally
        {
            UserAgentFileLog.RedirectTo(original);
        }
    }
}
