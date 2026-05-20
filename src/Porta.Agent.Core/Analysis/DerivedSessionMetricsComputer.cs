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
    double DistractionEpisodeSeconds = 0d,
    string AttentionSegmentReasonCode = "steady_focus",
    string AttentionSegmentReasonText = "Steady focus episode.",
    string FocusEpisodeReasonCode = "focus_episode_active",
    string FocusEpisodeReasonText = "Focus episode active.",
    // ── Block C — distraction detection runtime state pass-through ─────────
    // Values are 0/empty until Block D wires up the computation.
    double DistrEwmaSwitchRate = 0d,
    double DistrMicroBurstAccumulatedSeconds = 0d,
    string DistrRecentDominantHashesJson = "",
    int    DistrPingPongCycleCount = 0,
    double DistrPingPongWindowStartSeconds = 0d,
    string DistrCandidateAnchorAppHash = "",
    double DistrCandidateAnchorAccumulatedSeconds = 0d,
    double DistrSessionActiveInteractionSeconds = 0d,
    double DistrAnchorLockedSeconds = 0d,
    double DistrEwmaAverageDwellSeconds = 0d,
    double DistrShallowDwellAccumulatedSeconds = 0d,
    double DistrWindowsOffAnchorSeconds = 0d,
    int    DistrWindowsOffAnchorBucketsBack = 0);

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
        EpisodeReason focusEpisodeReason = ResolveFocusEpisodeReason(continuity, attentionSegment);
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
            DistractionEpisodeSeconds: attentionSegment.DistractionEpisodeSeconds,
            AttentionSegmentReasonCode: attentionSegment.ReasonCode,
            AttentionSegmentReasonText: attentionSegment.ReasonText,
            FocusEpisodeReasonCode: focusEpisodeReason.Code,
            FocusEpisodeReasonText: focusEpisodeReason.Text);
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
                DistractionEpisodeSeconds: 0d,
                ReasonCode: "away",
                ReasonText: $"Away for {FormatEpisodeDuration(awaySeconds)}.");
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
                DistractionEpisodeSeconds: offTaskDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                ReasonCode: offTaskDistraction ? "outside_focus_context" : "context_excursion",
                ReasonText: offTaskDistraction
                    ? $"Stayed outside focus context for {FormatEpisodeDuration(segmentSeconds)}."
                    : $"Outside focus context for {FormatEpisodeDuration(segmentSeconds)}.");
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
                DistractionEpisodeSeconds: fragmentationDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                ReasonCode: fragmentationDistraction ? "fragmentation_distraction" : "brief_instability",
                ReasonText: fragmentationDistraction
                    ? $"Rapid switching / fragmentation for {FormatEpisodeDuration(segmentSeconds)}."
                    : $"Brief switching instability for {FormatEpisodeDuration(segmentSeconds)}.");
        }

        return new AttentionSegmentDecision(
            Kind: "focus",
            SegmentSeconds: 0d,
            DistractionEpisodeActive: false,
            DistractionEpisodeSeconds: 0d,
            ReasonCode: ResolveSteadyFocusReasonCode(continuity.ReasonCode),
            ReasonText: ResolveSteadyFocusReasonText(continuity.ReasonCode));
    }

    private static EpisodeReason ResolveFocusEpisodeReason(
        TaskContinuityDecision continuity,
        AttentionSegmentDecision attentionSegment)
    {
        return continuity.ReasonCode switch
        {
            "" => new EpisodeReason("focus_episode_active", "Focus episode active."),
            "focus_settling" => new EpisodeReason("focus_settling", "Focus episode is settling before Ari starts the timer."),
            "passive_focus" => new EpisodeReason("passive_focus", "Focus episode is continuing through a reading or thinking pause."),
            "short_pause" => new EpisodeReason("thinking_pause", "Focus episode is paused during a short reading or thinking pause."),
            "pause_decay" => new EpisodeReason("extended_pause", $"Focus episode is fading after {FormatEpisodeDuration(continuity.IdlePauseSeconds)} of quiet time."),
            "idle_unstable" => new EpisodeReason("unstable_idle", "Focus episode is paused because the idle pattern is unstable."),
            "focus_unavailable" => new EpisodeReason("focus_context_missing", "Focus episode is paused because Ari cannot confirm the focus context."),
            "focus_unavailable_decay" => new EpisodeReason("focus_context_missing_decay", "Focus episode is fading because Ari cannot confirm the focus context."),
            "context_excursion" => new EpisodeReason("context_excursion", $"Focus episode is holding during a short context excursion ({FormatEpisodeDuration(continuity.ContextSwitchSeconds)})."),
            "context_excursion_decay" => new EpisodeReason("context_excursion_decay", $"Focus episode is decaying after {FormatEpisodeDuration(continuity.ContextSwitchSeconds)} outside the focus context."),
            "context_shift_reset" => new EpisodeReason("major_context_shift", $"Major context shift ended the focus episode after {FormatEpisodeDuration(continuity.ContextSwitchSeconds)} outside the focus context."),
            "focus_unstable" => new EpisodeReason("focus_unstable", $"Focus episode is paused because switching has been unstable for {FormatEpisodeDuration(continuity.InstabilitySeconds)}."),
            "focus_instability" => new EpisodeReason("focus_instability", $"Focus episode is decaying because switching has stayed unstable for {FormatEpisodeDuration(continuity.InstabilitySeconds)}."),
            "instability_reset" => new EpisodeReason("instability_reset", $"Sustained instability ended the focus episode after {FormatEpisodeDuration(continuity.InstabilitySeconds)}."),
            "long_break" => new EpisodeReason("long_break", $"Long break ended the focus episode after {FormatEpisodeDuration(attentionSegment.SegmentSeconds)} away."),
            _ => new EpisodeReason(NormalizeReasonCode(continuity.ReasonCode, fallback: "focus_episode_state"), "Focus episode state changed.")
        };
    }

    private static string ResolveSteadyFocusReasonCode(string? continuityReasonCode)
    {
        return continuityReasonCode switch
        {
            "focus_settling" => "focus_settling",
            "passive_focus" => "passive_focus",
            "short_pause" => "thinking_pause",
            "focus_unavailable" => "focus_context_missing",
            _ => "steady_focus"
        };
    }

    private static string ResolveSteadyFocusReasonText(string? continuityReasonCode)
    {
        return continuityReasonCode switch
        {
            "focus_settling" => "Focus episode is settling before Ari starts the timer.",
            "passive_focus" => "Reading or thinking pause inside the focus episode.",
            "short_pause" => "Short thinking pause inside the focus episode.",
            "focus_unavailable" => "Focus context unavailable; Ari is preserving the last known focus episode.",
            _ => "Steady focus episode."
        };
    }

    private static string NormalizeReasonCode(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().Replace('-', '_').ToLowerInvariant();
    }

    private static string FormatEpisodeDuration(double seconds)
    {
        double clampedSeconds = ClampNonNegativeFinite(seconds);
        if (clampedSeconds < 60d)
        {
            return $"{Math.Round(clampedSeconds, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}s";
        }

        int totalSeconds = (int)Math.Round(clampedSeconds, MidpointRounding.AwayFromZero);
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int secs = totalSeconds % 60;
        if (hours > 0)
        {
            return minutes > 0
                ? $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString(CultureInfo.InvariantCulture)}m"
                : $"{hours.ToString(CultureInfo.InvariantCulture)}h";
        }

        return secs > 0 && minutes < 3
            ? $"{minutes.ToString(CultureInfo.InvariantCulture)}m {secs.ToString(CultureInfo.InvariantCulture)}s"
            : $"{minutes.ToString(CultureInfo.InvariantCulture)}m";
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
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0);
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0);
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
            double ewmaSwitchRate = ReadNonNegativeDouble(root, "micro_burst_ewma_switch_rate");
            double microBurstAccumulatedSeconds = ReadNonNegativeDouble(root, "micro_burst_accumulated_seconds");
            string recentDominantHashes = ReadString(root, "ping_pong_recent_hashes_json");
            int pingPongCycleCount = (int)ReadNonNegativeInt64(root, "ping_pong_cycle_count");
            double pingPongWindowStartSeconds = ReadNonNegativeDouble(root, "ping_pong_window_start_seconds");
            string candidateAnchorAppHash = ReadString(root, "candidate_anchor_app_hash");
            double candidateAnchorAccumulatedSeconds = ReadNonNegativeDouble(root, "candidate_anchor_accumulated_seconds");
            double sessionActiveInteractionSeconds = ReadNonNegativeDouble(root, "session_active_interaction_seconds");
            double anchorLockedSeconds = ReadNonNegativeDouble(root, "anchor_locked_seconds");
            double ewmaAverageDwellSeconds = ReadNonNegativeDouble(root, "ewma_average_dwell_seconds");
            double shallowDwellAccumulatedSeconds = ReadNonNegativeDouble(root, "shallow_dwell_accumulated_seconds");
            double windowsOffAnchorSeconds = ReadNonNegativeDouble(root, "windows_off_anchor_seconds");
            int windowsOffAnchorBucketsBack = (int)ReadNonNegativeInt64(root, "windows_off_anchor_buckets_back");
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
                FocusEpisodeCandidateSeconds: focusEpisodeCandidateSeconds,
                EwmaSwitchRate: ewmaSwitchRate,
                MicroBurstAccumulatedSeconds: microBurstAccumulatedSeconds,
                RecentDominantHashes: recentDominantHashes,
                PingPongCycleCount: pingPongCycleCount,
                PingPongWindowStartSeconds: pingPongWindowStartSeconds,
                CandidateAnchorAppHash: candidateAnchorAppHash,
                CandidateAnchorAccumulatedSeconds: candidateAnchorAccumulatedSeconds,
                SessionActiveInteractionSeconds: sessionActiveInteractionSeconds,
                AnchorLockedSeconds: anchorLockedSeconds,
                EwmaAverageDwellSeconds: ewmaAverageDwellSeconds,
                ShallowDwellAccumulatedSeconds: shallowDwellAccumulatedSeconds,
                WindowsOffAnchorSeconds: windowsOffAnchorSeconds,
                WindowsOffAnchorBucketsBack: windowsOffAnchorBucketsBack);
        }
        catch
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0);
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
        // ── Original 11 fields ──────────────────────────────────────────────────
        double TaskContinuitySeconds,
        double IdleFragmentationIndex,
        string AnchorAppHash,
        long   LastUpdateBucketSeq,
        string ReasonCode,
        double IdlePauseSeconds,
        double ContextSwitchSeconds,
        string ContinuityState,
        double ContinuityScore,
        double InstabilitySeconds,
        double FocusEpisodeCandidateSeconds,
        // ── Block B — distraction detection state ───────────────────────────────
        // Pattern 2 – Micro-burst (rapid repeated switching)
        double EwmaSwitchRate,
        double MicroBurstAccumulatedSeconds,
        // Pattern 3 – Ping-pong (A→B→A→B cycle detection)
        // Stored as compact JSON string so it survives the existing read/write path
        string RecentDominantHashes,
        int    PingPongCycleCount,
        double PingPongWindowStartSeconds,
        // Anchor tracking (shared across patterns)
        string CandidateAnchorAppHash,
        double CandidateAnchorAccumulatedSeconds,
        // Pattern 4 – Never-settled (active but no anchor lock)
        double SessionActiveInteractionSeconds,
        double AnchorLockedSeconds,
        // Pattern 5 – Shallow-dwell (grazing)
        double EwmaAverageDwellSeconds,
        double ShallowDwellAccumulatedSeconds,
        // Pattern 8 – Focus-departure (time away from anchor)
        double WindowsOffAnchorSeconds,
        int    WindowsOffAnchorBucketsBack);

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
        double DistractionEpisodeSeconds,
        string ReasonCode,
        string ReasonText);

    private readonly record struct EpisodeReason(
        string Code,
        string Text);

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
