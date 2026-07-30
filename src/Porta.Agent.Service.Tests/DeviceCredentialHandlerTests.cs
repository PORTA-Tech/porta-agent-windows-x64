using System.Net;
using Porta.Agent.Core.Security;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// The bug these exist for: the first implementation attached the credential inside one helper
/// method, and machine-summary, daily-stats, narrative, daily-digest, action-events and
/// user-context all build their requests inline and so never got it. Testing the handler proves
/// the attachment is a property of the pipeline, not of whichever method happened to be edited.
/// </summary>
public sealed class DeviceCredentialHandlerTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(StatusCode));
        }
    }

    private static readonly Secrets.DeviceCredentialReadResult Present =
        new(true, "c5645dbe-67b2-45e8-94d6-69ac9eb3e374", "test-secret-value", null);

    private static readonly Secrets.DeviceCredentialReadResult Missing =
        new(false, null, null, "DEVICE_CREDENTIAL_MISSING");

    // Every route the fleet client actually calls.
    public static IEnumerable<object[]> FleetRoutes() => new[]
    {
        new object[] { "api/agent/machine-summary" },
        new object[] { "api/agent/daily-stats" },
        new object[] { "api/agent/narrative" },
        new object[] { "api/agent/daily-digest" },
        new object[] { "api/agent/action-events" },
        new object[] { "api/agent/categories/resolve" },
        new object[] { "api/agent/user-context?accountId=abc" },
        new object[] { "api/device/register" }
    };

    [Theory]
    [MemberData(nameof(FleetRoutes))]
    public async Task EveryFleetRouteCarriesTheCredential(string route)
    {
        var recorder = new RecordingHandler();
        using var client = new HttpClient(new DeviceCredentialHandler(recorder, () => Present))
        {
            BaseAddress = new Uri("https://control-plane.test/")
        };

        await client.PostAsync(route, new StringContent("{}"));

        Assert.NotNull(recorder.Last);
        Assert.True(recorder.Last!.Headers.Contains("X-Porta-Device-Id"), $"{route} lost the device id header");
        Assert.True(recorder.Last.Headers.Contains("X-Porta-Device-Secret"), $"{route} lost the device secret header");
        Assert.Equal("c5645dbe-67b2-45e8-94d6-69ac9eb3e374", recorder.Last.Headers.GetValues("X-Porta-Device-Id").Single());
    }

    [Fact]
    public async Task GetRequestsCarryTheCredentialToo()
    {
        var recorder = new RecordingHandler();
        using var client = new HttpClient(new DeviceCredentialHandler(recorder, () => Present))
        {
            BaseAddress = new Uri("https://control-plane.test/")
        };

        await client.GetAsync("api/agent/user-context?accountId=abc");

        Assert.True(recorder.Last!.Headers.Contains("X-Porta-Device-Secret"));
    }

    // An unlinked device has no credential and never will. It must still be able to report.
    [Fact]
    public async Task NoCredentialSendsNoHeadersAndDoesNotThrow()
    {
        var recorder = new RecordingHandler();
        using var client = new HttpClient(new DeviceCredentialHandler(recorder, () => Missing))
        {
            BaseAddress = new Uri("https://control-plane.test/")
        };

        HttpResponseMessage response = await client.PostAsync("api/agent/machine-summary", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(recorder.Last!.Headers.Contains("X-Porta-Device-Id"));
        Assert.False(recorder.Last.Headers.Contains("X-Porta-Device-Secret"));
    }

    [Fact]
    public async Task A401DoesNotThrowAndStillReturnsTheResponse()
    {
        var recorder = new RecordingHandler { StatusCode = HttpStatusCode.Unauthorized };
        using var client = new HttpClient(new DeviceCredentialHandler(recorder, () => Present))
        {
            BaseAddress = new Uri("https://control-plane.test/")
        };

        HttpResponseMessage response = await client.PostAsync("api/agent/daily-stats", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HeadersAreNotDuplicatedOnRetryOfTheSameMessageShape()
    {
        var recorder = new RecordingHandler();
        using var client = new HttpClient(new DeviceCredentialHandler(recorder, () => Present))
        {
            BaseAddress = new Uri("https://control-plane.test/")
        };

        await client.PostAsync("api/agent/machine-summary", new StringContent("{}"));
        await client.PostAsync("api/agent/machine-summary", new StringContent("{}"));

        Assert.Single(recorder.Last!.Headers.GetValues("X-Porta-Device-Id"));
    }
}
