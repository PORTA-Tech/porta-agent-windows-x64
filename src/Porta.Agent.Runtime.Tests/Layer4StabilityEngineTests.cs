using System.Text.Json;
using Porta.Agent.Runtime.Layer3;
using Porta.Agent.Runtime.Layer4;

namespace Porta.Agent.Runtime.Tests;

public sealed class Layer4StabilityEngineTests
{
    [Fact]
    public void Stabilize_PreservesCoverageAndCarriesItIntoExplanation()
    {
        var engine = new Layer4StabilityEngine(new Layer4StabilityConfig());
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-12T10:20:00Z");
        Layer3StateResult input = new(
            Success: true,
            StateId: "state-1",
            CorrelationId: "corr-1",
            StateName: "stable",
            Probability: 0.9d,
            Confidence: 0.8d,
            EvidenceCoverage: 0.35d,
            GlobalRiskScore: 0.1d,
            RiskDelta: 0d,
            TrendDirection: "flat",
            ModelVersion: "deterministic-v1.1.0",
            DistributionJson: "{\"stable\":1}",
            ContributingPrimitivesJson: "[]",
            ExplanationJson: "{}",
            EvaluatedAtUtc: now,
            ErrorReason: string.Empty);

        Layer4StateResult result = engine.Stabilize(input);
        using JsonDocument explanation = JsonDocument.Parse(result.ExplanationJson);
        JsonElement layer4 = explanation.RootElement.GetProperty("layer4_stability");

        Assert.Equal(0.35d, result.EvidenceCoverage, precision: 6);
        Assert.Equal(0.4d, result.Confidence, precision: 6);
        Assert.Equal(0.35d, layer4.GetProperty("evidence_coverage").GetDouble(), precision: 6);
        Assert.Equal("cold_start_passthrough", result.TransitionReason);
    }
}
