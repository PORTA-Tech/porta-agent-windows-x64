using System.Net;
using System.Net.Http;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter.Tests;

/// <summary>
/// The rule under test, for both /v1/me and /auth/refresh: a 401 is the only answer that
/// authorises the caller to delete a stored credential. Everything else -- offline, 500,
/// a truncated body -- means "we learned nothing", and collapsing those to the same
/// outcome is what made a dropped connection look like an expired sign-in.
/// </summary>
public sealed class ControlPlaneSessionResultTests
{
    private const string MeBody =
        "{\"account\":{\"accountId\":\"a1\",\"displayName\":\"Jane\",\"primaryEmail\":\"jane@example.com\","
        + "\"emailVerified\":true,\"avatarUrl\":null,\"status\":\"active\",\"authProvider\":\"local\","
        + "\"lastLoginAt\":null,\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\"},"
        + "\"organizations\":[],\"devices\":[],\"consents\":[]}";

    [Fact]
    public async Task ReadMeResult_TreatsUnauthorizedAsAuthoritative()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error_code\":\"SESSION_EXPIRED\"}")
        };

        ControlPlaneMeResult result = await ControlPlaneAccountClient.ReadMeResultAsync(response, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Unauthorized, result.Outcome);
        Assert.True(result.SessionRejected);
        Assert.False(result.Ok);
    }

    // The bug: a 500 or a timeout used to collapse to the same null as a 401, and the
    // caller deleted the stored session on it. A server error must never sign anyone out.
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ReadMeResult_DoesNotTreatServerFailureAsAnExpiredSession(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("upstream unavailable")
        };

        ControlPlaneMeResult result = await ControlPlaneAccountClient.ReadMeResultAsync(response, CancellationToken.None);

        Assert.False(result.SessionRejected);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ReadMeResult_DoesNotTreatAnUnreadableBodyAsAnExpiredSession()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not json")
        };

        ControlPlaneMeResult result = await ControlPlaneAccountClient.ReadMeResultAsync(response, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Rejected, result.Outcome);
        Assert.False(result.SessionRejected);
    }

    [Fact]
    public async Task ReadMeResult_ReturnsTheProfileOnSuccess()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MeBody)
        };

        ControlPlaneMeResult result = await ControlPlaneAccountClient.ReadMeResultAsync(response, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("jane@example.com", result.Profile!.Account.PrimaryEmail);
    }

    [Fact]
    public async Task ReadRefreshResult_KeepsTheRotatedTokenFromTheResponse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"access_token\":\"new-access\",\"token_type\":\"Bearer\",\"refresh_token\":\"rotated\","
                + "\"refresh_expires_at_utc\":\"2026-11-02T00:00:00Z\","
                + "\"user\":{\"account_id\":\"a1\",\"user_id\":\"jane@example.com\",\"role\":\"developer\","
                + "\"display_name\":\"Jane\",\"avatar_url\":null,\"auth_provider\":\"local\"},"
                + "\"session_expires_at_utc\":\"2026-09-03T00:00:00Z\","
                + "\"reauth_expires_at_utc\":\"2026-08-04T00:10:00Z\"}")
        };

        ControlPlaneRefreshResult result = await ControlPlaneAccountClient.ReadRefreshResultAsync(response, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("new-access", result.Response!.AccessToken);
        // Storing the old token instead of this one is what trips reuse detection.
        Assert.Equal("rotated", result.Response.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ReadRefreshResult_TreatsRefusalAsFinalForTheToken(HttpStatusCode status)
    {
        using var response = new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"error_code\":\"REFRESH_TOKEN_REUSED\"}")
        };

        ControlPlaneRefreshResult result = await ControlPlaneAccountClient.ReadRefreshResultAsync(response, CancellationToken.None);

        Assert.True(result.TokenRejected);
        Assert.False(result.Ok);
    }

    // A control plane that has not deployed /auth/refresh yet answers 404. Discarding the
    // stored token on that would sign the user out for a deploy-order reason.
    [Fact]
    public async Task ReadRefreshResult_DoesNotDiscardTheTokenWhenTheEndpointIsMissing()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("Cannot POST /auth/refresh")
        };

        ControlPlaneRefreshResult result = await ControlPlaneAccountClient.ReadRefreshResultAsync(response, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Rejected, result.Outcome);
        Assert.False(result.TokenRejected);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ReadRefreshResult_DoesNotDiscardTheTokenWhenTheServerErrs()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("maintenance")
        };

        ControlPlaneRefreshResult result = await ControlPlaneAccountClient.ReadRefreshResultAsync(response, CancellationToken.None);

        Assert.False(result.TokenRejected);
        Assert.False(result.Ok);
    }
}
