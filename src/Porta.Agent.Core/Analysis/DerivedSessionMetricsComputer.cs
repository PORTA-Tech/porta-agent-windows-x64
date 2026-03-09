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
    double SaIdlePauseSeconds,
    double SaContextSwitchSeconds,
    string SaFreezeReason,
    bool SaUntrusted,
    double TaskContinuityScore = 0d,
    double TaskContinuitySeconds = 0d,
    string ContinuityState = "reset",
    double ContinuityConfidence = 0d);

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
        bool idleChannelMissing = IsIdleChannelMissing(bucket);
        ChannelEvidence evidence = ResolveChannelEvidence(bucket, primitive);
        bool saUntrusted = gating.ColdStart;

        TaskContinuityDecision continuity = EvaluateTaskContinuity(
            bucket,
            evidence,
            previous);

        double idleFragmentation = ComputeIdleFragmentationIndex(bucket, previous.IdleFragmentationIndex, idleChannelMissing);
        bool derivedPartial = !evidence.FocusAvailable
            || idleChannelMissing
            || (!evidence.AnyInteractionAvailable && continuity.IdlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds);
        double continuityConfidence = ComputeContinuityConfidence(
            bucket,
            evidence,
            idleChannelMissing,
            continuity.State);
        return new DerivedSessionMetricsResult(
            SustainedAttentionDurationSec: ClampNonNegativeFinite(continuity.TaskContinuitySeconds),
            IdleFragmentationIndex: Clamp01(idleFragmentation),
            Partial: derivedPartial,
            DominantAppHash: continuity.AnchorAppHash,
            SaIdlePauseSeconds: ClampNonNegativeFinite(continuity.IdlePauseSeconds),
            SaContextSwitchSeconds: ClampNonNegativeFinite(continuity.ContextSwitchSeconds),
            SaFreezeReason: continuity.ReasonCode,
            SaUntrusted: saUntrusted,
            TaskContinuityScore: continuity.Score,
            TaskContinuitySeconds: ClampNonNegativeFinite(continuity.TaskContinuitySeconds),
            ContinuityState: continuity.State,
            ContinuityConfidence: continuityConfidence);
    }

    private static ChannelEvidence ResolveChannelEvidence(InputBucketRow bucket, PrimitiveComputationResult primitive)
    {
        TryReadMissingMajorChannels(bucket.ContributorsJson, out HashSet<string> missingChannels);
        bool keyboardAvailable = !missingChannels.Contains("keyboard");
        bool mouseAvailable = !missingChannels.Contains("mouse");
        bool focusAvailable = primitive.FocusChannelAvailable && !string.IsNullOrWhiteSpace(bucket.DominantAppHash);
        bool keyboardActive = keyboardAvailable
            && (bucket.KeyboardBurstRate > 0d || bucket.KeyboardBackspaceRate > 0d || bucket.KeyboardIkiMean > 0d);
        bool mouseActive = mouseAvailable
            && (bucket.ScrollVelocityIndex > 0d
                || bucket.MouseSpeedMean > 0d
                || bucket.MouseScrollSpeed > 0d
                || bucket.MouseMicroCorrectionRate > 0d
                || bucket.MouseJerkP95 > 0d);

        return new ChannelEvidence(
            KeyboardAvailable: keyboardAvailable,
            MouseAvailable: mouseAvailable,
            FocusAvailable: focusAvailable,
            KeyboardActive: keyboardActive,
            MouseActive: mouseActive);
    }

    private static TaskContinuityDecision EvaluateTaskContinuity(
        InputBucketRow bucket,
        ChannelEvidence evidence,
        DerivedSessionRuntimeState previous)
    {
        double previousContinuitySeconds = ClampNonNegativeFinite(previous.TaskContinuitySeconds);
        string anchorAppHash = string.IsNullOrWhiteSpace(previous.AnchorAppHash)
            ? bucket.DominantAppHash
            : previous.AnchorAppHash;
        string currentContextAppHash = evidence.FocusAvailable && !string.IsNullOrWhiteSpace(bucket.DominantAppHash)
            ? bucket.DominantAppHash
            : anchorAppHash;
        double idlePauseSeconds = ResolveIdlePauseSeconds(previous, bucket);
        bool contextChanged = evidence.FocusAvailable
            && !string.IsNullOrWhiteSpace(anchorAppHash)
            && !string.IsNullOrWhiteSpace(currentContextAppHash)
            && !string.Equals(anchorAppHash, currentContextAppHash, StringComparison.Ordinal);
        double contextSwitchSeconds = contextChanged
            ? ClampNonNegativeFinite(previous.ContextSwitchSeconds + Time.bucketSeconds)
            : 0d;
        double instabilitySeconds = ClampNonNegativeFinite(previous.InstabilitySeconds);

        if (bucket.TrueBreakSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
            || idlePauseSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds)
        {
            return BuildDecision(
                continuitySeconds: 0d,
                anchorAppHash: string.IsNullOrWhiteSpace(currentContextAppHash) ? anchorAppHash : currentContextAppHash,
                idlePauseSeconds: idlePauseSeconds,
                contextSwitchSeconds: 0d,
                instabilitySeconds: 0d,
                state: "reset",
                reasonCode: "long_break");
        }

        if (contextChanged)
        {
            if (contextSwitchSeconds <= PortaExecutionConstants.DerivedSessionMetrics.ContextShiftGraceSeconds)
            {
                return BuildDecision(
                    previousContinuitySeconds,
                    anchorAppHash,
                    idlePauseSeconds,
                    contextSwitchSeconds,
                    0d,
                    "pause",
                    "context_excursion");
            }

            if (contextSwitchSeconds >= PortaExecutionConstants.DerivedSessionMetrics.ContextShiftResetSeconds)
            {
                return BuildDecision(
                    0d,
                    currentContextAppHash,
                    idlePauseSeconds,
                    0d,
                    0d,
                    "reset",
                    "context_shift_reset");
            }

            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.ContextShiftDecayPerBucketSeconds),
                anchorAppHash,
                idlePauseSeconds,
                contextSwitchSeconds,
                0d,
                "decay",
                "context_excursion_decay");
        }

        if (!evidence.FocusAvailable)
        {
            return EvaluateNoFocusContinuity(previousContinuitySeconds, anchorAppHash, idlePauseSeconds, evidence);
        }

        bool spikeInstability = bucket.FocusSwitchCount >= SessionConstants.SustainedAttention.FocusSwitchesSpikeMin
            || bucket.FocusAppEntropy >= SessionConstants.SustainedAttention.EntropySpikeMin;
        bool moderateInstability = bucket.FocusSwitchCount > SessionConstants.SustainedAttention.FocusSwitchesLowMax
            || bucket.FocusAppEntropy > SessionConstants.SustainedAttention.EntropyStableMax;

        if (spikeInstability || moderateInstability)
        {
            instabilitySeconds += Time.bucketSeconds;
            if (instabilitySeconds >= PortaExecutionConstants.DerivedSessionMetrics.InstabilityResetSeconds)
            {
                return BuildDecision(
                    0d,
                    currentContextAppHash,
                    idlePauseSeconds,
                    0d,
                    0d,
                    "reset",
                    "instability_reset");
            }

            if (spikeInstability)
            {
                return BuildDecision(
                    ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.InstabilityDecayPerBucketSeconds),
                    currentContextAppHash,
                    idlePauseSeconds,
                    0d,
                    instabilitySeconds,
                    "decay",
                    "focus_instability");
            }

            return BuildDecision(
                previousContinuitySeconds,
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                instabilitySeconds,
                "pause",
                "focus_unstable");
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleDecayPerBucketSeconds),
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "decay",
                "pause_decay");
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds)
        {
            return BuildDecision(
                previousContinuitySeconds,
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "pause",
                "short_pause");
        }

        if (bucket.IdleSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleSecondsStableMax)
        {
            return BuildDecision(
                previousContinuitySeconds,
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "pause",
                "idle_unstable");
        }

        return BuildDecision(
            previousContinuitySeconds + Time.bucketSeconds,
            currentContextAppHash,
            idlePauseSeconds: 0d,
            contextSwitchSeconds: 0d,
            instabilitySeconds: 0d,
            state: "keep",
            reasonCode: string.Empty);
    }

    private static TaskContinuityDecision EvaluateNoFocusContinuity(
        double previousContinuitySeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        ChannelEvidence evidence)
    {
        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleDecayPerBucketSeconds),
                anchorAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "decay",
                "focus_unavailable_decay");
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds || !evidence.AnyInteractionActive)
        {
            return BuildDecision(
                previousContinuitySeconds,
                anchorAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "pause",
                "focus_unavailable");
        }

        return BuildDecision(
            previousContinuitySeconds,
            anchorAppHash,
            idlePauseSeconds,
            0d,
            0d,
            "pause",
            "focus_unavailable");
    }

    private static double ResolveIdlePauseSeconds(DerivedSessionRuntimeState previous, InputBucketRow bucket)
    {
        if (bucket.IdleSeconds <= 0.25d)
        {
            return 0d;
        }

        return ClampNonNegativeFinite(previous.IdlePauseSeconds + bucket.IdleSeconds);
    }

    private static double ComputeContinuityConfidence(
        InputBucketRow bucket,
        ChannelEvidence evidence,
        bool idleChannelMissing,
        string continuityState)
    {
        double confidence = Clamp01(bucket.QualityScore);
        if (!evidence.FocusAvailable)
        {
            confidence -= PortaExecutionConstants.DerivedSessionMetrics.FocusMissingConfidencePenalty;
        }

        if (!evidence.KeyboardAvailable && !evidence.MouseAvailable)
        {
            confidence -= PortaExecutionConstants.DerivedSessionMetrics.BothInteractionsMissingConfidencePenalty;
        }
        else
        {
            if (!evidence.KeyboardAvailable)
            {
                confidence -= PortaExecutionConstants.DerivedSessionMetrics.KeyboardMissingConfidencePenalty;
            }

            if (!evidence.MouseAvailable)
            {
                confidence -= PortaExecutionConstants.DerivedSessionMetrics.MouseMissingConfidencePenalty;
            }
        }

        if (idleChannelMissing)
        {
            confidence -= PortaExecutionConstants.DerivedSessionMetrics.IdleMissingConfidencePenalty;
        }

        if (bucket.Partial != 0)
        {
            confidence -= PortaExecutionConstants.DerivedSessionMetrics.PartialConfidencePenalty;
        }

        if (!evidence.AnyInteractionActive)
        {
            confidence -= PortaExecutionConstants.DerivedSessionMetrics.NoInteractionConfidencePenalty;
        }

        if (string.Equals(continuityState, "reset", StringComparison.Ordinal))
        {
            confidence = Math.Min(confidence, 0.50d);
        }

        return Clamp01(confidence);
    }

    private static TaskContinuityDecision BuildDecision(
        double continuitySeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        double contextSwitchSeconds,
        double instabilitySeconds,
        string state,
        string reasonCode)
    {
        return new TaskContinuityDecision(
            TaskContinuitySeconds: ClampNonNegativeFinite(continuitySeconds),
            AnchorAppHash: anchorAppHash,
            IdlePauseSeconds: ClampNonNegativeFinite(idlePauseSeconds),
            ContextSwitchSeconds: ClampNonNegativeFinite(contextSwitchSeconds),
            InstabilitySeconds: ClampNonNegativeFinite(instabilitySeconds),
            State: state,
            Score: ResolveContinuityScore(state),
            ReasonCode: reasonCode);
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
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d);
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d);
            }

            double sustained = ReadNonNegativeDouble(root, "task_continuity_seconds", "sa_seconds", "sustained_attention_duration_sec");
            double idleFragment = Clamp01(ReadNonNegativeDouble(root, "idle_fragmentation_index"));
            string contextHash = ReadString(root, "continuity_anchor_app_hash", "sa_context_app_hash", "dominant_app_hash");
            long lastUpdateBucketSeq = ReadNonNegativeInt64(root, "continuity_last_update_bucket_seq", "sa_last_update_bucket_seq", "bucket_seq");
            double idlePauseSeconds = ReadNonNegativeDouble(root, "continuity_idle_pause_seconds", "sa_idle_pause_seconds");
            double contextSwitchSeconds = ReadNonNegativeDouble(root, "continuity_context_switch_seconds", "sa_context_switch_seconds");
            string freezeReason = ReadString(root, "continuity_reason_code", "sa_freeze_reason");
            string continuityState = ReadString(root, "continuity_state");
            double continuityScore = Clamp01(ReadNonNegativeDouble(root, "task_continuity_score"));
            double instabilitySeconds = ReadNonNegativeDouble(root, "continuity_instability_seconds");
            return new DerivedSessionRuntimeState(
                TaskContinuitySeconds: sustained,
                IdleFragmentationIndex: idleFragment,
                AnchorAppHash: contextHash,
                LastUpdateBucketSeq: lastUpdateBucketSeq,
                ReasonCode: freezeReason,
                IdlePauseSeconds: idlePauseSeconds,
                ContextSwitchSeconds: contextSwitchSeconds,
                ContinuityState: string.IsNullOrWhiteSpace(continuityState) ? "reset" : continuityState,
                ContinuityScore: continuityScore,
                InstabilitySeconds: instabilitySeconds);
        }
        catch
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d);
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
        double TaskContinuitySeconds,
        double IdleFragmentationIndex,
        string AnchorAppHash,
        long LastUpdateBucketSeq,
        string ReasonCode,
        double IdlePauseSeconds,
        double ContextSwitchSeconds,
        string ContinuityState,
        double ContinuityScore,
        double InstabilitySeconds);

    private readonly record struct ChannelEvidence(
        bool KeyboardAvailable,
        bool MouseAvailable,
        bool FocusAvailable,
        bool KeyboardActive,
        bool MouseActive)
    {
        public bool AnyInteractionAvailable => KeyboardAvailable || MouseAvailable;
        public bool AnyInteractionActive => KeyboardActive || MouseActive;
    }

    private readonly record struct TaskContinuityDecision(
        double TaskContinuitySeconds,
        string AnchorAppHash,
        double IdlePauseSeconds,
        double ContextSwitchSeconds,
        double InstabilitySeconds,
        string State,
        double Score,
        string ReasonCode);

    private static double ResolveContinuityScore(string state)
    {
        return state switch
        {
            "keep" => PortaExecutionConstants.DerivedSessionMetrics.KeepScore,
            "pause" => PortaExecutionConstants.DerivedSessionMetrics.PauseScore,
            "decay" => PortaExecutionConstants.DerivedSessionMetrics.DecayScore,
            _ => PortaExecutionConstants.DerivedSessionMetrics.ResetScore
        };
    }
}
