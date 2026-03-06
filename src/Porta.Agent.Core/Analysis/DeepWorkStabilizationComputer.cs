using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct DeepWorkStabilizationRuntimeState(
    bool DeepWorkActive,
    long OnDwellMs,
    long OffDwellMs,
    long LastGoodTsMs,
    long LastBucketSeq,
    long LastTransitionTsMs,
    long DeepWorkStartedAtUtcMs);

internal readonly record struct DeepWorkStabilizationResult(
    bool DeepWorkActive,
    DeepWorkStabilizationRuntimeState RuntimeState,
    string DebugMode,
    string Transition,
    bool Transitioned,
    bool PreviousDeepWorkActive,
    bool GapExceeded,
    bool LowQualityPaused,
    bool BaselineReadyGate,
    bool DeepWorkCandidate,
    bool DeepWorkExitCandidate,
    bool OnSignal,
    bool OffSignal,
    bool IntensityInRange,
    long GapMs,
    double Confidence,
    double FComponent,
    double IComponent,
    double VComponent,
    double SustainedAttentionDurationSec);

internal static class DeepWorkStabilizationComputer
{
    internal static DeepWorkStabilizationResult ComputeForFinalizedBucket(
        InputBucketRow bucket,
        OverloadInferenceResult overloadInference,
        DerivedSessionMetricsResult derivedSession,
        BaselineGatingResult gating,
        DeepWorkStabilizationRuntimeState previous)
    {
        double confidence = Clamp01(overloadInference.Confidence);
        double quality = Clamp01(overloadInference.Quality);
        double fComponent = Clamp01(overloadInference.FComponent);
        double iComponent = Clamp01(overloadInference.IComponent);
        double vComponent = Clamp01(overloadInference.VComponent);
        double sustainedAttentionSec = ClampNonNegative(derivedSession.SustainedAttentionDurationSec);

        bool deepWorkActive = previous.DeepWorkActive;
        long currentBucketEndUtcMs = Time.bucketStartEnd(bucket.BucketSeq).endUtcMs;
        long gapMs = ComputeGapMs(previous.LastGoodTsMs, currentBucketEndUtcMs);
        long gapAllowMs = PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds * 1000L;
        bool gapExceeded = gapMs > gapAllowMs;

        bool baselineReadyGate = gating.BaselineReady;
        bool lowQualityPaused = quality < PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold
            || bucket.Partial == 1
            || derivedSession.Partial;
        bool intensityInRange = iComponent >= PortaExecutionConstants.DeepWorkDetection.IntensityMin
            && iComponent <= PortaExecutionConstants.DeepWorkDetection.IntensityMax;
        bool deepWorkCandidate = baselineReadyGate
            && confidence >= PortaExecutionConstants.DeepWorkDetection.ConfidenceMin
            && fComponent <= PortaExecutionConstants.DeepWorkDetection.FragmentationMax
            && vComponent <= PortaExecutionConstants.DeepWorkDetection.VolatilityMax
            && intensityInRange
            && sustainedAttentionSec >= PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds;
        bool deepWorkExitCandidate = confidence < PortaExecutionConstants.DeepWorkDetection.ConfidenceMin
            || fComponent > PortaExecutionConstants.DeepWorkDetection.FragmentationMax
            || vComponent > PortaExecutionConstants.DeepWorkDetection.VolatilityMax
            || iComponent < PortaExecutionConstants.DeepWorkDetection.IntensityMin
            || iComponent > PortaExecutionConstants.DeepWorkDetection.IntensityMax
            || sustainedAttentionSec < PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds;
        bool onSignal = !lowQualityPaused && deepWorkCandidate;
        bool offSignal = !lowQualityPaused && deepWorkExitCandidate;

        long onDwellMs = Math.Max(0, previous.OnDwellMs);
        long offDwellMs = Math.Max(0, previous.OffDwellMs);
        string debugMode = previous.LastBucketSeq == 0 ? "bootstrap_hold" : "stable_hold";
        string transition = string.Empty;
        bool transitioned = false;
        bool dwellAccumulated = false;

        if (lowQualityPaused)
        {
            // Mirror overload Step-12 behavior for action-driving state:
            // low quality pauses dwell accumulation and decays both dwells by one bucket.
            onDwellMs = Math.Max(0, onDwellMs - PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
            offDwellMs = Math.Max(0, offDwellMs - PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
            debugMode = "quality_pause_decay";
        }
        else if (!deepWorkActive)
        {
            if (onSignal)
            {
                onDwellMs = checked(onDwellMs + Time.bucket_ms);
                offDwellMs = 0;
                gapMs = 0;
                gapExceeded = false;
                dwellAccumulated = true;
                debugMode = "on_dwell_accumulating";
                if (onDwellMs >= (PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds * 1000L))
                {
                    deepWorkActive = true;
                    onDwellMs = 0;
                    offDwellMs = 0;
                    transitioned = true;
                    transition = "activated";
                    debugMode = "activated";
                }
            }
            else if (gapExceeded)
            {
                onDwellMs = 0;
                offDwellMs = 0;
                debugMode = "gap_reset";
            }
            else
            {
                debugMode = "inactive_hold";
            }
        }
        else
        {
            if (offSignal)
            {
                offDwellMs = checked(offDwellMs + Time.bucket_ms);
                onDwellMs = 0;
                gapMs = 0;
                gapExceeded = false;
                dwellAccumulated = true;
                debugMode = "off_dwell_accumulating";
                if (offDwellMs >= (PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds * 1000L))
                {
                    deepWorkActive = false;
                    onDwellMs = 0;
                    offDwellMs = 0;
                    transitioned = true;
                    transition = "deactivated";
                    debugMode = "deactivated";
                }
            }
            else if (gapExceeded)
            {
                onDwellMs = 0;
                offDwellMs = 0;
                debugMode = "gap_reset";
            }
            else
            {
                debugMode = "active_hold";
            }
        }

        long lastTransitionTsMs = transitioned
            ? currentBucketEndUtcMs
            : previous.LastTransitionTsMs;
        long deepWorkStartedAtUtcMs = previous.DeepWorkStartedAtUtcMs;
        if (transitioned && deepWorkActive)
        {
            deepWorkStartedAtUtcMs = currentBucketEndUtcMs;
        }
        else if (transitioned && !deepWorkActive)
        {
            deepWorkStartedAtUtcMs = 0;
        }
        else if (deepWorkActive && deepWorkStartedAtUtcMs <= 0)
        {
            // Backfill if older runtime state did not carry a start timestamp.
            deepWorkStartedAtUtcMs = currentBucketEndUtcMs;
        }
        long lastGoodTsMs = dwellAccumulated
            ? currentBucketEndUtcMs
            : previous.LastGoodTsMs;
        DeepWorkStabilizationRuntimeState nextState = new(
            DeepWorkActive: deepWorkActive,
            OnDwellMs: Math.Max(0, onDwellMs),
            OffDwellMs: Math.Max(0, offDwellMs),
            LastGoodTsMs: Math.Max(0, lastGoodTsMs),
            LastBucketSeq: Math.Max(previous.LastBucketSeq, bucket.BucketSeq),
            LastTransitionTsMs: Math.Max(0, lastTransitionTsMs),
            DeepWorkStartedAtUtcMs: Math.Max(0, deepWorkStartedAtUtcMs));

        return new DeepWorkStabilizationResult(
            DeepWorkActive: deepWorkActive,
            RuntimeState: nextState,
            DebugMode: debugMode,
            Transition: transition,
            Transitioned: transitioned,
            PreviousDeepWorkActive: previous.DeepWorkActive,
            GapExceeded: gapExceeded,
            LowQualityPaused: lowQualityPaused,
            BaselineReadyGate: baselineReadyGate,
            DeepWorkCandidate: deepWorkCandidate,
            DeepWorkExitCandidate: deepWorkExitCandidate,
            OnSignal: onSignal,
            OffSignal: offSignal,
            IntensityInRange: intensityInRange,
            GapMs: Math.Max(0, gapMs),
            Confidence: confidence,
            FComponent: fComponent,
            IComponent: iComponent,
            VComponent: vComponent,
            SustainedAttentionDurationSec: sustainedAttentionSec);
    }

    private static long ComputeGapMs(long previousLastGoodTsMs, long currentBucketEndUtcMs)
    {
        if (previousLastGoodTsMs <= 0 || currentBucketEndUtcMs <= previousLastGoodTsMs)
        {
            return 0;
        }

        return Math.Max(0, currentBucketEndUtcMs - previousLastGoodTsMs);
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double ClampNonNegative(double value)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }
}
