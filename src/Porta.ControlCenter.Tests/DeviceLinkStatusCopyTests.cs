using System.Net;
using System.Net.Http;
using Porta.ControlCenter;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter.Tests;

public sealed class DeviceLinkStatusCopyTests
{
    [Fact]
    public async Task ReadDeviceCallResult_TreatsUnauthorizedAsItsOwnOutcome()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"unauthorized\",\"error_code\":\"SESSION_EXPIRED\"}")
        };

        ControlPlaneDeviceCallResult result = await ControlPlaneAccountClient
            .ReadDeviceCallResultAsync(response, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Unauthorized, result.Outcome);
        Assert.True(result.SessionRejected);
        Assert.False(result.Ok);
    }

    // The whole point of the change: a 401 and a refused-but-reachable call must not
    // land on the same outcome, because only one of them is the user's to fix.
    [Fact]
    public async Task ReadDeviceCallResult_SeparatesRejectionFromUnauthorized()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error_code\":\"DEVICE_LINK_MISMATCH\"}")
        };

        ControlPlaneDeviceCallResult result = await ControlPlaneAccountClient
            .ReadDeviceCallResultAsync(response, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Rejected, result.Outcome);
        Assert.False(result.SessionRejected);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task ReadDeviceCallResult_ReportsSuccessOnlyWhenTheBodySaysOk()
    {
        using var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"ok\":true,\"linkState\":\"linked\",\"device\":{\"deviceId\":\"d1\",\"status\":\"active\"}}")
        };

        ControlPlaneDeviceCallResult ok = await ControlPlaneAccountClient
            .ReadDeviceCallResultAsync(okResponse, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Success, ok.Outcome);
        Assert.True(ok.Ok);

        using var notOkResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"ok\":false,\"linkState\":\"unlinked\",\"device\":{\"deviceId\":\"d1\",\"status\":\"active\"}}")
        };

        ControlPlaneDeviceCallResult notOk = await ControlPlaneAccountClient
            .ReadDeviceCallResultAsync(notOkResponse, CancellationToken.None);

        Assert.Equal(ControlPlaneCallOutcome.Success, notOk.Outcome);
        Assert.False(notOk.Ok);
    }

    [Fact]
    public void UnreachableIsNeverOkAndNeverAsksForReSignIn()
    {
        ControlPlaneDeviceCallResult unreachable = ControlPlaneDeviceCallResult.Unreachable;

        Assert.False(unreachable.Ok);
        Assert.False(unreachable.SessionRejected);
        Assert.Equal(ControlPlaneCallOutcome.Unreachable, unreachable.Outcome);
    }

    [Fact]
    public void OnlyUnauthorizedAsksTheUserToSignInAgain()
    {
        Assert.True(DeviceLinkStatusCopy.RequiresReSignIn(ControlPlaneCallOutcome.Unauthorized));
        Assert.False(DeviceLinkStatusCopy.RequiresReSignIn(ControlPlaneCallOutcome.Rejected));
        Assert.False(DeviceLinkStatusCopy.RequiresReSignIn(ControlPlaneCallOutcome.Unreachable));
        Assert.False(DeviceLinkStatusCopy.RequiresReSignIn(ControlPlaneCallOutcome.Success));
    }

    // The enum is internal, so these iterate in-body rather than using [InlineData].
    private static readonly DeviceLinkOperation[] AllOperations =
    {
        DeviceLinkOperation.LinkCurrentDevice,
        DeviceLinkOperation.UnlinkCurrentDevice,
        DeviceLinkOperation.UnlinkManagedDevice,
        DeviceLinkOperation.RevokeManagedDevice
    };

    [Fact]
    public void ExpiredSignInAlwaysTellsTheUserToSignInAgain()
    {
        foreach (DeviceLinkOperation operation in AllOperations)
        {
            string message = DeviceLinkStatusCopy.BuildFailureMessage(
                operation,
                ControlPlaneCallOutcome.Unauthorized);

            Assert.Contains("Sign in again", message, StringComparison.Ordinal);
            Assert.Contains("expired", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachOutcomeReadsDifferently()
    {
        foreach (DeviceLinkOperation operation in AllOperations)
        {
            string unauthorized = DeviceLinkStatusCopy.BuildFailureMessage(operation, ControlPlaneCallOutcome.Unauthorized);
            string unreachable = DeviceLinkStatusCopy.BuildFailureMessage(operation, ControlPlaneCallOutcome.Unreachable);
            string rejected = DeviceLinkStatusCopy.BuildFailureMessage(operation, ControlPlaneCallOutcome.Rejected);

            Assert.NotEqual(unauthorized, unreachable);
            Assert.NotEqual(unauthorized, rejected);
            Assert.NotEqual(unreachable, rejected);
        }
    }

    // An offline unlink must not imply the device came off the account.
    [Fact]
    public void UnreachableUnlinkSaysTheDeviceIsStillLinked()
    {
        string message = DeviceLinkStatusCopy.BuildFailureMessage(
            DeviceLinkOperation.UnlinkCurrentDevice,
            ControlPlaneCallOutcome.Unreachable);

        Assert.Contains("still linked", message, StringComparison.Ordinal);
        Assert.Contains("Could not reach the account server", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedUnlinkDoesNotClaimTheDeviceWasUnlinked()
    {
        string message = DeviceLinkStatusCopy.BuildFailureMessage(
            DeviceLinkOperation.UnlinkCurrentDevice,
            ControlPlaneCallOutcome.Rejected);

        Assert.Contains("still linked", message, StringComparison.Ordinal);
    }
}
