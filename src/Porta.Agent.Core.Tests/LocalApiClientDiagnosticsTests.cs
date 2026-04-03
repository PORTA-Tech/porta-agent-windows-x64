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
