using Porta.Agent.Common.Identity;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter.Tests;

public sealed class RememberedSignInStateTests
{
    [Fact]
    public void ExpiryIsOnlyClaimedWhenTheServerGaveOne()
    {
        var withoutExpiry = new RememberedSignInState(
            "jane@example.com",
            "refresh-token",
            DateTimeOffset.UtcNow,
            ExpiresAtUtc: null);

        // No expiry means the server did not say. Treating that as expired would throw
        // away a working token; let the control plane be the one to refuse it.
        Assert.False(withoutExpiry.IsExpired);
    }

    [Fact]
    public void ExpiredTokensAreRecognisedWithoutARoundTrip()
    {
        var expired = new RememberedSignInState(
            "jane@example.com",
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(-91),
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var live = expired with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30) };

        Assert.True(expired.IsExpired);
        Assert.False(live.IsExpired);
    }

    // The purge must keep pointing at v1 forever. Repointing it at the current file would
    // silently delete the credential it is meant to protect.
    [Fact]
    public void LegacyPurgeTargetsTheV1PasswordFile()
    {
        string path = LegacyRememberedSignInPurge.BuildLegacyPath();

        Assert.EndsWith("control-center.remembered-signin.v1.json", path, StringComparison.Ordinal);
    }
}
