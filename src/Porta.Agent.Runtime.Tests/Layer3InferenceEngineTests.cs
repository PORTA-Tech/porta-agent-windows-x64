using System.Text.Json;
using Porta.Agent.Runtime.Layer3;

namespace Porta.Agent.Runtime.Tests;

public sealed class Layer3InferenceEngineTests
{
    [Fact]
    public void Evaluate_HighOnlyMetricDoesNotTreatLowDeviationAsRisk()
    {
        var engine = new Layer3InferenceEngine(new Layer3InferenceConfig());
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-12T10:00:00Z");
        engine.RecordPrimitive(BuildPrimitive("system.cpu_utilization", zScore: -6d, confidence: 0.9d, now));

        Layer3StateResult result = engine.Evaluate(now);

        Assert.True(result.Success);
        Assert.Equal("stable", result.StateName);
        Assert.Equal(0d, result.GlobalRiskScore, precision: 6);
    }

    [Fact]
    public void Evaluate_LowOnlyMetricTreatsNegativeDeviationAsRisk()
    {
        var engine = new Layer3InferenceEngine(new Layer3InferenceConfig());
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-12T10:05:00Z");
        engine.RecordPrimitive(BuildPrimitive("system.service_health_status", zScore: -6d, confidence: 0.9d, now));

        Layer3StateResult result = engine.Evaluate(now);

        Assert.True(result.Success);
        Assert.Equal("critical", result.StateName);
        Assert.Equal(1d, result.GlobalRiskScore, precision: 6);
    }

    [Fact]
    public void Evaluate_RenormalizesMissingnessAndScalesConfidenceByCoverage()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-03-12T10:10:00Z");
        var sparse = new Layer3InferenceEngine(new Layer3InferenceConfig());
        sparse.RecordPrimitive(BuildPrimitive("system.cpu_utilization", zScore: 6d, confidence: 0.8d, now));
        Layer3StateResult sparseResult = sparse.Evaluate(now);

        var full = new Layer3InferenceEngine(new Layer3InferenceConfig());
        foreach (string metric in new Layer3InferenceConfig().MetricRiskSpecs.Keys)
        {
            full.RecordPrimitive(BuildPrimitive(metric, zScore: 0d, confidence: 0.8d, now));
        }

        Layer3StateResult fullResult = full.Evaluate(now);
        using JsonDocument explanation = JsonDocument.Parse(sparseResult.ExplanationJson);
        JsonElement riskBreakdown = explanation.RootElement.GetProperty("risk_breakdown");
        JsonElement evidence = explanation.RootElement.GetProperty("evidence");

        Assert.True(sparseResult.Success);
        Assert.Equal(1d, sparseResult.GlobalRiskScore, precision: 6);
        Assert.Equal(0.11d, sparseResult.EvidenceCoverage, precision: 6);
        Assert.True(sparseResult.Confidence < fullResult.Confidence);
        Assert.Equal(0.11d, riskBreakdown.GetProperty("evidence_coverage").GetDouble(), precision: 6);
        Assert.Equal(1, evidence.GetProperty("present_metric_count").GetInt32());
        Assert.Equal(13, evidence.GetProperty("missing_metric_count").GetInt32());
        Assert.Equal(14, evidence.GetProperty("total_metric_count").GetInt32());
    }

    private static Layer3PrimitiveInput BuildPrimitive(string metricName, double zScore, double confidence, DateTimeOffset atUtc)
    {
        return new Layer3PrimitiveInput(
            PrimitiveId: $"primitive-{metricName}",
            CorrelationId: $"corr-{metricName}",
            MachineId: "machine-a",
            Name: metricName,
            Value: 1d,
            Unit: "unit",
            ZScore: zScore,
            Ewma: 1d,
            Confidence: confidence,
            ComputedAtUtc: atUtc);
    }
}
