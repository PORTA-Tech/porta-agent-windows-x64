using System.Text.Json;
using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class StateBucketJsonContractsTests
{
    [Fact]
    public void BuildOverloadContributorsJson_ContainsTopTwoKeysByWeightedContribution()
    {
        var overload = new OverloadInferenceResult(
            OverloadLikelihood: 0.42d,
            Confidence: 0.66d,
            FComponent: 0.55d,
            IComponent: 0.48d,
            VComponent: 0.37d,
            FTopPrimitives:
            [
                new OverloadTopPrimitive("focus_switch_rate", 0.50d, 0.50d, 0.25d),
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.15d, 0.135d)
            ],
            ITopPrimitives:
            [
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.55d, 0.495d),
                new OverloadTopPrimitive("scroll_velocity_index", 0.10d, 0.30d, 0.03d)
            ],
            VTopPrimitives:
            [
                new OverloadTopPrimitive("focus_switch_rate", 0.50d, 0.30d, 0.15d),
                new OverloadTopPrimitive("typing_burst_intensity", 0.90d, 0.15d, 0.135d)
            ],
            Quality: 0.82d,
            Agreement: 0.74d,
            PrimitiveCoverage: 1.0d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: 1);

        string json = SqliteBrainStateStore.BuildOverloadContributorsJson(["test"], overload);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        Assert.Equal(0.82d, root.GetProperty("quality").GetDouble(), precision: 6);
        Assert.Equal(0.74d, root.GetProperty("agreement").GetDouble(), precision: 6);

        JsonElement fTop = root.GetProperty("components").GetProperty("F").GetProperty("top");
        Assert.Equal(2, fTop.GetArrayLength());
        Assert.Equal("focus_switch_rate", fTop[0].GetProperty("key").GetString());
        Assert.Equal("typing_burst_intensity", fTop[1].GetProperty("key").GetString());
    }

    [Fact]
    public void BuildStateBucketStateFlagsJson_WritesPinnedFlags()
    {
        string json = SqliteBrainStateStore.BuildStateBucketStateFlagsJson(
            coldStart: true,
            partial: true,
            saResetReason: "context_changed",
            overloadInferenceSource: "bucket_finalizer",
            overloadActive: true,
            lowQuality: true,
            dwellOnMs: 15000,
            dwellOffMs: 5000,
            gapMs: 10000,
            overloadStateDebug: new
            {
                mode = "stable_hold",
                previous_overload_active = false
            });
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("schema_ver").GetInt32());
        Assert.Equal(1, root.GetProperty("cold_start").GetInt32());
        Assert.Equal(1, root.GetProperty("partial").GetInt32());
        Assert.Equal("context_changed", root.GetProperty("sa_reset_reason").GetString());
        Assert.True(root.GetProperty("overload_active").GetBoolean());
        Assert.True(root.GetProperty("low_quality").GetBoolean());
        Assert.Equal(15000, root.GetProperty("dwell_on_ms").GetInt64());
        Assert.Equal(5000, root.GetProperty("dwell_off_ms").GetInt64());
        Assert.Equal(10d, root.GetProperty("gap_s").GetDouble(), precision: 6);
        Assert.Equal("stable_hold", root.GetProperty("overload_state_debug").GetProperty("mode").GetString());
    }
}
