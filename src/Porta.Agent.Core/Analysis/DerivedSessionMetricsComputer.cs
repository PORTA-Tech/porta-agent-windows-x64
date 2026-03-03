using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Analysis;

internal readonly record struct DerivedSessionMetricsResult(
    double SustainedAttentionDurationSec,
    double IdleFragmentationIndex,
    bool Partial,
    string DominantAppHash,
    string SaFreezeReason,
    bool SaUntrusted);

internal static class DerivedSessionMetricsComputer
{
    internal static DerivedSessionMetricsResult ComputeForFinalizedBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow bucket,
        PrimitiveComputationResult primitive,
        BaselineGatingResult gating)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(tx);

        DerivedSessionRuntimeState previous = ReadPreviousRuntimeState(connection, tx);
        bool missingData = IsMissingSessionData(bucket, primitive);
        bool idleChannelMissing = IsIdleChannelMissing(bucket);
        bool saUntrusted = gating.ColdStart;

        double sustainedAttention;
        string contextAppHash;
        string freezeReason;
        if (missingData)
        {
            // Step 10B contract: partial/missing data decays instead of hard reset.
            sustainedAttention = ClampNonNegativeFinite(
                previous.SustainedAttentionDurationSec * SessionConstants.IdleFragmentation.MissingDecay);
            contextAppHash = previous.SaContextAppHash;
            freezeReason = "partial_or_missing";
        }
        else
        {
            contextAppHash = bucket.DominantAppHash;
            SustainedAttentionDecision decision = EvaluateSustainedAttentionGate(
                bucket,
                gating,
                previous.SaContextAppHash);
            if (decision.Accumulate)
            {
                sustainedAttention = previous.SustainedAttentionDurationSec + Time.bucketSeconds;
                freezeReason = string.Empty;
            }
            else if (decision.Reset)
            {
                sustainedAttention = 0d;
                freezeReason = decision.FreezeReason;
            }
            else
            {
                sustainedAttention = previous.SustainedAttentionDurationSec;
                freezeReason = decision.FreezeReason;
            }
        }

        double idleFragmentation = ComputeIdleFragmentationIndex(bucket, previous.IdleFragmentationIndex, idleChannelMissing);
        return new DerivedSessionMetricsResult(
            SustainedAttentionDurationSec: ClampNonNegativeFinite(sustainedAttention),
            IdleFragmentationIndex: Clamp01(idleFragmentation),
            Partial: missingData || idleChannelMissing,
            DominantAppHash: contextAppHash,
            SaFreezeReason: freezeReason,
            SaUntrusted: saUntrusted);
    }

    private static bool IsMissingSessionData(InputBucketRow bucket, PrimitiveComputationResult primitive)
    {
        if (bucket.Partial != 0 || !primitive.FocusChannelAvailable)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(bucket.DominantAppHash))
        {
            return true;
        }

        return false;
    }

    private static SustainedAttentionDecision EvaluateSustainedAttentionGate(
        InputBucketRow bucket,
        BaselineGatingResult gating,
        string previousContextAppHash)
    {
        if (bucket.TrueBreakFlag == 1 || bucket.TrueBreakSeconds > 0d)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: true, FreezeReason: "true_break");
        }

        if (bucket.FocusSwitchCount >= SessionConstants.SustainedAttention.FocusSwitchesSpikeMin)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: true, FreezeReason: "focus_switch_spike");
        }

        if (bucket.FocusAppEntropy >= SessionConstants.SustainedAttention.EntropySpikeMin)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: true, FreezeReason: "entropy_spike");
        }

        if (!string.IsNullOrWhiteSpace(previousContextAppHash)
            && !string.Equals(previousContextAppHash, bucket.DominantAppHash, StringComparison.Ordinal))
        {
            // Step 10C context-stable contract: strict dominant app hash equality.
            return new SustainedAttentionDecision(Accumulate: false, Reset: true, FreezeReason: "context_changed");
        }

        bool qualityDecent = bucket.QualityScore >= SessionConstants.SustainedAttention.QualityDecentMin;
        if (!gating.PoliciesEnabled && !qualityDecent)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: false, FreezeReason: "quality_low");
        }

        if (bucket.FocusSwitchCount > SessionConstants.SustainedAttention.FocusSwitchesLowMax)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: false, FreezeReason: "focus_switch_not_low");
        }

        if (bucket.FocusAppEntropy > SessionConstants.SustainedAttention.EntropyStableMax)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: false, FreezeReason: "entropy_unstable");
        }

        if (bucket.IdleSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleSecondsStableMax)
        {
            return new SustainedAttentionDecision(Accumulate: false, Reset: false, FreezeReason: "idle_unstable");
        }

        return new SustainedAttentionDecision(Accumulate: true, Reset: false, FreezeReason: string.Empty);
    }

    private static double ComputeIdleFragmentationIndex(
        InputBucketRow bucket,
        double previousIdleFragmentationIndex,
        bool idleChannelMissing)
    {
        if (idleChannelMissing)
        {
            return Clamp01(previousIdleFragmentationIndex * SessionConstants.IdleFragmentation.MissingDecay);
        }

        double frequencyNorm = Clamp01(bucket.IdleMicroIdleCount / SessionConstants.IdleFragmentation.FrequencyMaxPerBucket);
        double microIdleRatio = Clamp01(bucket.IdleMicroIdleSeconds / Time.bucketSeconds);
        double ratioNorm = Clamp01(microIdleRatio / SessionConstants.IdleFragmentation.RatioMaxPerBucket);
        double rawIndex = Clamp01(
            (SessionConstants.IdleFragmentation.FrequencyWeight * frequencyNorm)
            + (SessionConstants.IdleFragmentation.RatioWeight * ratioNorm));
        double smoothed = (SessionConstants.IdleFragmentation.EwmaAlpha * rawIndex)
            + ((1d - SessionConstants.IdleFragmentation.EwmaAlpha) * Clamp01(previousIdleFragmentationIndex));
        return Clamp01(smoothed);
    }

    private static bool IsIdleChannelMissing(InputBucketRow bucket)
    {
        if (!TryReadMissingMajorChannels(bucket.ContributorsJson, out HashSet<string> missingChannels))
        {
            return false;
        }

        return missingChannels.Contains("idle");
    }

    private static bool TryReadMissingMajorChannels(string contributorsJson, out HashSet<string> missingChannels)
    {
        missingChannels = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(contributorsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(contributorsJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("missing_major_channels", out JsonElement missing)
                || missing.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement element in missing.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    missingChannels.Add(value.Trim().ToLowerInvariant());
                }
            }

            return missingChannels.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static DerivedSessionRuntimeState ReadPreviousRuntimeState(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT value_json
            FROM runtime_state
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", PortaExecutionConstants.DerivedSessionMetrics.RuntimeStateKey);

        object? scalar = cmd.ExecuteScalar();
        if (scalar == null || scalar == DBNull.Value)
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty);
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty);
            }

            double sustained = ReadNonNegativeDouble(root, "sa_seconds", "sustained_attention_duration_sec");
            double idleFragment = Clamp01(ReadNonNegativeDouble(root, "idle_fragmentation_index"));
            string contextHash = ReadString(root, "sa_context_app_hash", "dominant_app_hash");
            long lastUpdateBucketSeq = ReadNonNegativeInt64(root, "sa_last_update_bucket_seq", "bucket_seq");
            string freezeReason = ReadString(root, "sa_freeze_reason");
            return new DerivedSessionRuntimeState(
                SustainedAttentionDurationSec: sustained,
                IdleFragmentationIndex: idleFragment,
                SaContextAppHash: contextHash,
                SaLastUpdateBucketSeq: lastUpdateBucketSeq,
                SaFreezeReason: freezeReason);
        }
        catch
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty);
        }
    }

    private static double ReadNonNegativeDouble(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double numeric))
            {
                return ClampNonNegativeFinite(numeric);
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return ClampNonNegativeFinite(parsed);
            }
        }

        return 0d;
    }

    private static long ReadNonNegativeInt64(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long numeric))
            {
                return Math.Max(0, numeric);
            }

            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return Math.Max(0, parsed);
            }
        }

        return 0;
    }

    private static string ReadString(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static double ClampNonNegativeFinite(double value)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            return 0d;
        }

        return value;
    }

    private readonly record struct DerivedSessionRuntimeState(
        double SustainedAttentionDurationSec,
        double IdleFragmentationIndex,
        string SaContextAppHash,
        long SaLastUpdateBucketSeq,
        string SaFreezeReason);

    private readonly record struct SustainedAttentionDecision(
        bool Accumulate,
        bool Reset,
        string FreezeReason);
}
