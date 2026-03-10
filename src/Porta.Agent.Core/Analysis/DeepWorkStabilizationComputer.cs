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
        DeepWorkStabilizationRuntimeState previous,
        RuntimeDeepWorkDetectionSettings? policy = null,
        RuntimeOverloadStabilizationSettings? overloadPolicy = null)
    {
        RuntimeDeepWorkDetectionSettings settings = policy ?? new RuntimeDeepWorkDetectionSettings(
            PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds,
            PortaExecutionConstants.DeepWorkDetection.ConfidenceMin,
            PortaExecutionConstants.DeepWorkDetection.FragmentationMax,
            PortaExecutionConstants.DeepWorkDetection.VolatilityMax,
            PortaExecutionConstants.DeepWorkDetection.IntensityMin,
            PortaExecutionConstants.DeepWorkDetection.IntensityMax,
            PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds,
            PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds,
            PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds);
        RuntimeOverloadStabilizationSettings overloadSettings = overloadPolicy ?? new RuntimeOverloadStabilizationSettings(
            PortaExecutionConstants.OverloadStabilization.ThresholdOn,
            PortaExecutionConstants.OverloadStabilization.ThresholdOff,
            PortaExecutionConstants.OverloadStabilization.DwellOnSeconds,
            PortaExecutionConstants.OverloadStabilization.DwellOffSeconds,
            PortaExecutionConstants.OverloadStabilization.ConfidenceMin,
            PortaExecutionConstants.OverloadStabilization.GapAllowSeconds,
            PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold,
            PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
        double confidence = Clamp01(overloadInference.Confidence);
        double quality = Clamp01(overloadInference.Quality);
        double fComponent = Clamp01(overloadInference.FComponent);
        double iComponent = Clamp01(overloadInference.IComponent);
        double vComponent = Clamp01(overloadInference.VComponent);
        double sustainedAttentionSec = ClampNonNegative(derivedSession.SustainedAttentionDurationSec);

        bool deepWorkActive = previous.DeepWorkActive;
        long currentBucketEndUtcMs = Time.bucketStartEnd(bucket.BucketSeq).endUtcMs;
        long gapMs = ComputeGapMs(previous.LastGoodTsMs, currentBucketEndUtcMs);
        long gapAllowMs = settings.GapAllowSeconds * 1000L;
        bool gapExceeded = gapMs > gapAllowMs;

        bool baselineReadyGate = gating.SystemActionsEnabled;
        bool lowQualityPaused = quality < overloadSettings.LowQualityPauseThreshold
            || bucket.Partial == 1
            || derivedSession.Partial;
        bool intensityInRange = iComponent >= settings.IntensityMin
            && iComponent <= settings.IntensityMax;
        bool deepWorkCandidate = baselineReadyGate
            && confidence >= settings.ConfidenceMin
            && fComponent <= settings.FragmentationMax
            && vComponent <= settings.VolatilityMax
            && intensityInRange
            && sustainedAttentionSec >= settings.SustainedAttentionSeconds;
        bool deepWorkExitCandidate = confidence < settings.ConfidenceMin
            || fComponent > settings.FragmentationMax
            || vComponent > settings.VolatilityMax
            || iComponent < settings.IntensityMin
            || iComponent > settings.IntensityMax
            || sustainedAttentionSec < settings.SustainedAttentionSeconds;
        bool onSignal = !lowQualityPaused && deepWorkCandidate;
        bool offSignal = !lowQualityPaused && deepWorkExitCandidate;

        long onDwellMs = Math.Max(0, previous.OnDwellMs);
        long offDwellMs = Math.Max(0, previous.OffDwellMs);
        string debugMode = previous.LastBucketSeq == 0 ? "bootstrap_hold" : "stable_hold";
        string transition = string.Empty;
        bool transitioned = false;
        bool dwellAccumulated = false;
        bool keepAlive = false;

        if (lowQualityPaused)
        {
            // Mirror overload Step-12 behavior for action-driving state:
            // low quality pauses dwell accumulation and decays both dwells by one bucket.
            onDwellMs = Math.Max(0, onDwellMs - overloadSettings.LowQualityDwellDecayMs);
            offDwellMs = Math.Max(0, offDwellMs - overloadSettings.LowQualityDwellDecayMs);
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
                if (onDwellMs >= (settings.DwellOnSeconds * 1000L))
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
                if (offDwellMs >= (settings.DwellOffSeconds * 1000L))
                {
                    deepWorkActive = false;
                    onDwellMs = 0;
                    offDwellMs = 0;
                    transitioned = true;
                    transition = "deactivated";
                    debugMode = "deactivated";
                }
            }
            else if (onSignal)
            {
                gapMs = 0;
                gapExceeded = false;
                keepAlive = true;
                debugMode = "active_hold";
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
        long lastGoodTsMs = (dwellAccumulated || keepAlive)
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
