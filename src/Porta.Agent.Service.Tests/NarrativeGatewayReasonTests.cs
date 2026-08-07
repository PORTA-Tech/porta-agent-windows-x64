using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Tests;

// The narrative path is fail-soft at four layers, and every one of them used to collapse its
// failure into a bare null. That made "the model declined" indistinguishable from "the request
// never left the machine" at the only durable log in the chain, and cost a full session of
// inference on 2026-08-07.
//
// These pin the contract that makes the cause readable: a line carries no reason, and an absent
// line always carries one. A reason code that goes missing puts us straight back to guessing, so
// it is worth a test of its own.
public sealed class NarrativeGatewayReasonTests
{
    [Fact]
    public void Ok_CarriesTheLineAndNoReason()
    {
        NarrativeGatewayResult result = NarrativeGatewayResult.Ok("Yesterday you didn't finish the pricing page.");

        Assert.Equal("Yesterday you didn't finish the pricing page.", result.Line);
        Assert.Empty(result.Reason);
    }

    [Fact]
    public void Failed_CarriesNoLineAndAReason()
    {
        NarrativeGatewayResult result = NarrativeGatewayResult.Failed("http_401");

        Assert.Null(result.Line);
        Assert.Equal("http_401", result.Reason);
    }

    // The distinctions that actually matter when reading a morning's log: an auth rejection, a
    // payload rejection, a server that answered emptily, a budget that ran out, and a call that
    // never left the machine must not look alike.
    [Theory]
    [InlineData("http_401")]
    [InlineData("http_400")]
    [InlineData("http_200_empty_line")]
    [InlineData("gateway_budget_exceeded:9s")]
    [InlineData("agent_budget_exceeded:10s")]
    [InlineData("client_disabled:no_backend_url")]
    [InlineData("identity_not_linked:unlinked")]
    [InlineData("local_api_no_response")]
    [InlineData("transport:HttpRequestException")]
    public void EveryFailureReasonIsDistinctAndNonEmpty(string reason)
    {
        NarrativeGatewayResult result = NarrativeGatewayResult.Failed(reason);

        Assert.Null(result.Line);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(reason, result.Reason);
    }

    // The reason has to survive the LocalApi hop — that is the boundary it crosses to reach the
    // only component with a durable log.
    [Fact]
    public void ReasonSurvivesTheLocalApiResponseContract()
    {
        var response = new LocalApiNarrativeResponse(
            Success: true, Line: null, TimestampUtc: DateTimeOffset.UtcNow, Reason: "http_401");

        Assert.Null(response.Line);
        Assert.Equal("http_401", response.Reason);
    }

    [Fact]
    public void ALineOverTheContractCarriesNoReason()
    {
        var response = new LocalApiNarrativeResponse(
            Success: true, Line: "Here's where things stood.", TimestampUtc: DateTimeOffset.UtcNow);

        Assert.Equal("Here's where things stood.", response.Line);
        Assert.Empty(response.Reason);
    }
}
