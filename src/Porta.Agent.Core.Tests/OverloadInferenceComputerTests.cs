using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class OverloadInferenceComputerTests
{
    [Fact]
    public void ComputeForFinalizedBucket_UsesComponentFallback_WhenSnapshotValuesAreNonFinite()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: double.NaN, confidence: double.NaN);
        InputBucketRow bucket = BuildBucket(quality: 0.80d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.90d, scroll: 0.10d, focus: 0.50d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.Equal(0.441d, result.OverloadLikelihood, precision: 6);
        Assert.Equal(0.50d, result.Confidence, precision: 6);
        Assert.Equal(0.20d, result.Agreement, precision: 6);
        Assert.Equal(1.0d, result.PrimitiveCoverage, precision: 6);
        Assert.Equal(0, result.MissingPrimitiveCount);
        Assert.False(result.UsedSnapshotFallback);
        Assert.Equal("bucket_finalizer", result.Source);
        Assert.Equal(OverloadComponentsRegistry.RegistryVersion, result.ComponentsRegistryVersion);
        Assert.Collection(
            result.FTopPrimitives,
            first =>
            {
                Assert.Equal(PrimitiveRegistry.FocusSwitchRate.PrimitiveKey, first.Key);
                Assert.Equal(0.25d, first.Contribution, precision: 6);
            },
            second =>
            {
                Assert.Equal(PrimitiveRegistry.TypingBurstIntensity.PrimitiveKey, second.Key);
                Assert.Equal(0.135d, second.Contribution, precision: 6);
            });
    }

    [Fact]
    public void ComputeForFinalizedBucket_KnownPrimitives_ComputesWeightedFIVAndOverload()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0d, confidence: 0d);
        InputBucketRow bucket = BuildBucket(quality: 0.70d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.80d, scroll: 0.20d, focus: 0.60d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.Equal(0.49d, result.FComponent, precision: 6);
        Assert.Equal(0.59d, result.IComponent, precision: 6);
        Assert.Equal(0.41d, result.VComponent, precision: 6);
        Assert.Equal(0.491d, result.OverloadLikelihood, precision: 6);
        Assert.Equal(0.552667d, result.PressureLevel, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_UsesAbsoluteStrainFloor_ForExpressivePressure()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0d, confidence: 0d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d) with
        {
            FocusSwitchCount = 6,
            FocusAppEntropy = 2.6d,
            FocusActiveAppCount = 5d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.15d, scroll: 0.18d, focus: 0.21d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.Equal(0.183075d, result.OverloadLikelihood, precision: 6);
        Assert.Equal(PortaExecutionConstants.OverloadInference.PressureAbsoluteHardFloor, result.PressureLevel, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_CanReportHighPressure_WhileKeepingConservativeOverloadBelowActivation()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0d, confidence: 0d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d) with
        {
            FocusSwitchCount = 6,
            FocusAppEntropy = 2.6d,
            FocusActiveAppCount = 5d
        };
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.15d, scroll: 0.18d, focus: 0.21d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.True(result.PressureLevel >= PortaExecutionConstants.OverloadStabilization.ThresholdOn);
        Assert.True(result.OverloadLikelihood < PortaExecutionConstants.OverloadStabilization.ThresholdOn);
        Assert.True(result.PressureLevel > result.OverloadLikelihood);
    }

    [Fact]
    public void ComputeForFinalizedBucket_CapsConfidenceDuringColdStart()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0.70d, confidence: 0.90d);
        InputBucketRow bucket = BuildBucket(quality: 0.95d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.3d, scroll: 0.4d, focus: 0.5d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Cold(guidanceEnabled: true),
            robust: null);

        Assert.Equal(0.41025d, result.OverloadLikelihood, precision: 6);
        Assert.Equal(PortaExecutionConstants.BaselineReadiness.ColdStartConfidenceCap, result.Confidence, precision: 6);
        Assert.False(result.UsedSnapshotFallback);
    }

    [Fact]
    public void ComputeForFinalizedBucket_UsesPinnedPrimitiveRegistry_WhenRobustIsProvided()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: double.NaN, confidence: double.NaN);
        InputBucketRow bucket = BuildBucket(quality: 0.70d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.1d, scroll: 0.2d, focus: 0.3d);
        var robust = new RobustNormalizationSnapshot(
            Metrics: new Dictionary<string, MetricNormalizationValue>(),
            TypingComponent: 0.20d,
            ScrollComponent: 0.80d,
            FocusComponent: 0.40d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust);

        Assert.Equal(0.21025d, result.OverloadLikelihood, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_MissingPrimitive_ReducesAgreement()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0.30d, confidence: 0.80d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d);
        PrimitiveComputationResult allAvailable = BuildPrimitive(typing: 0.10d, scroll: 0.20d, focus: 0.30d);
        PrimitiveComputationResult missingFocus = new PrimitiveComputationResult(
            TypingRaw: 0.10d,
            ScrollRaw: 0.20d,
            FocusRaw: 0.30d,
            TypingSmoothed: 0.10d,
            ScrollSmoothed: 0.20d,
            FocusSmoothed: 0.27d, // Step 9 decay behavior keeps a non-zero smoothed signal.
            TypingChannelAvailable: true,
            ScrollChannelAvailable: true,
            FocusChannelAvailable: false);

        OverloadInferenceResult full = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            allAvailable,
            BaselineGatingResult.Warm,
            robust: null);
        OverloadInferenceResult missing = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            missingFocus,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.True(missing.Agreement < full.Agreement);
        Assert.Equal(0.8d, full.Agreement, precision: 6);
        Assert.Equal(0.553333d, missing.Agreement, precision: 6);
        Assert.Equal(0.666667d, missing.PrimitiveCoverage, precision: 6);
        Assert.Equal(1, missing.MissingPrimitiveCount);
        Assert.True(missing.IComponent > 0d); // missing primitive still contributes via decayed smoothed value.
    }

    [Fact]
    public void ComputeForFinalizedBucket_AgreementDropsWhenSpreadIncreases()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0d, confidence: 0d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d);
        PrimitiveComputationResult lowSpread = BuildPrimitive(typing: 0.50d, scroll: 0.52d, focus: 0.51d);
        PrimitiveComputationResult highSpread = BuildPrimitive(typing: 0.90d, scroll: 0.10d, focus: 0.50d);

        OverloadInferenceResult low = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            lowSpread,
            BaselineGatingResult.Warm,
            robust: null);
        OverloadInferenceResult high = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            highSpread,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.True(low.Agreement > high.Agreement);
        Assert.Equal(0.98d, low.Agreement, precision: 6);
        Assert.Equal(0.20d, high.Agreement, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ConfidenceIsAverageAndCappedInColdStart()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0d, confidence: 0d);
        InputBucketRow bucket = BuildBucket(quality: 0.60d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.50d, scroll: 0.50d, focus: 0.50d);

        OverloadInferenceResult warm = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);
        OverloadInferenceResult cold = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Cold(guidanceEnabled: true),
            robust: null);

        Assert.Equal(1.0d, warm.Agreement, precision: 6);
        Assert.Equal(0.8d, warm.Confidence, precision: 6); // (0.6 + 1.0) / 2
        Assert.Equal(PortaExecutionConstants.BaselineReadiness.ColdStartConfidenceCap, cold.Confidence, precision: 6);
    }

    [Fact]
    public void ComputeForFinalizedBucket_SelectsTopTwoPerComponentByWeightedContribution()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0.0d, confidence: 0.0d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d);
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.90d, scroll: 0.10d, focus: 0.50d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.Equal(2, result.FTopPrimitives.Count);
        Assert.Equal(2, result.ITopPrimitives.Count);
        Assert.Equal(2, result.VTopPrimitives.Count);
        Assert.True(result.FTopPrimitives[0].Contribution >= result.FTopPrimitives[1].Contribution);
        Assert.True(result.ITopPrimitives[0].Contribution >= result.ITopPrimitives[1].Contribution);
        Assert.True(result.VTopPrimitives[0].Contribution >= result.VTopPrimitives[1].Contribution);
    }

    [Fact]
    public void ComputeForFinalizedBucket_PartialBucket_AppliesAgreementPenalty()
    {
        AgentBrainSnapshot snapshot = BuildSnapshot(overload: 0.20d, confidence: 0.80d);
        InputBucketRow bucket = BuildBucket(quality: 0.80d) with
        {
            Partial = 1
        };
        PrimitiveComputationResult primitive = BuildPrimitive(typing: 0.20d, scroll: 0.20d, focus: 0.20d);

        OverloadInferenceResult result = OverloadInferenceComputer.ComputeForFinalizedBucket(
            snapshot,
            bucket,
            primitive,
            BaselineGatingResult.Warm,
            robust: null);

        Assert.Equal(0.8d, result.Agreement, precision: 6);
        Assert.Equal(0.8d, result.Confidence, precision: 6); // avg(quality=0.8, agreement=0.8)
    }

    [Fact]
    public void Registry_UsesPrimitiveRegistryKeysOnly()
    {
        HashSet<string> primitiveKeys = PrimitiveRegistry.Metrics
            .Select(static metric => metric.PrimitiveKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (OverloadComponentDefinition component in OverloadComponentsRegistry.Components)
        {
            foreach (OverloadComponentSource source in component.Sources)
            {
                Assert.Contains(source.PrimitiveKey, primitiveKeys);
            }
        }
    }

    private static AgentBrainSnapshot BuildSnapshot(double overload, double confidence)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.Parse("2026-03-03T00:00:00.000Z"),
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            SmoothedTypingBurstIntensity: 0d,
            SmoothedScrollVelocityIndex: 0d,
            SmoothedFocusSwitchRate: 0d,
            FocusStrength: 0.50d,
            OverloadLikelihood: overload,
            FatigueProbability: 0.20d,
            CognitiveStability: 0.80d,
            DominantStateValue: 0.50d,
            DominantStateName: "focus",
            Confidence: confidence,
            LastAction: "monitor_only",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: ["test"]);
    }

    private static InputBucketRow BuildBucket(double quality)
    {
        return new InputBucketRow(
            BucketSeq: 1,
            TimestampUtc: "2026-03-03T00:00:00.000Z",
            StartUtc: "2026-03-03T00:00:00.000Z",
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            KeyboardIkiMean: 0d,
            KeyboardIkiCv: 0d,
            KeyboardBurstRate: 0d,
            KeyboardBackspaceRate: 0d,
            MouseSpeedMean: 0d,
            MouseJerkP95: 0d,
            MouseScrollSpeed: 0d,
            MouseMicroCorrectionRate: 0d,
            FocusAppEntropy: 0d,
            FocusSwitchCount: 0,
            DominantAppHash: "app_hash_work",
            FocusActiveAppCount: 1d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            IdleMicroIdleSeconds: 0d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: quality,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static PrimitiveComputationResult BuildPrimitive(double typing, double scroll, double focus)
    {
        return new PrimitiveComputationResult(
            TypingRaw: typing,
            ScrollRaw: scroll,
            FocusRaw: focus,
            TypingSmoothed: typing,
            ScrollSmoothed: scroll,
            FocusSmoothed: focus,
            TypingChannelAvailable: true,
            ScrollChannelAvailable: true,
            FocusChannelAvailable: true);
    }
}
