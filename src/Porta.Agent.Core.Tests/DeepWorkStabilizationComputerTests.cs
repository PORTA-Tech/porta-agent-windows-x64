using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class DeepWorkStabilizationComputerTests
{
    [Fact]
    public void ComputeForFinalizedBucket_ActivatesAfterPinnedOnDwell()
    {
        DeepWorkStabilizationRuntimeState state = default;
        DeepWorkStabilizationResult result = default;
        const int requiredBuckets = PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds / Time.bucketSeconds;
        int transitionCount = 0;
        for (int i = 1; i <= requiredBuckets; i++)
        {
            result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: i),
                BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
                BuildDerivedSession(sustainedAttentionSeconds: 1200d),
                BaselineGatingResult.Warm,
                state);
            if (i < requiredBuckets)
            {
                Assert.False(result.DeepWorkActive);
                Assert.False(result.Transitioned);
            }
            else
            {
                Assert.True(result.Transitioned);
            }

            if (result.Transitioned)
            {
                transitionCount++;
            }

            state = result.RuntimeState;
        }

        Assert.True(result.DeepWorkActive);
        Assert.True(result.Transitioned);
        Assert.Equal("activated", result.Transition);
        Assert.Equal(Time.bucketStartEnd(requiredBuckets).endUtcMs, result.RuntimeState.LastTransitionTsMs);
        Assert.Equal(Time.bucketStartEnd(requiredBuckets).endUtcMs, result.RuntimeState.DeepWorkStartedAtUtcMs);
        Assert.Equal(1, transitionCount);
    }

    [Fact]
    public void ComputeForFinalizedBucket_DeactivatesAfterPinnedOffDwell()
    {
        var activeState = new DeepWorkStabilizationRuntimeState(
            DeepWorkActive: true,
            OnDwellMs: 0,
            OffDwellMs: 0,
            LastGoodTsMs: Time.bucketStartEnd(100).endUtcMs,
            LastBucketSeq: 100,
            LastTransitionTsMs: Time.bucketStartEnd(100).endUtcMs,
            DeepWorkStartedAtUtcMs: Time.bucketStartEnd(100).endUtcMs);
        DeepWorkStabilizationRuntimeState state = activeState;
        DeepWorkStabilizationResult result = default;
        const int requiredBuckets = PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds / Time.bucketSeconds;
        for (int i = 1; i <= requiredBuckets; i++)
        {
            long seq = 100 + i;
            result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: seq),
                BuildInference(confidence: 0.80d, f: 0.80d, i: 0.55d, v: 0.80d),
                BuildDerivedSession(sustainedAttentionSeconds: 1200d),
                BaselineGatingResult.Warm,
                state);
            if (i < requiredBuckets)
            {
                Assert.True(result.DeepWorkActive);
                Assert.False(result.Transitioned);
            }

            state = result.RuntimeState;
        }

        Assert.False(result.DeepWorkActive);
        Assert.True(result.Transitioned);
        Assert.Equal("deactivated", result.Transition);
        Assert.Equal(Time.bucketStartEnd(100 + requiredBuckets).endUtcMs, result.RuntimeState.LastTransitionTsMs);
        Assert.Equal(0, result.RuntimeState.DeepWorkStartedAtUtcMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ColdStartBlocksEligibility()
    {
        DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.95d, f: 0.10d, i: 0.50d, v: 0.10d),
            BuildDerivedSession(sustainedAttentionSeconds: 2400d),
            BaselineGatingResult.Cold(guidanceEnabled: true),
            default);

        Assert.False(result.BaselineReadyGate);
        Assert.False(result.DeepWorkCandidate);
        Assert.False(result.DeepWorkExitCandidate);
        Assert.False(result.OnSignal);
        Assert.False(result.Transitioned);
        Assert.False(result.DeepWorkActive);
    }

    [Fact]
    public void ComputeForFinalizedBucket_GapAboveAllowResetsDwells()
    {
        var previous = new DeepWorkStabilizationRuntimeState(
            DeepWorkActive: false,
            OnDwellMs: 15_000,
            OffDwellMs: 5_000,
            LastGoodTsMs: Time.bucketStartEnd(10).endUtcMs,
            LastBucketSeq: 10,
            LastTransitionTsMs: 0,
            DeepWorkStartedAtUtcMs: 0);

        DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 15),
            BuildInference(confidence: 0.80d, f: 0.90d, i: 0.55d, v: 0.90d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            previous);

        Assert.True(result.GapExceeded);
        Assert.Equal(25_000, result.GapMs);
        Assert.Equal(0, result.RuntimeState.OnDwellMs);
        Assert.Equal(0, result.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_IntensityOutOfRangePreventsOnSignal()
    {
        DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.80d, f: 0.20d, i: 0.20d, v: 0.20d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            default);

        Assert.False(result.IntensityInRange);
        Assert.False(result.OnSignal);
        Assert.False(result.Transitioned);
    }

    [Fact]
    public void ComputeForFinalizedBucket_SingleBrokenEntryBucketWithinGap_DoesNotResetOnDwell()
    {
        DeepWorkStabilizationRuntimeState state = default;

        DeepWorkStabilizationResult b1 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            state);
        Assert.Equal(5_000, b1.RuntimeState.OnDwellMs);

        DeepWorkStabilizationResult b2 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 2),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            b1.RuntimeState);
        Assert.Equal(10_000, b2.RuntimeState.OnDwellMs);

        // One broken entry bucket (candidate=false), then recover within 10s.
        DeepWorkStabilizationResult b3 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 3),
            BuildInference(confidence: 0.80d, f: 0.70d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            b2.RuntimeState);
        Assert.False(b3.DeepWorkCandidate);
        Assert.False(b3.GapExceeded);
        Assert.Equal(10_000, b3.RuntimeState.OnDwellMs);

        DeepWorkStabilizationResult b4 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 4),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 1200d),
            BaselineGatingResult.Warm,
            b3.RuntimeState);
        Assert.False(b4.Transitioned);
        Assert.Equal(15_000, b4.RuntimeState.OnDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ActiveMode_RefreshesGapTimerFromCandidateSignal()
    {
        var state = new DeepWorkStabilizationRuntimeState(
            DeepWorkActive: true,
            OnDwellMs: 0,
            OffDwellMs: 5_000,
            LastGoodTsMs: Time.bucketStartEnd(100).endUtcMs,
            LastBucketSeq: 100,
            LastTransitionTsMs: Time.bucketStartEnd(95).endUtcMs,
            DeepWorkStartedAtUtcMs: Time.bucketStartEnd(95).endUtcMs);

        // Candidate remains true while active -> off candidate is false, treated as gap.
        DeepWorkStabilizationResult b101 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 101),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.40d),
            BuildDerivedSession(sustainedAttentionSeconds: 1500d),
            BaselineGatingResult.Warm,
            state);
        Assert.False(b101.OffSignal);
        Assert.False(b101.GapExceeded);
        Assert.Equal(5_000, b101.RuntimeState.OffDwellMs);

        DeepWorkStabilizationResult b102 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 102),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.40d),
            BuildDerivedSession(sustainedAttentionSeconds: 1500d),
            BaselineGatingResult.Warm,
            b101.RuntimeState);
        Assert.False(b102.GapExceeded);
        Assert.Equal(5_000, b102.RuntimeState.OffDwellMs);

        DeepWorkStabilizationResult b103 = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 103),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.40d),
            BuildDerivedSession(sustainedAttentionSeconds: 1500d),
            BaselineGatingResult.Warm,
            b102.RuntimeState);
        Assert.False(b103.GapExceeded);
        Assert.Equal(5_000, b103.RuntimeState.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_ThresholdEdgesAreInclusiveForCandidate()
    {
        DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.65d, f: 0.45d, i: 0.40d, v: 0.50d),
            BuildDerivedSession(sustainedAttentionSeconds: 900d),
            BaselineGatingResult.Warm,
            default);

        Assert.True(result.DeepWorkCandidate);
        Assert.False(result.DeepWorkExitCandidate);
        Assert.True(result.OnSignal);
        Assert.False(result.OffSignal);
    }

    [Fact]
    public void ComputeForFinalizedBucket_UsesPolicyOverrideThresholdsAndDwell()
    {
        RuntimeDeepWorkDetectionSettings deepWorkPolicy = new(
            SustainedAttentionSeconds: 300,
            ConfidenceMin: 0.50d,
            FragmentationMax: 0.60d,
            VolatilityMax: 0.60d,
            IntensityMin: 0.30d,
            IntensityMax: 0.80d,
            DwellOnSeconds: 10,
            DwellOffSeconds: 10,
            GapAllowSeconds: 10);
        RuntimeOverloadStabilizationSettings overloadPolicy = new(
            ThresholdOn: PortaExecutionConstants.OverloadStabilization.ThresholdOn,
            ThresholdOff: PortaExecutionConstants.OverloadStabilization.ThresholdOff,
            DwellOnSeconds: PortaExecutionConstants.OverloadStabilization.DwellOnSeconds,
            DwellOffSeconds: PortaExecutionConstants.OverloadStabilization.DwellOffSeconds,
            ConfidenceMin: PortaExecutionConstants.OverloadStabilization.ConfidenceMin,
            GapAllowSeconds: PortaExecutionConstants.OverloadStabilization.GapAllowSeconds,
            LowQualityPauseThreshold: PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold,
            LowQualityDwellDecayMs: PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);

        DeepWorkStabilizationResult withoutOverride = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 300d),
            BaselineGatingResult.Warm,
            default);

        DeepWorkStabilizationRuntimeState state = default;
        DeepWorkStabilizationResult first = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 1),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 300d),
            BaselineGatingResult.Warm,
            state,
            deepWorkPolicy,
            overloadPolicy);
        DeepWorkStabilizationResult second = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
            BuildBucket(bucketSeq: 2),
            BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
            BuildDerivedSession(sustainedAttentionSeconds: 300d),
            BaselineGatingResult.Warm,
            first.RuntimeState,
            deepWorkPolicy,
            overloadPolicy);

        Assert.False(withoutOverride.DeepWorkCandidate);
        Assert.False(withoutOverride.DeepWorkActive);
        Assert.False(first.DeepWorkActive);
        Assert.True(second.DeepWorkActive);
        Assert.True(second.Transitioned);
        Assert.Equal("activated", second.Transition);
    }

    [Fact]
    public void ComputeForFinalizedBucket_LowQualityDecaysDwellsByOneBucketAndPreventsTransitions()
    {
        var state = new DeepWorkStabilizationRuntimeState(
            DeepWorkActive: false,
            OnDwellMs: 20_000,
            OffDwellMs: 10_000,
            LastGoodTsMs: Time.bucketStartEnd(50).endUtcMs,
            LastBucketSeq: 50,
            LastTransitionTsMs: 0,
            DeepWorkStartedAtUtcMs: 0);

        for (int i = 1; i <= 3; i++)
        {
            DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: 50 + i, quality: 0.20d, partial: 1),
                BuildInference(confidence: 0.95d, f: 0.10d, i: 0.55d, v: 0.10d, quality: 0.20d),
                BuildDerivedSession(sustainedAttentionSeconds: 1600d, partial: true),
                BaselineGatingResult.Warm,
                state);
            Assert.True(result.LowQualityPaused);
            Assert.False(result.Transitioned);
            state = result.RuntimeState;
        }

        Assert.Equal(5_000, state.OnDwellMs);
        Assert.Equal(0, state.OffDwellMs);
    }

    [Fact]
    public void ComputeForFinalizedBucket_RestartMidDwell_ContinuesFromPersistedState()
    {
        DeepWorkStabilizationRuntimeState state = default;
        for (int i = 1; i <= 10; i++)
        {
            DeepWorkStabilizationResult result = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: i),
                BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
                BuildDerivedSession(sustainedAttentionSeconds: 1200d),
                BaselineGatingResult.Warm,
                state);
            state = result.RuntimeState;
        }

        // Simulate restart: persisted runtime_state is loaded into a fresh process.
        DeepWorkStabilizationRuntimeState reloadedState = new(
            DeepWorkActive: state.DeepWorkActive,
            OnDwellMs: state.OnDwellMs,
            OffDwellMs: state.OffDwellMs,
            LastGoodTsMs: state.LastGoodTsMs,
            LastBucketSeq: state.LastBucketSeq,
            LastTransitionTsMs: state.LastTransitionTsMs,
            DeepWorkStartedAtUtcMs: state.DeepWorkStartedAtUtcMs);

        int remainingBuckets = (PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds / Time.bucketSeconds) - 10;
        DeepWorkStabilizationResult final = default;
        for (int i = 1; i <= remainingBuckets; i++)
        {
            long seq = 10 + i;
            final = DeepWorkStabilizationComputer.ComputeForFinalizedBucket(
                BuildBucket(bucketSeq: seq),
                BuildInference(confidence: 0.80d, f: 0.40d, i: 0.55d, v: 0.35d),
                BuildDerivedSession(sustainedAttentionSeconds: 1200d),
                BaselineGatingResult.Warm,
                reloadedState);
            reloadedState = final.RuntimeState;
        }

        Assert.True(final.DeepWorkActive);
        Assert.True(final.Transitioned);
        Assert.Equal("activated", final.Transition);
    }

    private static InputBucketRow BuildBucket(long bucketSeq, double quality = 0.9d, int partial = 0)
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
            Partial: partial,
            ContributorsJson: "{\"schema_ver\":1}");
    }

    private static OverloadInferenceResult BuildInference(double confidence, double f, double i, double v, double quality = 0.9d)
    {
        return new OverloadInferenceResult(
            OverloadLikelihood: 0.5d,
            Confidence: confidence,
            FComponent: f,
            IComponent: i,
            VComponent: v,
            FTopPrimitives: [],
            ITopPrimitives: [],
            VTopPrimitives: [],
            Quality: quality,
            Agreement: 0.9d,
            PrimitiveCoverage: 1.0d,
            MissingPrimitiveCount: 0,
            UsedSnapshotFallback: false,
            Source: "bucket_finalizer",
            ComponentsRegistryVersion: 1);
    }

    private static DerivedSessionMetricsResult BuildDerivedSession(double sustainedAttentionSeconds, bool partial = false)
    {
        return new DerivedSessionMetricsResult(
            SustainedAttentionDurationSec: sustainedAttentionSeconds,
            IdleFragmentationIndex: 0.05d,
            Partial: partial,
            DominantAppHash: "app",
            SaIdlePauseSeconds: 0d,
            SaContextSwitchSeconds: 0d,
            SaFreezeReason: string.Empty,
            SaUntrusted: false);
    }
}
