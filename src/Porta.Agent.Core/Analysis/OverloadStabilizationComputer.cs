using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct OverloadStabilizationRuntimeState(
    bool OverloadActive,
    long OnDwellMs,
    long OffDwellMs,
    long LastGoodTsMs,
    long LastBucketSeq,
    long LastTransitionTsMs);

internal readonly record struct OverloadStabilizationResult(
    bool OverloadActive,
    OverloadStabilizationRuntimeState RuntimeState,
    string DebugMode,
    string Transition,
    bool Transitioned,
    bool PreviousOverloadActive,
    bool GapExceeded,
    bool LowQualityPaused,
    bool Eligible,
    bool OnSignal,
    bool OffSignal,
    bool BetweenThresholds,
    long GapMs,
    double OverloadLikelihood,
    double Confidence,
    double QualityScore);

internal static class OverloadStabilizationComputer
{
    internal static OverloadStabilizationResult ComputeForFinalizedBucket(
        InputBucketRow bucket,
        OverloadInferenceResult overloadInference,
        OverloadStabilizationRuntimeState previous)
    {
        double overload = Clamp01(overloadInference.OverloadLikelihood);
        double confidence = Clamp01(overloadInference.Confidence);
        double quality = Clamp01(overloadInference.Quality);
        bool overloadActive = previous.OverloadActive;
        long currentBucketEndUtcMs = Time.bucketStartEnd(bucket.BucketSeq).endUtcMs;
        long gapMs = ComputeGapMs(previous.LastGoodTsMs, currentBucketEndUtcMs);
        long gapAllowMs = PortaExecutionConstants.OverloadStabilization.GapAllowSeconds * 1000L;
        bool gapExceeded = gapMs > gapAllowMs;
        bool lowQualityPaused = quality < PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold;
        bool eligible = confidence >= PortaExecutionConstants.OverloadStabilization.ConfidenceMin && !lowQualityPaused;
        long onDwellMs = Math.Max(0, previous.OnDwellMs);
        long offDwellMs = Math.Max(0, previous.OffDwellMs);
        string debugMode = previous.LastBucketSeq == 0 ? "bootstrap_hold" : "stable_hold";
        string transition = string.Empty;
        bool transitioned = false;

        bool betweenThresholds = overload > PortaExecutionConstants.OverloadStabilization.ThresholdOff
            && overload < PortaExecutionConstants.OverloadStabilization.ThresholdOn;

        bool onSignal = eligible && overload >= PortaExecutionConstants.OverloadStabilization.ThresholdOn;
        bool offSignal = eligible && overload <= PortaExecutionConstants.OverloadStabilization.ThresholdOff;

        if (lowQualityPaused)
        {
            // Step 12F override: low quality pauses all dwell accumulation,
            // decays both dwells by one bucket, and never transitions state by itself.
            onDwellMs = Math.Max(0, onDwellMs - PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
            offDwellMs = Math.Max(0, offDwellMs - PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);
            debugMode = "quality_pause_decay";
        }
        else if (!overloadActive)
        {
            if (onSignal)
            {
                onDwellMs = checked(onDwellMs + Time.bucket_ms);
                offDwellMs = 0;
                gapMs = 0;
                gapExceeded = false;
                debugMode = "on_dwell_accumulating";
                if (onDwellMs >= (PortaExecutionConstants.OverloadStabilization.DwellOnSeconds * 1000L))
                {
                    overloadActive = true;
                    onDwellMs = 0;
                    offDwellMs = 0;
                    transitioned = true;
                    transition = "activated";
                    debugMode = "activated";
                }
            }
            else
            {
                // E: non-signal buckets are gaps. hold within gap_allow, reset after.
                if (gapExceeded)
                {
                    onDwellMs = 0;
                    offDwellMs = 0;
                    debugMode = "gap_reset";
                }
                else
                {
                    debugMode = !eligible
                        ? "ineligible_hold"
                        : (betweenThresholds ? "between_hold" : "below_threshold_hold");
                }
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
                debugMode = "off_dwell_accumulating";
                if (offDwellMs >= (PortaExecutionConstants.OverloadStabilization.DwellOffSeconds * 1000L))
                {
                    overloadActive = false;
                    onDwellMs = 0;
                    offDwellMs = 0;
                    transitioned = true;
                    transition = "deactivated";
                    debugMode = "deactivated";
                }
            }
            else
            {
                // E: non-signal buckets are gaps. hold within gap_allow, reset after.
                if (gapExceeded)
                {
                    onDwellMs = 0;
                    offDwellMs = 0;
                    debugMode = "gap_reset";
                }
                else
                {
                    debugMode = !eligible
                        ? "ineligible_hold"
                        : (betweenThresholds ? "between_hold" : "above_threshold_hold");
                }
            }
        }

        long lastTransitionTsMs = transitioned
            ? currentBucketEndUtcMs
            : previous.LastTransitionTsMs;
        long lastGoodTsMs = eligible
            ? currentBucketEndUtcMs
            : previous.LastGoodTsMs;
        OverloadStabilizationRuntimeState nextState = new(
            OverloadActive: overloadActive,
            OnDwellMs: Math.Max(0, onDwellMs),
            OffDwellMs: Math.Max(0, offDwellMs),
            LastGoodTsMs: Math.Max(0, lastGoodTsMs),
            LastBucketSeq: Math.Max(previous.LastBucketSeq, bucket.BucketSeq),
            LastTransitionTsMs: Math.Max(0, lastTransitionTsMs));

        return new OverloadStabilizationResult(
            OverloadActive: overloadActive,
            RuntimeState: nextState,
            DebugMode: debugMode,
            Transition: transition,
            Transitioned: transitioned,
            PreviousOverloadActive: previous.OverloadActive,
            GapExceeded: gapExceeded,
            LowQualityPaused: lowQualityPaused,
            Eligible: eligible,
            OnSignal: onSignal,
            OffSignal: offSignal,
            BetweenThresholds: betweenThresholds,
            GapMs: Math.Max(0, gapMs),
            OverloadLikelihood: overload,
            Confidence: confidence,
            QualityScore: quality);
    }

    private static long ComputeGapMs(long previousLastGoodTsMs, long currentBucketEndUtcMs)
    {
        if (previousLastGoodTsMs <= 0 || currentBucketEndUtcMs <= previousLastGoodTsMs)
        {
            return 0;
        }

        // Step 12E pinned rule: gap_ms = now_ms - last_good_ts_ms.
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
}
