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
    double ContinuityConfidence = 0d,
    double FocusEpisodeCandidateSeconds = 0d,
    double ContinuityInstabilitySeconds = 0d,
    string AttentionSegmentKind = "focus",
    double AttentionSegmentSeconds = 0d,
    bool DistractionEpisodeActive = false,
    double DistractionEpisodeSeconds = 0d);

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
            continuity.State,
            continuity.IdlePauseSeconds);
        AttentionSegmentDecision attentionSegment = EvaluateAttentionSegment(bucket, evidence, continuity);
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
            ContinuityConfidence: continuityConfidence,
            FocusEpisodeCandidateSeconds: continuity.FocusEpisodeCandidateSeconds,
            ContinuityInstabilitySeconds: continuity.InstabilitySeconds,
            AttentionSegmentKind: attentionSegment.Kind,
            AttentionSegmentSeconds: attentionSegment.SegmentSeconds,
            DistractionEpisodeActive: attentionSegment.DistractionEpisodeActive,
            DistractionEpisodeSeconds: attentionSegment.DistractionEpisodeSeconds);
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
        double previousCandidateSeconds = ClampNonNegativeFinite(previous.FocusEpisodeCandidateSeconds);
        double continuityGapSeconds = ResolveContinuityGapSeconds(previous, bucket);
        bool resetForGap = continuityGapSeconds >= PortaExecutionConstants.DerivedSessionMetrics.GapResetSeconds;
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

        if (resetForGap)
        {
            previousContinuitySeconds = 0d;
            previousCandidateSeconds = 0d;
            anchorAppHash = evidence.FocusAvailable && !string.IsNullOrWhiteSpace(bucket.DominantAppHash)
                ? bucket.DominantAppHash
                : string.Empty;
            currentContextAppHash = anchorAppHash;
            idlePauseSeconds = bucket.IdleSeconds <= 0.25d
                ? 0d
                : ClampNonNegativeFinite(bucket.IdleSeconds);
            contextChanged = false;
            contextSwitchSeconds = 0d;
            instabilitySeconds = 0d;
        }

        if (bucket.TrueBreakSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
            || idlePauseSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds)
        {
            return BuildDecision(
                continuitySeconds: 0d,
                focusEpisodeCandidateSeconds: 0d,
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
                    previousCandidateSeconds,
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
                    0d,
                    currentContextAppHash,
                    idlePauseSeconds,
                    contextSwitchSeconds,
                    0d,
                    "reset",
                    "context_shift_reset");
            }

            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.ContextShiftDecayPerBucketSeconds),
                ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
                anchorAppHash,
                idlePauseSeconds,
                contextSwitchSeconds,
                0d,
                "decay",
                "context_excursion_decay");
        }

        if (!evidence.FocusAvailable)
        {
            return EvaluateNoFocusContinuity(previousContinuitySeconds, previousCandidateSeconds, anchorAppHash, idlePauseSeconds, evidence);
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
                    0d,
                    currentContextAppHash,
                    idlePauseSeconds,
                    0d,
                    instabilitySeconds,
                    "reset",
                    "instability_reset");
            }

            if (spikeInstability)
            {
                return BuildDecision(
                    ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.InstabilityDecayPerBucketSeconds),
                    ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
                    currentContextAppHash,
                    idlePauseSeconds,
                    0d,
                    instabilitySeconds,
                    "decay",
                    "focus_instability");
            }

            return BuildDecision(
                previousContinuitySeconds,
                previousCandidateSeconds,
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                instabilitySeconds,
                "pause",
                "focus_unstable");
        }

        if (evidence.AnyInteractionActive)
        {
            return AdvanceFocusEpisode(
                previousContinuitySeconds,
                previousCandidateSeconds,
                currentContextAppHash,
                idlePauseSeconds: 0d,
                contextSwitchSeconds: 0d,
                instabilitySeconds: 0d,
                reasonCode: string.Empty);
        }

        if (CanAccumulatePassiveFocus(previousContinuitySeconds, previousCandidateSeconds, idlePauseSeconds))
        {
            return AdvanceFocusEpisode(
                previousContinuitySeconds,
                previousCandidateSeconds,
                currentContextAppHash,
                idlePauseSeconds,
                contextSwitchSeconds: 0d,
                instabilitySeconds: 0d,
                reasonCode: "passive_focus");
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleDecayPerBucketSeconds),
                ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
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
                previousCandidateSeconds,
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
                previousCandidateSeconds,
                currentContextAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "pause",
                "idle_unstable");
        }

        return BuildDecision(
            previousContinuitySeconds,
            previousCandidateSeconds,
            currentContextAppHash,
            idlePauseSeconds,
            0d,
            0d,
            "pause",
            "passive_focus");
    }

    private static TaskContinuityDecision EvaluateNoFocusContinuity(
        double previousContinuitySeconds,
        double previousCandidateSeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        ChannelEvidence evidence)
    {
        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleDecayPerBucketSeconds),
                ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
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
                previousCandidateSeconds,
                anchorAppHash,
                idlePauseSeconds,
                0d,
                0d,
                "pause",
                "focus_unavailable");
        }

        return BuildDecision(
            previousContinuitySeconds,
            previousCandidateSeconds,
            anchorAppHash,
            idlePauseSeconds,
            0d,
            0d,
            "pause",
            "focus_unavailable");
    }

    private static TaskContinuityDecision AdvanceFocusEpisode(
        double previousContinuitySeconds,
        double previousCandidateSeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        double contextSwitchSeconds,
        double instabilitySeconds,
        string reasonCode)
    {
        if (previousContinuitySeconds > 0d)
        {
            return BuildDecision(
                previousContinuitySeconds + Time.bucketSeconds,
                0d,
                anchorAppHash,
                idlePauseSeconds,
                contextSwitchSeconds,
                instabilitySeconds,
                "keep",
                reasonCode);
        }

        double candidateSeconds = ClampNonNegativeFinite(previousCandidateSeconds + Time.bucketSeconds);
        if (candidateSeconds >= PortaExecutionConstants.DerivedSessionMetrics.FocusEpisodeSettleSeconds)
        {
            return BuildDecision(
                candidateSeconds,
                0d,
                anchorAppHash,
                idlePauseSeconds,
                contextSwitchSeconds,
                instabilitySeconds,
                "keep",
                reasonCode);
        }

        return BuildDecision(
            0d,
            candidateSeconds,
            anchorAppHash,
            idlePauseSeconds,
            contextSwitchSeconds,
            instabilitySeconds,
            "pause",
            "focus_settling");
    }

    private static bool CanAccumulatePassiveFocus(
        double previousContinuitySeconds,
        double previousCandidateSeconds,
        double idlePauseSeconds)
    {
        bool episodeHasMomentum = previousContinuitySeconds > 0d || previousCandidateSeconds > 0d;
        return episodeHasMomentum
            && idlePauseSeconds <= PortaExecutionConstants.DerivedSessionMetrics.PassiveFocusAccumulateMaxSeconds;
    }

    private static double ResolveIdlePauseSeconds(DerivedSessionRuntimeState previous, InputBucketRow bucket)
    {
        if (bucket.IdleSeconds <= 0.25d)
        {
            return 0d;
        }

        return ClampNonNegativeFinite(previous.IdlePauseSeconds + bucket.IdleSeconds);
    }

    private static double ResolveContinuityGapSeconds(DerivedSessionRuntimeState previous, InputBucketRow bucket)
    {
        if (previous.LastUpdateBucketSeq <= 0 || bucket.BucketSeq <= previous.LastUpdateBucketSeq)
        {
            return 0d;
        }

        long skippedBuckets = bucket.BucketSeq - previous.LastUpdateBucketSeq - 1;
        if (skippedBuckets <= 0)
        {
            return 0d;
        }

        return ClampNonNegativeFinite(skippedBuckets * Time.bucketSeconds);
    }

    private static double ComputeContinuityConfidence(
        InputBucketRow bucket,
        ChannelEvidence evidence,
        bool idleChannelMissing,
        string continuityState,
        double idlePauseSeconds)
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
                confidence -= 0.05d;
            }

            if (!evidence.MouseAvailable)
            {
                confidence -= 0.06d;
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
            confidence -= ResolveNoInteractionConfidencePenalty(idlePauseSeconds);
        }

        if (string.Equals(continuityState, "reset", StringComparison.Ordinal))
        {
            confidence = Math.Min(confidence, 0.50d);
        }

        return Clamp01(confidence);
    }

    private static double ResolveNoInteractionConfidencePenalty(double idlePauseSeconds)
    {
        double quietSeconds = ClampNonNegativeFinite(idlePauseSeconds);
        if (quietSeconds <= 0d)
        {
            return 0.03d;
        }

        if (quietSeconds <= PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds)
        {
            double progress = quietSeconds / PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds;
            return 0.03d + (0.05d * progress);
        }

        if (quietSeconds <= PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            double progress = (quietSeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds)
                / (PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds
                    - PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds);
            return 0.08d + (0.10d * progress);
        }

        double extendedProgress = (quietSeconds - PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
            / (PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
                - PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds);
        return 0.18d + (0.07d * Math.Clamp(extendedProgress, 0d, 1d));
    }

    private static TaskContinuityDecision BuildDecision(
        double continuitySeconds,
        double focusEpisodeCandidateSeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        double contextSwitchSeconds,
        double instabilitySeconds,
        string state,
        string reasonCode)
    {
        return new TaskContinuityDecision(
            TaskContinuitySeconds: ClampNonNegativeFinite(continuitySeconds),
            FocusEpisodeCandidateSeconds: ClampNonNegativeFinite(focusEpisodeCandidateSeconds),
            AnchorAppHash: anchorAppHash,
            IdlePauseSeconds: ClampNonNegativeFinite(idlePauseSeconds),
            ContextSwitchSeconds: ClampNonNegativeFinite(contextSwitchSeconds),
            InstabilitySeconds: ClampNonNegativeFinite(instabilitySeconds),
            State: state,
            Score: ResolveContinuityScore(state),
            ReasonCode: reasonCode);
    }

    private static AttentionSegmentDecision EvaluateAttentionSegment(
        InputBucketRow bucket,
        ChannelEvidence evidence,
        TaskContinuityDecision continuity)
    {
        string reasonCode = continuity.ReasonCode;
        if (string.Equals(reasonCode, "long_break", StringComparison.Ordinal)
            || continuity.IdlePauseSeconds >= PortaExecutionConstants.DistractionEpisodes.AwaySegmentStartSeconds
            || bucket.TrueBreakFlag != 0)
        {
            double awaySeconds = Math.Max(
                continuity.IdlePauseSeconds,
                bucket.TrueBreakSeconds > 0d ? bucket.TrueBreakSeconds : Time.bucketSeconds);
            return new AttentionSegmentDecision(
                Kind: "away",
                SegmentSeconds: ClampNonNegativeFinite(awaySeconds),
                DistractionEpisodeActive: false,
                DistractionEpisodeSeconds: 0d);
        }

        bool contextExcursion = string.Equals(reasonCode, "context_excursion", StringComparison.Ordinal)
            || string.Equals(reasonCode, "context_excursion_decay", StringComparison.Ordinal)
            || string.Equals(reasonCode, "context_shift_reset", StringComparison.Ordinal);
        if (contextExcursion)
        {
            double segmentSeconds = Math.Max(continuity.ContextSwitchSeconds, Time.bucketSeconds);
            bool offTaskDistraction = evidence.AnyInteractionActive
                && continuity.ContextSwitchSeconds >= PortaExecutionConstants.DistractionEpisodes.OffTaskDistractionStartSeconds;
            return new AttentionSegmentDecision(
                Kind: offTaskDistraction ? "off_task_distraction" : "context_excursion",
                SegmentSeconds: ClampNonNegativeFinite(segmentSeconds),
                DistractionEpisodeActive: offTaskDistraction,
                DistractionEpisodeSeconds: offTaskDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d);
        }

        bool instabilityPhase = string.Equals(reasonCode, "focus_unstable", StringComparison.Ordinal)
            || string.Equals(reasonCode, "focus_instability", StringComparison.Ordinal)
            || string.Equals(reasonCode, "instability_reset", StringComparison.Ordinal);
        if (instabilityPhase)
        {
            bool chaoticChurn = bucket.FocusActiveAppCount >= PortaExecutionConstants.DistractionEpisodes.FragmentationHighAppCountMin
                || bucket.FocusSwitchCount >= SessionConstants.SustainedAttention.FocusSwitchesSpikeMin
                || bucket.FocusAppEntropy >= SessionConstants.SustainedAttention.EntropySpikeMin;
            bool fragmentationDistraction = evidence.AnyInteractionActive
                && chaoticChurn
                && continuity.InstabilitySeconds >= PortaExecutionConstants.DistractionEpisodes.FragmentationDistractionStartSeconds;
            double segmentSeconds = Math.Max(continuity.InstabilitySeconds, Time.bucketSeconds);
            return new AttentionSegmentDecision(
                Kind: fragmentationDistraction ? "fragmentation_distraction" : "focus",
                SegmentSeconds: fragmentationDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                DistractionEpisodeActive: fragmentationDistraction,
                DistractionEpisodeSeconds: fragmentationDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d);
        }

        return new AttentionSegmentDecision(
            Kind: "focus",
            SegmentSeconds: 0d,
            DistractionEpisodeActive: false,
            DistractionEpisodeSeconds: 0d);
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
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d);
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d);
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
            double focusEpisodeCandidateSeconds = ReadNonNegativeDouble(root, "focus_episode_candidate_seconds");
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
                InstabilitySeconds: instabilitySeconds,
                FocusEpisodeCandidateSeconds: focusEpisodeCandidateSeconds);
        }
        catch
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d);
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
        double InstabilitySeconds,
        double FocusEpisodeCandidateSeconds);

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
        double FocusEpisodeCandidateSeconds,
        string AnchorAppHash,
        double IdlePauseSeconds,
        double ContextSwitchSeconds,
        double InstabilitySeconds,
        string State,
        double Score,
        string ReasonCode);

    private readonly record struct AttentionSegmentDecision(
        string Kind,
        double SegmentSeconds,
        bool DistractionEpisodeActive,
        double DistractionEpisodeSeconds);

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
