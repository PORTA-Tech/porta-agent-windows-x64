using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class OverloadStabilizationComputerTests
{
    [Fact]
    public void ComputeForFinalizedBucket_ActivatesAfterPinnedOnDwell()
    {
        OverloadStabilizationRuntimeState state = default;
        OverloadStabilizationResult result = default;
        const int requiredBuckets = PortaExecutionConstants.OverloadStabilization.DwellOnSeconds / Time.bucketSeconds;
        for (int i = 1; i <= requiredBuckets; i++)
        {
            result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: i, quality: 0.40d),
                BuildInference(overload: 0.80d, confidence: 0.60d, quality: 0.40d),
                state);
            if (i < requiredBuckets)
            {
                Assert.False(result.OverloadActive);
                Assert.False(result.Transitioned);
            }

            state = result.RuntimeState;
        }

        Assert.True(result.OverloadActive);
        Assert.True(result.Transitioned);
        Assert.Equal("activated", result.Transition);
        Assert.Equal(Time.bucketStartEnd(requiredBuckets).endUtcMs, result.RuntimeState.LastTransitionTsMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_DeactivatesAfterPinnedOffDwell()
    {
        var activeState = new OverloadStabilizationRuntimeState(
            OverloadActive: true,
            OnDwellMs: 0,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(100).endUtcMs,
            LastBucketSeq: 100,
            LastTransitionTsMs: Time.bucketStartEnd(100).endUtcMs);
        OverloadStabilizationRuntimeState state = activeState;
        OverloadStabilizationResult result = default;
        const int requiredBuckets = PortaExecutionConstants.OverloadStabilization.DwellOffSeconds / Time.bucketSeconds;
        for (int i = 1; i <= requiredBuckets; i++)
        {
            long seq = 100 + i;
            result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: seq, quality: 0.40d),
                BuildInference(overload: 0.65d, confidence: 0.60d, quality: 0.40d),
                state);
            if (i < requiredBuckets)
            {
                Assert.True(result.OverloadActive);
                Assert.False(result.Transitioned);
            }

            state = result.RuntimeState;
        }

        Assert.False(result.OverloadActive);
        Assert.True(result.Transitioned);
        Assert.Equal("deactivated", result.Transition);
        Assert.Equal(Time.bucketStartEnd(100 + requiredBuckets).endUtcMs, result.RuntimeState.LastTransitionTsMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowQualityPausesAndDecaysByOneBucket()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 20_000,
            OffDwellMs: 10_000,
            LastGoodTsMs: Time.bucketStartEnd(50).endUtcMs,
            LastBucketSeq: 50,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 51, quality: 0.20d),
            BuildInference(overload: 0.95d, confidence: 0.90d, quality: 0.20d),
            previous);

        Assert.False(result.Transitioned);
        Assert.True(result.LowQualityPaused);
        Assert.Equal("quality_pause_decay", result.DebugMode);
        Assert.Equal(15_000, result.RuntimeState.OnDwellMs);
        Assert.Equal(5_000, result.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_GapAboveAllowResetsDwell()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 15_000,
            OffDwellMs: 5_000,
            LastGoodTsMs: Time.bucketStartEnd(10).endUtcMs,
            LastBucketSeq: 10,
            LastTransitionTsMs: 0);
        // 3 missing buckets => gap 15s > gap_allow(10s)
        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 14, quality: 0.90d),
            BuildInference(overload: 0.10d, confidence: 0.90d, quality: 0.90d),
            previous);

        Assert.True(result.GapExceeded);
        Assert.Equal(20_000, result.GapMs);
        Assert.Equal(0, result.RuntimeState.OnDwellMs);
        Assert.Equal(0, result.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ClampsNonFiniteDebugInputs()
    {
        InputBucketRow bucket = BuildBucket(bucketSeq: 1, quality: 0.50d);
        OverloadInferenceResult inference = BuildInference(overload: double.NaN, confidence: double.PositiveInfinity, quality: -1.0d);
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 0,
            OffDwellMs: 0,
            LastGoodTsMs: 0,
            LastBucketSeq: 0,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            bucket,
            inference,
            previous);

        Assert.False(result.OverloadActive);
        Assert.Equal(0d, result.OverloadLikelihood, precision: 6);
        Assert.Equal(0d, result.Confidence, precision: 6);
        Assert.Equal(0d, result.QualityScore, precision: 6);
        Assert.Equal("quality_pause_decay", result.DebugMode);
        Assert.False(result.Transitioned);
    }

    [Fact]
    public void ComputeForFinalizedBucket_IneligibleConfidence_HoldsDwellWithoutAccumulating()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 20_000,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(5).endUtcMs,
            LastBucketSeq: 5,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 6, quality: 0.90d),
            BuildInference(overload: 0.95d, confidence: 0.50d, quality: 0.90d),
            previous);

        Assert.False(result.Eligible);
        Assert.False(result.OnSignal);
        Assert.False(result.Transitioned);
        Assert.Equal("ineligible_hold", result.DebugMode);
        Assert.Equal(20_000, result.RuntimeState.OnDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_BetweenThresholds_HoldsDwellsSteady()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 25_000,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(8).endUtcMs,
            LastBucketSeq: 8,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 9, quality: 0.90d),
            BuildInference(overload: 0.70d, confidence: 0.90d, quality: 0.90d),
            previous);

        Assert.True(result.Eligible);
        Assert.True(result.BetweenThresholds);
        Assert.False(result.OnSignal);
        Assert.False(result.OffSignal);
        Assert.False(result.Transitioned);
        Assert.Equal("between_hold", result.DebugMode);
        Assert.Equal(25_000, result.RuntimeState.OnDwellMs);
        Assert.Equal(0, result.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_SingleBucketDipWithinGapAllow_DoesNotResetOnDwell()
    {
        var state = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 20_000,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(200).endUtcMs,
            LastBucketSeq: 200,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult dip = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 201, quality: 0.90d),
            BuildInference(overload: 0.70d, confidence: 0.90d, quality: 0.90d),
            state);
        Assert.False(dip.GapExceeded);
        Assert.Equal(20_000, dip.RuntimeState.OnDwellMs);

        OverloadStabilizationResult recover = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 202, quality: 0.90d),
            BuildInference(overload: 0.90d, confidence: 0.90d, quality: 0.90d),
            dip.RuntimeState);
        Assert.False(recover.GapExceeded);
        Assert.Equal(25_000, recover.RuntimeState.OnDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowQualitySeveralBuckets_DecaysByFiveSecondsPerBucket()
    {
        var state = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 30_000,
            OffDwellMs: 15_000,
            LastGoodTsMs: Time.bucketStartEnd(300).endUtcMs,
            LastBucketSeq: 300,
            LastTransitionTsMs: 0);

        for (int i = 1; i <= 3; i++)
        {
            OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: 300 + i, quality: 0.20d),
                BuildInference(overload: 0.95d, confidence: 0.95d, quality: 0.20d),
                state);
            Assert.True(result.LowQualityPaused);
            Assert.False(result.Transitioned);
            state = result.RuntimeState;
        }

        Assert.Equal(15_000, state.OnDwellMs);
        Assert.Equal(0, state.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowConfidenceBehavesAsGap_ResetsAfterGapAllow()
    {
        var state = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 15_000,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(400).endUtcMs,
            LastBucketSeq: 400,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult b401 = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 401, quality: 0.90d),
            BuildInference(overload: 0.95d, confidence: 0.50d, quality: 0.90d),
            state);
        Assert.False(b401.Eligible);
        Assert.False(b401.GapExceeded);
        Assert.Equal(15_000, b401.RuntimeState.OnDwellMs);

        OverloadStabilizationResult b402 = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 402, quality: 0.90d),
            BuildInference(overload: 0.95d, confidence: 0.50d, quality: 0.90d),
            b401.RuntimeState);
        Assert.False(b402.GapExceeded); // exactly 10s is allowed
        Assert.Equal(15_000, b402.RuntimeState.OnDwellMs);

        OverloadStabilizationResult b403 = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 403, quality: 0.90d),
            BuildInference(overload: 0.95d, confidence: 0.50d, quality: 0.90d),
            b402.RuntimeState);
        Assert.True(b403.GapExceeded); // 15s > 10s
        Assert.Equal(0, b403.RuntimeState.OnDwellMs);
        Assert.Equal(0, b403.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_EligibilityUpdatesLastGoodTs()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 10_000,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(20).endUtcMs,
            LastBucketSeq: 20,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 21, quality: 0.90d),
            BuildInference(overload: 0.70d, confidence: 0.90d, quality: 0.90d),
            previous);

        Assert.True(result.Eligible);
        Assert.Equal(Time.bucketStartEnd(21).endUtcMs, result.RuntimeState.LastGoodTsMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowQualityNearOnTransition_DoesNotActivate()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: false,
            OnDwellMs: 115_000,
            OffDwellMs: 5_000,
            LastGoodTsMs: Time.bucketStartEnd(30).endUtcMs,
            LastBucketSeq: 30,
            LastTransitionTsMs: 0);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 31, quality: 0.20d),
            BuildInference(overload: 0.95d, confidence: 0.95d, quality: 0.20d),
            previous);

        Assert.False(result.Eligible);
        Assert.True(result.LowQualityPaused);
        Assert.False(result.Transitioned);
        Assert.False(result.OverloadActive);
        Assert.Equal(110_000, result.RuntimeState.OnDwellMs);
        Assert.Equal(0, result.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowQualityNearOffTransition_DoesNotDeactivate()
    {
        var previous = new OverloadStabilizationRuntimeState(
            OverloadActive: true,
            OnDwellMs: 5_000,
            OffDwellMs: 55_000,
            LastGoodTsMs: Time.bucketStartEnd(40).endUtcMs,
            LastBucketSeq: 40,
            LastTransitionTsMs: Time.bucketStartEnd(35).endUtcMs);

        OverloadStabilizationResult result = OverloadStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 41, quality: 0.10d),
            BuildInference(overload: 0.30d, confidence: 0.95d, quality: 0.10d),
            previous);

        Assert.False(result.Eligible);
        Assert.True(result.LowQualityPaused);
        Assert.False(result.Transitioned);
        Assert.True(result.OverloadActive);
        Assert.Equal(0, result.RuntimeState.OnDwellMs);
        Assert.Equal(50_000, result.RuntimeState.OffDwellMs);
    }

    private static InputBucketRow BuildBucket(long bucketSeq, double quality)
    {
        string startUtc = Time.toIsoUtcMsString(Time.bucketStartEnd(bucketSeq).startUtcMs);
        return new InputBucketRow(
            BucketSeq: bucketSeq,
            TimestampUtc: startUtc,
            StartUtc: startUtc,
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
            DominantAppHash: string.Empty,
            FocusActiveAppCount: 0d,
            IdleSeconds: 0d,
            IdleMicroIdleCount: 0d,
            IdleMicroIdleSeconds: 0d,
            TrueBreakSeconds: 0d,
            TrueBreakFlag: 0,
            QualityScore: quality,
            Partial: 0,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static OverloadInferenceResult BuildInference(double overload, double confidence, double quality)
    {
        return new OverloadInferenceResult(
            OverloadLikelihood: overload,
            Confidence: confidence,
            FComponent: 0d,
            IComponent: 0d,
            VComponent: 0d,
            FTopPrimitives: [],
            ITopPrimitives: [],
            VTopPrimitives: [],
            Quality: quality,
            Agreement: 0d,
            PrimitiveCoverage: 0d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: 1);
    }
}
