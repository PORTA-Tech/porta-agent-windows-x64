using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class LocalApiClientDiagnosticsTests
{
    [Fact]
    public async Task SendInputSignalsAsync_KeepsHttpStatusAndRawBody_ForJsonAckFailures()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                """{"success":false,"message":"input_signals_source_heartbeat_missing","timestampUtc":"2026-04-03T10:00:00Z"}""",
                Encoding.UTF8,
                "application/json")
        });

        LocalApiAckResponse? ack = await client.SendInputSignalsAsync(CreateRequest());

        Assert.NotNull(ack);
        Assert.False(ack.Success);
        Assert.Contains("kind=http_error", ack.Message, StringComparison.Ordinal);
        Assert.Contains("status=409", ack.Message, StringComparison.Ordinal);
        Assert.Contains("ack=input_signals_source_heartbeat_missing", ack.Message, StringComparison.Ordinal);
        Assert.Contains("body=", ack.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInputSignalsAsync_KeepsHttpStatusAndRawBody_ForServerErrors()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("database busy", Encoding.UTF8, "text/plain")
        });

        LocalApiAckResponse? ack = await client.SendInputSignalsAsync(CreateRequest());

        Assert.NotNull(ack);
        Assert.False(ack.Success);
        Assert.Contains("kind=server_error", ack.Message, StringComparison.Ordinal);
        Assert.Contains("status=503", ack.Message, StringComparison.Ordinal);
        Assert.Contains("body=database busy", ack.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInputSignalsAsync_ReturnsTimeoutDiagnostics_InsteadOfNull()
    {
        using var client = CreateClient(_ => throw new TaskCanceledException("request timed out"));

        LocalApiAckResponse? ack = await client.SendInputSignalsAsync(CreateRequest());

        Assert.NotNull(ack);
        Assert.False(ack.Success);
        Assert.Contains("kind=timeout", ack.Message, StringComparison.Ordinal);
        Assert.Contains("detail=request timed out", ack.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInputSignalsAsync_ReturnsTransportDiagnostics_InsteadOfNull()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        LocalApiAckResponse? ack = await client.SendInputSignalsAsync(CreateRequest());

        Assert.NotNull(ack);
        Assert.False(ack.Success);
        Assert.Contains("kind=transport_error", ack.Message, StringComparison.Ordinal);
        Assert.Contains("detail=connection refused", ack.Message, StringComparison.Ordinal);
    }

    // ── the narrative route: the last blind spot in the fail-soft chain ──
    //
    // Every other layer already hands a reason code up (ControlPlaneClient, NarrativeGateway), but
    // GetNarrativeAsync ended in catch { return null; }, and the host reported every one of these
    // as the single code "local_api_no_response" — which reads as "the local service was silent"
    // when in fact the service was up and something specific went wrong.

    [Fact]
    public async Task GetNarrativeAsync_Timeout_NamesTheBudgetThatRanOut_NotABareNull()
    {
        using var client = CreateClient(_ => throw new TaskCanceledException("timed out"));

        LocalApiNarrativeResponse? response = await client.GetNarrativeAsync(NarrativeRequest());

        Assert.NotNull(response);
        Assert.Null(response!.Line);
        // The client's own timeout is far shorter than the chain behind this route, so naming the
        // number is the whole point: a bare "timeout" would still not say which budget.
        Assert.Equal("local_api_timeout:3s", response.Reason);
    }

    [Fact]
    public async Task GetNarrativeAsync_HttpError_CarriesTheStatusCode()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream failed", Encoding.UTF8, "text/plain")
        });

        LocalApiNarrativeResponse? response = await client.GetNarrativeAsync(NarrativeRequest());

        Assert.Equal("local_api_http_502", response!.Reason);
    }

    [Fact]
    public async Task GetNarrativeAsync_TransportFailure_CarriesTheExceptionType()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("connection refused"));

        LocalApiNarrativeResponse? response = await client.GetNarrativeAsync(NarrativeRequest());

        Assert.Equal("local_api_transport:HttpRequestException", response!.Reason);
    }

    [Fact]
    public async Task GetNarrativeAsync_CallerCancelled_Propagates_SoTheHostReportsItsOwnBudget()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = CreateClient(_ => throw new OperationCanceledException());

        // Blaming the local API for a deadline the caller set would send the next investigation
        // to the wrong machine.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetNarrativeAsync(NarrativeRequest(), cts.Token));
    }

    [Fact]
    public async Task GetNarrativeAsync_Success_ReturnsTheLineAndNoReason()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"success":true,"line":"Yesterday you left the pricing page unfinished.","timestampUtc":"2026-08-08T08:06:12Z","reason":""}""",
                Encoding.UTF8,
                "application/json")
        });

        LocalApiNarrativeResponse? response = await client.GetNarrativeAsync(NarrativeRequest());

        Assert.Equal("Yesterday you left the pricing page unfinished.", response!.Line);
        Assert.Equal(string.Empty, response.Reason);
    }

    [Fact]
    public async Task GetNarrativeAsync_EmptyBody_IsDistinguishableFromADeclinedLine()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        });

        LocalApiNarrativeResponse? response = await client.GetNarrativeAsync(NarrativeRequest());

        Assert.Equal("local_api_empty_body", response!.Reason);
    }

    // The defect itself, guarded: the narrative route must not run under the 3s budget that is
    // correct for every other local call. 3s sat below the host's 10s token, the gateway's 9s and
    // the server's 8s, so it was always the binding constraint and a line could only land if the
    // whole chain — local API, network, Render, Opus — beat three seconds. Roughly seven check-ins
    // in ten failed at ~2.9-3.0s for four rounds of investigation that all looked server-side.
    [Fact]
    public void NarrativeRoute_RunsOnALongerBudgetThanTheOtherLocalCalls()
    {
        using var client = new LocalApiClient(httpClient: null, settingsOverride: null, endpointOverride: null);

        TimeSpan shared = ReadTimeout(client, "_httpClient");
        TimeSpan narrative = ReadTimeout(client, "_narrativeHttpClient");

        Assert.Equal(TimeSpan.FromSeconds(3), shared);
        // Above the host's 10s cancellation token on purpose: the host budget is meant to be the
        // ceiling, and this one must never be the binding constraint again.
        Assert.True(
            narrative > TimeSpan.FromSeconds(10),
            $"the narrative budget must sit above the host's 10s ceiling, but was {narrative.TotalSeconds:F0}s");
    }

    private static TimeSpan ReadTimeout(LocalApiClient client, string fieldName)
    {
        System.Reflection.FieldInfo field = typeof(LocalApiClient).GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{fieldName} not found — the client was restructured.");
        return ((HttpClient)field.GetValue(client)!).Timeout;
    }

    private static LocalApiNarrativeRequest NarrativeRequest()
        => new(SchemaVer: 1, Kind: "morning", UserText: null);

    private static LocalApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        return new LocalApiClient(
            httpClient,
            new LocalApiRuntimeSettings(
                Port: 17887,
                AuthToken: "ABC123",
                LocalApiAuthReady: true,
                LocalApiAuthError: null,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                UpdatedAtUtc: DateTimeOffset.UtcNow),
            new LocalApiEndpointSettings(
                Host: "127.0.0.1",
                Port: 17887,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
    }

    private static LocalApiInputSignalsRequest CreateRequest()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        return new LocalApiInputSignalsRequest(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: nowUtc.AddSeconds(-5),
            WindowEndUtc: nowUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0,
            MouseMoveDy: 0,
            FocusAppHash: null,
            IdleSeconds: 0,
            LastInputEventUtc: nowUtc);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
