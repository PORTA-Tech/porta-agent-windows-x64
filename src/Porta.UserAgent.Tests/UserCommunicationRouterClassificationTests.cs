using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream B1 (LOCKED classification) — proves the behavioral-toast split the host relies on:
// only recovery + the one-time first-win are interrupts (routed through the anti-spam gate);
// every other behavioral toast is a non-interrupt that must never reach the toast renderer.
public sealed class UserCommunicationRouterClassificationTests
{
    [Theory]
    [InlineData("ari:recovery")]
    [InlineData("ari:first_focus")]
    [InlineData("ARI:RECOVERY")] // case-insensitive
    public void IsInterruptToast_TrueForRecoveryAndFirstWin(string key)
    {
        Assert.True(UserCommunicationRouter.IsInterruptToast(key));
    }

    [Theory]
    [InlineData("ari:pressure")]
    [InlineData("ari:milestone_30")]
    [InlineData("ari:milestone_90")]
    [InlineData("ari:long_session_no_break")]
    [InlineData("ari:weak_hour")]
    [InlineData("ari:session_wrap")]
    [InlineData("")]
    [InlineData(null)]
    public void IsInterruptToast_FalseForEverythingElse(string? key)
    {
        Assert.False(UserCommunicationRouter.IsInterruptToast(key));
    }

    [Theory]
    [InlineData("ari:recovery", "ari:recovery")]
    [InlineData("ari:first_focus", "ari:first_win")] // first-win uses the locked gate key
    public void ResolveAriGateKey_MapsToLockedKeys(string toastKey, string expectedGateKey)
    {
        Assert.Equal(expectedGateKey, UserCommunicationRouter.ResolveAriGateKey(toastKey));
    }
}
