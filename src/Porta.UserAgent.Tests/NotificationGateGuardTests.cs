using System.IO;

namespace Porta.UserAgent.Tests;

// Workstream B1 rule-#2 proof (mechanized "grep-proof"): the host must not emit any interrupt
// toast outside the anti-spam gate. Every interrupt now goes through
// _interventionExecutor.TryShowGatedInterruptToast; the demoted behavioral/capture/update
// classes go to the activity log, never the renderer. This test fails the moment someone
// reintroduces a bare renderer toast in the host (the exact class of regression Workstream B
// removes).
public sealed class NotificationGateGuardTests
{
    [Fact]
    public void UserAgentHost_EmitsNoInterruptOutsideTheGate()
    {
        string source = ReadHostSource();

        // A bare `_toastRenderer.TryShow(` is the WinForms fallback toast bypassing the gate.
        // (`_toastRenderer.TryShowSystemNotification(` — the user-set reminder OS alarm — does
        // NOT match this because of the trailing "System...", and is intentionally out of scope.)
        Assert.DoesNotContain("_toastRenderer.TryShow(", source);

        // The deleted legacy direct-emit helper must stay deleted.
        Assert.DoesNotContain("TryShowCommunicationToast", source);
    }

    private static string ReadHostSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (string root in new[] { dir.FullName, Path.Combine(dir.FullName, "src") })
            {
                string candidate = Path.Combine(root, "Porta.UserAgent", "UserAgentHost", "UserAgentHost.cs");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate UserAgentHost.cs to verify the no-ungated-emit invariant.");
    }
}
