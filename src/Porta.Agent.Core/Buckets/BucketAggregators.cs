namespace Porta.Agent.Core.Buckets;

/// <summary>
/// Pure in-memory deterministic bucket mutations.
/// Same input sequence produces same bucket state.
/// </summary>
internal static class BucketAggregators
{
    internal static void ApplyEventToBucket(BucketBuffer bucket, AgentBrainSnapshot snapshot)
    {
        // Privacy + determinism guard:
        // - sanitize numeric fields to avoid NaN/Infinity leaks
        // - hash sensitive contributor identifiers before staging/persistence
        AgentBrainSnapshot safeSnapshot = BucketPrivacyContract.SanitizeForBucketPersistence(snapshot);

        if (bucket.LatestSnapshot == null || safeSnapshot.TimestampUtc >= bucket.LatestSnapshot.TimestampUtc)
        {
            bucket.LatestSnapshot = safeSnapshot;
        }

        bucket.SampleCount++;
        bucket.TypingBurstIntensitySum += safeSnapshot.TypingBurstIntensity;
        bucket.ScrollVelocityIndexSum += safeSnapshot.ScrollVelocityIndex;
        bucket.FocusSwitchRateSum += safeSnapshot.FocusSwitchRate;

        foreach (string contributor in safeSnapshot.Contributors.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            bucket.ContributorsStage.Add(contributor);
        }

        long tsUtcMs = safeSnapshot.TimestampUtc.ToUnixTimeMilliseconds();
        string? focusAppHash = TryResolveFocusAppHash(safeSnapshot.Contributors);
        if (!string.IsNullOrWhiteSpace(focusAppHash))
        {
            bucket.Focus.RecordFocusEvent(tsUtcMs, focusAppHash);
        }

        double? idleSeconds = TryResolveIdleSeconds(safeSnapshot.Contributors);
        if (idleSeconds.HasValue)
        {
            bucket.Idle.RecordSample(tsUtcMs, idleSeconds.Value);
        }

        // Staged explanation fields; persisted by finalizer in later steps.
        bucket.WhyStage["last_action"] = safeSnapshot.LastAction;
        bucket.WhyStage["policy_id"] = safeSnapshot.PolicyId;
        bucket.WhyStage["domain"] = safeSnapshot.Domain;

        bucket.StateFlagsStage["activation_window_triggered"] = safeSnapshot.ActivationWindowTriggered ? "1" : "0";
        bucket.StateFlagsStage["cooldown_triggered"] = safeSnapshot.CooldownTriggered ? "1" : "0";
        bucket.StateFlagsStage["activation_window_open"] = safeSnapshot.ActivationWindowOpen ? "1" : "0";
    }

    internal static void ApplyKeyboardKeydownToBucket(BucketBuffer bucket, long tsUtcMs, bool isBackspace)
    {
        bucket.Keyboard.RecordKeydown(tsUtcMs, isBackspace);
    }

    internal static void ApplyMouseMoveToBucket(BucketBuffer bucket, long tsUtcMs, int dx, int dy)
    {
        bucket.Mouse.RecordMove(tsUtcMs, dx, dy);
    }

    internal static void ApplyMouseWheelToBucket(BucketBuffer bucket, long tsUtcMs, int delta)
    {
        bucket.Mouse.RecordWheel(tsUtcMs, delta);
    }

    internal static void ApplyFocusEventToBucket(BucketBuffer bucket, long tsUtcMs, string appHash)
    {
        bucket.Focus.RecordFocusEvent(tsUtcMs, appHash);
    }

    internal static void ApplyIdleSampleToBucket(BucketBuffer bucket, long tsUtcMs, double idleSeconds)
    {
        bucket.Idle.RecordSample(tsUtcMs, idleSeconds);
    }

    internal static AgentBrainSnapshot? BuildFinalizedSnapshot(BucketBuffer bucket)
    {
        AgentBrainSnapshot? latest = bucket.LatestSnapshot;
        if (latest == null)
        {
            latest = BuildEmptySnapshot(bucket.StartUtcMs);
        }

        int sampleCount = Math.Max(1, bucket.SampleCount);
        double typingAvg = NormalizeMetric(bucket.TypingBurstIntensitySum / sampleCount);
        double scrollAvg = NormalizeMetric(bucket.ScrollVelocityIndexSum / sampleCount);
        double focusAvg = NormalizeMetric(bucket.FocusSwitchRateSum / sampleCount);
        DateTimeOffset bucketStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(bucket.StartUtcMs);
        bucket.Keyboard.RecomputeDerivedMetrics();
        bucket.Mouse.RecomputeDerivedMetrics();
        bucket.Focus.RecomputeDerivedMetrics(bucket.StartUtcMs, bucket.EndUtcMs);
        bucket.Idle.RecomputeDerivedMetrics(bucket.StartUtcMs, bucket.EndUtcMs);

        // Finalized bucket projection:
        // input_buckets gets deterministic per-bucket aggregated raw values.
        return latest with
        {
            TimestampUtc = bucketStartUtc,
            TypingBurstIntensity = typingAvg,
            ScrollVelocityIndex = scrollAvg,
            FocusSwitchRate = focusAvg
        };
    }

    private static AgentBrainSnapshot BuildEmptySnapshot(long bucketStartUtcMs)
    {
        DateTimeOffset bucketStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(bucketStartUtcMs);
        return new AgentBrainSnapshot(
            TimestampUtc: bucketStartUtc,
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            SmoothedTypingBurstIntensity: 0d,
            SmoothedScrollVelocityIndex: 0d,
            SmoothedFocusSwitchRate: 0d,
            FocusStrength: 0d,
            OverloadLikelihood: 0d,
            FatigueProbability: 0d,
            CognitiveStability: 0d,
            DominantStateValue: 0d,
            DominantStateName: "focus",
            Confidence: 0d,
            LastAction: "none",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: Array.Empty<string>(),
            PolicyId: "n/a",
            Domain: "control",
            EscalationLevel: "Inform",
            PlannedEndTimestampUtc: null,
            ActualEndTimestampUtc: null,
            CooldownEndTimestampUtc: null);
    }

    private static double NormalizeMetric(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static string? TryResolveFocusAppHash(IReadOnlyList<string> contributors)
    {
        foreach (string contributor in contributors)
        {
            if (string.IsNullOrWhiteSpace(contributor))
            {
                continue;
            }

            const string prefix = "foreground_app:";
            if (!contributor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = contributor[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.ToLowerInvariant();
            }
        }

        return null;
    }

    private static double? TryResolveIdleSeconds(IReadOnlyList<string> contributors)
    {
        foreach (string contributor in contributors)
        {
            if (string.IsNullOrWhiteSpace(contributor))
            {
                continue;
            }

            const string prefix = "idle_seconds:";
            if (!contributor.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string raw = contributor[prefix.Length..].Trim();
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
