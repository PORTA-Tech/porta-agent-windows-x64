using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
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
    int    DistrWindowsOffAnchorBucketsBack = 0,
    bool   DistrUnfocusedSessionFired = false,
    // ── Block C extension — Phase 2 category-based pattern state ───────────
    double DistrPassiveConsumptionAccumulatedSeconds = 0d,
    int    DistrCommMonitoringReturnCount = 0,
    double DistrCommMonitoringWindowStartSeconds = 0d,
    int    DistrCommMonitoringLastDominantCategory = 0,
    // ── Block C extension — Phase 3 unconditional + session-level state ────
    double DistrSocialMediaAccumulatedSeconds = 0d,
    double DistrGamingLauncherAccumulatedSeconds = 0d,
    double DistrGamingActiveAccumulatedSeconds = 0d,
    int    DistrSocialMediaReturnCount = 0,
    double DistrSocialMediaWindowStartSeconds = 0d,
    int    DistrSocialMediaLastDominantCategory = 0,
    double DistrFocusCategoryAbsentSeconds = 0d,
    double DistrSessionStartDistractionSeconds = 0d,
    bool   DistrSessionStartChecked = false);

internal static class DerivedSessionMetricsComputer
{
    internal static DerivedSessionMetricsResult ComputeForFinalizedBucket(
        SqliteConnection connection,
        SqliteTransaction tx,
        InputBucketRow bucket,
        PrimitiveComputationResult primitive,
        BaselineGatingResult gating,
        bool meetingInProgress = false)
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
        AttentionSegmentDecision attentionSegment = EvaluateAttentionSegment(bucket, evidence, continuity, previous, meetingInProgress);
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
            FocusEpisodeReasonText: focusEpisodeReason.Text,
            // Block D — anchor state
            DistrCandidateAnchorAppHash: continuity.CandidateAnchorAppHash,
            DistrCandidateAnchorAccumulatedSeconds: continuity.CandidateAnchorAccumulatedSeconds,
            DistrAnchorLockedSeconds: continuity.AnchorLockedSeconds,
            // Block E — pattern detection state
            DistrEwmaSwitchRate: attentionSegment.NewEwmaSwitchRate,
            DistrMicroBurstAccumulatedSeconds: attentionSegment.NewMicroBurstAccumulatedSeconds,
            DistrRecentDominantHashesJson: attentionSegment.NewRecentDominantHashesJson,
            DistrPingPongCycleCount: attentionSegment.NewPingPongCycleCount,
            DistrPingPongWindowStartSeconds: attentionSegment.NewPingPongWindowStartSeconds,
            DistrSessionActiveInteractionSeconds: attentionSegment.NewSessionActiveInteractionSeconds,
            DistrEwmaAverageDwellSeconds: attentionSegment.NewEwmaAverageDwellSeconds,
            DistrShallowDwellAccumulatedSeconds: attentionSegment.NewShallowDwellAccumulatedSeconds,
            DistrWindowsOffAnchorSeconds: attentionSegment.NewWindowsOffAnchorSeconds,
            DistrWindowsOffAnchorBucketsBack: attentionSegment.NewWindowsOffAnchorBucketsBack,
            DistrUnfocusedSessionFired: attentionSegment.NewUnfocusedSessionFired,
            DistrPassiveConsumptionAccumulatedSeconds: attentionSegment.NewPassiveConsumptionAccumulatedSeconds,
            DistrCommMonitoringReturnCount: attentionSegment.NewCommMonitoringReturnCount,
            DistrCommMonitoringWindowStartSeconds: attentionSegment.NewCommMonitoringWindowStartSeconds,
            DistrCommMonitoringLastDominantCategory: attentionSegment.NewCommMonitoringLastDominantCategory,
            DistrSocialMediaAccumulatedSeconds: attentionSegment.NewSocialMediaAccumulatedSeconds,
            DistrGamingLauncherAccumulatedSeconds: attentionSegment.NewGamingLauncherAccumulatedSeconds,
            DistrGamingActiveAccumulatedSeconds: attentionSegment.NewGamingActiveAccumulatedSeconds,
            DistrSocialMediaReturnCount: attentionSegment.NewSocialMediaReturnCount,
            DistrSocialMediaWindowStartSeconds: attentionSegment.NewSocialMediaWindowStartSeconds,
            DistrSocialMediaLastDominantCategory: attentionSegment.NewSocialMediaLastDominantCategory,
            DistrFocusCategoryAbsentSeconds: attentionSegment.NewFocusCategoryAbsentSeconds,
            DistrSessionStartDistractionSeconds: attentionSegment.NewSessionStartDistractionSeconds,
            DistrSessionStartChecked: attentionSegment.NewSessionStartChecked);
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
        double idlePauseSeconds = ResolveIdlePauseSeconds(previous, bucket);
        double instabilitySeconds = ClampNonNegativeFinite(previous.InstabilitySeconds);

        // ── Two-stage anchor logic (Block D) ────────────────────────────────────
        string currentDominant = evidence.FocusAvailable && !string.IsNullOrWhiteSpace(bucket.DominantAppHash)
            ? bucket.DominantAppHash
            : string.Empty;
        string anchorAppHash;
        string candidateAnchorAppHash;
        double candidateAnchorAccumulatedSeconds;
        double anchorLockedSeconds;

        if (resetForGap)
        {
            // Gap: clear everything and restart candidate accumulation from current app.
            anchorAppHash = currentDominant;
            candidateAnchorAppHash = string.Empty;
            candidateAnchorAccumulatedSeconds = 0d;
            anchorLockedSeconds = 0d;
        }
        else if (string.IsNullOrWhiteSpace(previous.AnchorAppHash))
        {
            // Stage 1 — candidate accumulation: anchor not yet locked.
            if (string.IsNullOrWhiteSpace(previous.CandidateAnchorAppHash))
            {
                // First time: seed candidate with whatever is dominant now.
                candidateAnchorAppHash = currentDominant;
                candidateAnchorAccumulatedSeconds = evidence.AnyInteractionActive && !string.IsNullOrWhiteSpace(currentDominant)
                    ? Time.bucketSeconds : 0d;
                anchorAppHash = string.Empty;
                anchorLockedSeconds = 0d;
            }
            else if (!string.IsNullOrWhiteSpace(currentDominant)
                && string.Equals(currentDominant, previous.CandidateAnchorAppHash, StringComparison.Ordinal))
            {
                // Still on the same candidate — accumulate if interaction active.
                double accumulated = evidence.AnyInteractionActive
                    ? ClampNonNegativeFinite(previous.CandidateAnchorAccumulatedSeconds + Time.bucketSeconds)
                    : previous.CandidateAnchorAccumulatedSeconds;

                if (accumulated >= PortaExecutionConstants.DistractionEpisodes.AnchorCandidateRequiredSeconds)
                {
                    // Candidate has held long enough — lock the anchor.
                    anchorAppHash = previous.CandidateAnchorAppHash;
                    anchorLockedSeconds = previous.SessionActiveInteractionSeconds;
                    candidateAnchorAppHash = string.Empty;
                    candidateAnchorAccumulatedSeconds = 0d;
                }
                else
                {
                    anchorAppHash = string.Empty;
                    candidateAnchorAppHash = previous.CandidateAnchorAppHash;
                    candidateAnchorAccumulatedSeconds = accumulated;
                    anchorLockedSeconds = 0d;
                }
            }
            else if (!string.IsNullOrWhiteSpace(currentDominant))
            {
                // Different app appeared — reset candidate.
                anchorAppHash = string.Empty;
                candidateAnchorAppHash = currentDominant;
                candidateAnchorAccumulatedSeconds = 0d;
                anchorLockedSeconds = 0d;
            }
            else
            {
                // No dominant app available yet — preserve existing candidate.
                anchorAppHash = string.Empty;
                candidateAnchorAppHash = previous.CandidateAnchorAppHash;
                candidateAnchorAccumulatedSeconds = previous.CandidateAnchorAccumulatedSeconds;
                anchorLockedSeconds = 0d;
            }
        }
        else
        {
            // Stage 2 — anchor is locked: watch for migration.
            anchorLockedSeconds = previous.AnchorLockedSeconds;
            bool onAnchor = string.IsNullOrWhiteSpace(currentDominant)
                || string.Equals(currentDominant, previous.AnchorAppHash, StringComparison.Ordinal);

            if (onAnchor || !evidence.AnyInteractionActive)
            {
                // Back on anchor (or no active interaction) — reset migration counter.
                anchorAppHash = previous.AnchorAppHash;
                candidateAnchorAppHash = string.Empty;
                candidateAnchorAccumulatedSeconds = 0d;
            }
            else if (string.Equals(currentDominant, previous.CandidateAnchorAppHash, StringComparison.Ordinal))
            {
                // Still on the same migration candidate — accumulate.
                double accumulated = ClampNonNegativeFinite(
                    previous.CandidateAnchorAccumulatedSeconds + Time.bucketSeconds);

                if (accumulated >= PortaExecutionConstants.DistractionEpisodes.AnchorMigrationThresholdSeconds)
                {
                    // New app has dominated long enough — migrate anchor.
                    anchorAppHash = currentDominant;
                    anchorLockedSeconds = previous.SessionActiveInteractionSeconds;
                    candidateAnchorAppHash = string.Empty;
                    candidateAnchorAccumulatedSeconds = 0d;
                }
                else
                {
                    anchorAppHash = previous.AnchorAppHash;
                    candidateAnchorAppHash = currentDominant;
                    candidateAnchorAccumulatedSeconds = accumulated;
                }
            }
            else
            {
                // New off-anchor app — start fresh migration counter.
                anchorAppHash = previous.AnchorAppHash;
                candidateAnchorAppHash = currentDominant;
                candidateAnchorAccumulatedSeconds = Time.bucketSeconds;
            }
        }

        string currentContextAppHash = !string.IsNullOrWhiteSpace(currentDominant)
            ? currentDominant
            : anchorAppHash;

        if (resetForGap)
        {
            previousContinuitySeconds = 0d;
            previousCandidateSeconds = 0d;
            currentContextAppHash = anchorAppHash;
            idlePauseSeconds = bucket.IdleSeconds <= 0.25d
                ? 0d
                : ClampNonNegativeFinite(bucket.IdleSeconds);
            instabilitySeconds = 0d;
        }

        if (bucket.TrueBreakSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds
            || idlePauseSeconds >= PortaExecutionConstants.DerivedSessionMetrics.IdleResetSeconds)
        {
            // Long break: clear candidate fields per spec.
            return BuildDecision(
                continuitySeconds: 0d,
                focusEpisodeCandidateSeconds: 0d,
                anchorAppHash: string.IsNullOrWhiteSpace(currentContextAppHash) ? anchorAppHash : currentContextAppHash,
                idlePauseSeconds: idlePauseSeconds,
                contextSwitchSeconds: 0d,
                instabilitySeconds: 0d,
                state: "reset",
                reasonCode: "long_break",
                candidateAnchorAppHash: string.Empty,
                candidateAnchorAccumulatedSeconds: 0d,
                anchorLockedSeconds: anchorLockedSeconds);
        }

        if (!evidence.FocusAvailable)
        {
            return EvaluateNoFocusContinuity(previousContinuitySeconds, previousCandidateSeconds, anchorAppHash, idlePauseSeconds, evidence,
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
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
                    0d, 0d, currentContextAppHash, idlePauseSeconds, 0d, instabilitySeconds,
                    "reset", "instability_reset",
                    candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
            }

            if (spikeInstability)
            {
                return BuildDecision(
                    ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.InstabilityDecayPerBucketSeconds),
                    ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
                    currentContextAppHash, idlePauseSeconds, 0d, instabilitySeconds,
                    "decay", "focus_instability",
                    candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
            }

            return BuildDecision(
                previousContinuitySeconds, previousCandidateSeconds, currentContextAppHash, idlePauseSeconds, 0d, instabilitySeconds,
                "pause", "focus_unstable",
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        if (evidence.AnyInteractionActive)
        {
            return AdvanceFocusEpisode(
                previousContinuitySeconds, previousCandidateSeconds, currentContextAppHash,
                idlePauseSeconds: 0d, contextSwitchSeconds: 0d, instabilitySeconds: 0d, reasonCode: string.Empty,
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        if (CanAccumulatePassiveFocus(previousContinuitySeconds, previousCandidateSeconds, idlePauseSeconds))
        {
            return AdvanceFocusEpisode(
                previousContinuitySeconds, previousCandidateSeconds, currentContextAppHash,
                idlePauseSeconds, contextSwitchSeconds: 0d, instabilitySeconds: 0d, reasonCode: "passive_focus",
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdlePauseMaxSeconds)
        {
            return BuildDecision(
                ClampNonNegativeFinite(previousContinuitySeconds - PortaExecutionConstants.DerivedSessionMetrics.IdleDecayPerBucketSeconds),
                ClampNonNegativeFinite(previousCandidateSeconds - Time.bucketSeconds),
                currentContextAppHash, idlePauseSeconds, 0d, 0d, "decay", "pause_decay",
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        if (idlePauseSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleAccumulateMaxSeconds)
        {
            return BuildDecision(
                previousContinuitySeconds, previousCandidateSeconds,
                currentContextAppHash, idlePauseSeconds, 0d, 0d, "pause", "short_pause",
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        if (bucket.IdleSeconds > PortaExecutionConstants.DerivedSessionMetrics.IdleSecondsStableMax)
        {
            return BuildDecision(
                previousContinuitySeconds, previousCandidateSeconds,
                currentContextAppHash, idlePauseSeconds, 0d, 0d, "pause", "idle_unstable",
                candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
        }

        return BuildDecision(
            previousContinuitySeconds, previousCandidateSeconds,
            currentContextAppHash, idlePauseSeconds, 0d, 0d, "pause", "passive_focus",
            candidateAnchorAppHash, candidateAnchorAccumulatedSeconds, anchorLockedSeconds);
    }

    private static TaskContinuityDecision EvaluateNoFocusContinuity(
        double previousContinuitySeconds,
        double previousCandidateSeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        ChannelEvidence evidence,
        string candidateAnchorAppHash = "",
        double candidateAnchorAccumulatedSeconds = 0d,
        double anchorLockedSeconds = 0d)
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
                "focus_unavailable_decay",
                candidateAnchorAppHash,
                candidateAnchorAccumulatedSeconds,
                anchorLockedSeconds);
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
                "focus_unavailable",
                candidateAnchorAppHash,
                candidateAnchorAccumulatedSeconds,
                anchorLockedSeconds);
        }

        return BuildDecision(
            previousContinuitySeconds,
            previousCandidateSeconds,
            anchorAppHash,
            idlePauseSeconds,
            0d,
            0d,
            "pause",
            "focus_unavailable",
            candidateAnchorAppHash,
            candidateAnchorAccumulatedSeconds,
            anchorLockedSeconds);
    }

    private static TaskContinuityDecision AdvanceFocusEpisode(
        double previousContinuitySeconds,
        double previousCandidateSeconds,
        string anchorAppHash,
        double idlePauseSeconds,
        double contextSwitchSeconds,
        double instabilitySeconds,
        string reasonCode,
        string candidateAnchorAppHash = "",
        double candidateAnchorAccumulatedSeconds = 0d,
        double anchorLockedSeconds = 0d)
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
                reasonCode,
                candidateAnchorAppHash,
                candidateAnchorAccumulatedSeconds,
                anchorLockedSeconds);
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
                reasonCode,
                candidateAnchorAppHash,
                candidateAnchorAccumulatedSeconds,
                anchorLockedSeconds);
        }

        return BuildDecision(
            0d,
            candidateSeconds,
            anchorAppHash,
            idlePauseSeconds,
            contextSwitchSeconds,
            instabilitySeconds,
            "pause",
            "focus_settling",
            candidateAnchorAppHash,
            candidateAnchorAccumulatedSeconds,
            anchorLockedSeconds);
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
        string reasonCode,
        string candidateAnchorAppHash = "",
        double candidateAnchorAccumulatedSeconds = 0d,
        double anchorLockedSeconds = 0d)
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
            ReasonCode: reasonCode,
            CandidateAnchorAppHash: candidateAnchorAppHash,
            CandidateAnchorAccumulatedSeconds: ClampNonNegativeFinite(candidateAnchorAccumulatedSeconds),
            AnchorLockedSeconds: ClampNonNegativeFinite(anchorLockedSeconds));
    }

    private static double GetTimeOfDayMultiplier()
    {
        int h = DateTime.Now.Hour;
        if (h >= PortaExecutionConstants.TimeOfDayMultipliers.EarlyMorningStart && h < PortaExecutionConstants.TimeOfDayMultipliers.EarlyMorningEnd)
            return PortaExecutionConstants.TimeOfDayMultipliers.EarlyMorningMultiplier;
        if (h >= PortaExecutionConstants.TimeOfDayMultipliers.MorningStart   && h < PortaExecutionConstants.TimeOfDayMultipliers.MorningEnd)
            return PortaExecutionConstants.TimeOfDayMultipliers.MorningMultiplier;
        if (h >= PortaExecutionConstants.TimeOfDayMultipliers.LunchStart     && h < PortaExecutionConstants.TimeOfDayMultipliers.LunchEnd)
            return PortaExecutionConstants.TimeOfDayMultipliers.LunchMultiplier;
        if (h >= PortaExecutionConstants.TimeOfDayMultipliers.AfternoonStart && h < PortaExecutionConstants.TimeOfDayMultipliers.AfternoonEnd)
            return PortaExecutionConstants.TimeOfDayMultipliers.AfternoonMultiplier;
        if (h >= PortaExecutionConstants.TimeOfDayMultipliers.EveningStart   && h < PortaExecutionConstants.TimeOfDayMultipliers.EveningEnd)
            return PortaExecutionConstants.TimeOfDayMultipliers.EveningMultiplier;
        return PortaExecutionConstants.TimeOfDayMultipliers.LateEveningMultiplier;
    }

    private static AttentionSegmentDecision EvaluateAttentionSegment(
        InputBucketRow bucket,
        ChannelEvidence evidence,
        TaskContinuityDecision continuity,
        DerivedSessionRuntimeState previous,
        bool meetingInProgress = false)
    {
        double mult = GetTimeOfDayMultiplier();

        // ── Phase 3: Unconditional Gaming / SocialMedia patterns ─────────────
        // Computed before ALL session-state gates. Accumulators grow/decay based
        // purely on what is currently foregrounded + whether the user is interacting.
        // These patterns fire even when session state is away/unstable/excursion.
        string p3Dom = evidence.FocusAvailable ? bucket.DominantAppHash : string.Empty;
        AppCategory p3Category = AppCategoryClassifier.GetCategoryForHash(p3Dom);
        double p3SessionActive = ClampNonNegativeFinite(
            previous.SessionActiveInteractionSeconds + Time.bucketSeconds);

        double socialMediaAcc = evidence.AnyInteractionActive && p3Category == AppCategory.SocialMedia
            ? ClampNonNegativeFinite(previous.SocialMediaAccumulatedSeconds + Time.bucketSeconds)
            : ClampNonNegativeFinite(previous.SocialMediaAccumulatedSeconds - Time.bucketSeconds);

        double gamingLauncherAcc = evidence.AnyInteractionActive && p3Category == AppCategory.GamingLauncher
            ? ClampNonNegativeFinite(previous.GamingLauncherAccumulatedSeconds + Time.bucketSeconds)
            : ClampNonNegativeFinite(previous.GamingLauncherAccumulatedSeconds - Time.bucketSeconds);

        double gamingActiveAcc = evidence.AnyInteractionActive && p3Category == AppCategory.Gaming
            ? ClampNonNegativeFinite(previous.GamingActiveAccumulatedSeconds + Time.bucketSeconds)
            : ClampNonNegativeFinite(previous.GamingActiveAccumulatedSeconds - Time.bucketSeconds);

        // Pattern 12 — SocialMedia return-loop state
        int socialMediaReturnCount = previous.SocialMediaReturnCount;
        double socialMediaWindowStart = previous.SocialMediaWindowStartSeconds;
        int socialMediaLastCategory = previous.SocialMediaLastDominantCategory;
        if (p3SessionActive - socialMediaWindowStart >
            PortaExecutionConstants.DistractionEpisodes.SocialMediaLoopWindowSeconds)
        {
            socialMediaReturnCount = 0;
            socialMediaWindowStart = p3SessionActive;
        }
        if (evidence.AnyInteractionActive)
        {
            bool returnedToSocial = p3Category == AppCategory.SocialMedia
                && socialMediaLastCategory != (int)AppCategory.SocialMedia;
            if (returnedToSocial) socialMediaReturnCount++;
            socialMediaLastCategory = (int)p3Category;
        }

        // Pattern 9: SocialMedia continuous duration (unconditional)
        if (socialMediaAcc >= PortaExecutionConstants.DistractionEpisodes.SocialMediaDistractionMinSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.SocialMediaDistraction, SegmentSeconds: socialMediaAcc,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: socialMediaAcc,
                ReasonCode: "social_media_distraction", ReasonText: "Social media use during work time.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: p3SessionActive,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: previous.PassiveConsumptionAccumulatedSeconds,
                NewCommMonitoringReturnCount: previous.CommMonitoringReturnCount,
                NewCommMonitoringWindowStartSeconds: previous.CommMonitoringWindowStartSeconds,
                NewCommMonitoringLastDominantCategory: previous.CommMonitoringLastDominantCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // Pattern 10: Gaming launcher open (unconditional)
        if (gamingLauncherAcc >= PortaExecutionConstants.DistractionEpisodes.GamingLauncherMinSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.GamingLauncherDistraction, SegmentSeconds: gamingLauncherAcc,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: gamingLauncherAcc,
                ReasonCode: "gaming_launcher_distraction", ReasonText: "Gaming launcher browsed during work time.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: p3SessionActive,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: previous.PassiveConsumptionAccumulatedSeconds,
                NewCommMonitoringReturnCount: previous.CommMonitoringReturnCount,
                NewCommMonitoringWindowStartSeconds: previous.CommMonitoringWindowStartSeconds,
                NewCommMonitoringLastDominantCategory: previous.CommMonitoringLastDominantCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // Pattern 11: Active game running (unconditional — no mult, 60 s is the hard floor)
        if (gamingActiveAcc >= PortaExecutionConstants.DistractionEpisodes.GamingActiveMinSeconds)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.ActiveGamingDistraction, SegmentSeconds: gamingActiveAcc,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: gamingActiveAcc,
                ReasonCode: "active_gaming_distraction", ReasonText: "Active gaming during work time.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: p3SessionActive,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: previous.PassiveConsumptionAccumulatedSeconds,
                NewCommMonitoringReturnCount: previous.CommMonitoringReturnCount,
                NewCommMonitoringWindowStartSeconds: previous.CommMonitoringWindowStartSeconds,
                NewCommMonitoringLastDominantCategory: previous.CommMonitoringLastDominantCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // Pattern 12: SocialMedia return loop (unconditional)
        if (socialMediaReturnCount >= PortaExecutionConstants.DistractionEpisodes.SocialMediaLoopMinReturns)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.SocialMediaLoop, SegmentSeconds: p3SessionActive - socialMediaWindowStart,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: p3SessionActive - socialMediaWindowStart,
                ReasonCode: "social_media_loop", ReasonText: "Repeated check-in loop to social media.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: p3SessionActive,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: previous.PassiveConsumptionAccumulatedSeconds,
                NewCommMonitoringReturnCount: previous.CommMonitoringReturnCount,
                NewCommMonitoringWindowStartSeconds: previous.CommMonitoringWindowStartSeconds,
                NewCommMonitoringLastDominantCategory: previous.CommMonitoringLastDominantCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Session-state gates (away / excursion / instability / no-interaction) ─
        string reasonCode = continuity.ReasonCode;
        if (string.Equals(reasonCode, "long_break", StringComparison.Ordinal)
            || continuity.IdlePauseSeconds >= PortaExecutionConstants.DistractionEpisodes.AwaySegmentStartSeconds
            || bucket.TrueBreakFlag != 0)
        {
            double awaySeconds = Math.Max(
                continuity.IdlePauseSeconds,
                bucket.TrueBreakSeconds > 0d ? bucket.TrueBreakSeconds : Time.bucketSeconds);

            // Phase 2b — meeting measurement: low input while a meeting app is foreground is
            // ATTENTIVE-IN-MEETING, not away. Without mic/camera (which would break consent)
            // "listening" and "stepped away" are indistinguishable — this is the disclosed,
            // consent-safe ceiling. Capped so a machine parked on Zoom for hours becomes away.
            bool attentiveInMeeting = meetingInProgress
                && continuity.IdlePauseSeconds < PortaExecutionConstants.MeetingMeasurement.MaxStillSeconds
                && bucket.TrueBreakSeconds < PortaExecutionConstants.MeetingMeasurement.MaxStillSeconds;

            // Away: carry session interaction seconds but preserve unfocused flag
            return new AttentionSegmentDecision(
                Kind: attentiveInMeeting ? AttentionSegmentKinds.InMeeting : AttentionSegmentKinds.Away,
                SegmentSeconds: ClampNonNegativeFinite(awaySeconds),
                DistractionEpisodeActive: false,
                DistractionEpisodeSeconds: 0d,
                ReasonCode: attentiveInMeeting ? "in_meeting" : "away",
                ReasonText: attentiveInMeeting
                    ? $"In a meeting ({FormatEpisodeDuration(awaySeconds)})."
                    : $"Away for {FormatEpisodeDuration(awaySeconds)}.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: 0d,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: previous.SessionActiveInteractionSeconds,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: 0d,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
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
                Kind: offTaskDistraction ? AttentionSegmentKinds.OffTaskDistraction : AttentionSegmentKinds.ContextExcursion,
                SegmentSeconds: ClampNonNegativeFinite(segmentSeconds),
                DistractionEpisodeActive: offTaskDistraction,
                DistractionEpisodeSeconds: offTaskDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                ReasonCode: offTaskDistraction ? "outside_focus_context" : "context_excursion",
                ReasonText: offTaskDistraction
                    ? $"Stayed outside focus context for {FormatEpisodeDuration(segmentSeconds)}."
                    : $"Outside focus context for {FormatEpisodeDuration(segmentSeconds)}.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: previous.SessionActiveInteractionSeconds,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
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
                Kind: fragmentationDistraction ? AttentionSegmentKinds.FragmentationDistraction : AttentionSegmentKinds.Focus,
                SegmentSeconds: fragmentationDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                DistractionEpisodeActive: fragmentationDistraction,
                DistractionEpisodeSeconds: fragmentationDistraction ? ClampNonNegativeFinite(segmentSeconds) : 0d,
                ReasonCode: fragmentationDistraction ? "fragmentation_distraction" : "brief_instability",
                ReasonText: fragmentationDistraction
                    ? $"Rapid switching / fragmentation for {FormatEpisodeDuration(segmentSeconds)}."
                    : $"Brief switching instability for {FormatEpisodeDuration(segmentSeconds)}.",
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: previous.SessionActiveInteractionSeconds,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Steady-focus branch: run all new detection patterns ──────────────
        // Guard: skip new patterns if not interacting
        if (!evidence.AnyInteractionActive)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.Focus, SegmentSeconds: 0d, DistractionEpisodeActive: false, DistractionEpisodeSeconds: 0d,
                ReasonCode: ResolveSteadyFocusReasonCode(continuity.ReasonCode),
                ReasonText: ResolveSteadyFocusReasonText(continuity.ReasonCode),
                NewEwmaSwitchRate: previous.EwmaSwitchRate,
                NewMicroBurstAccumulatedSeconds: previous.MicroBurstAccumulatedSeconds,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount,
                NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: previous.SessionActiveInteractionSeconds,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds,
                NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc,
                NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc,
                NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart,
                NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Pattern 2: Rolling micro-burst ────────────────────────────────────
        double ewmaSwitchRate = previous.EwmaSwitchRate;
        if (bucket.FocusSwitchCount >= PortaExecutionConstants.DistractionEpisodes.MicroBurstMinSwitchesPerBucket)
        {
            ewmaSwitchRate = PortaExecutionConstants.DistractionEpisodes.MicroBurstEwmaAlpha * bucket.FocusSwitchCount
                + (1d - PortaExecutionConstants.DistractionEpisodes.MicroBurstEwmaAlpha) * previous.EwmaSwitchRate;
        }
        double microBurstAccSec = ewmaSwitchRate > PortaExecutionConstants.DistractionEpisodes.MicroBurstSwitchRateThreshold
            ? ClampNonNegativeFinite(previous.MicroBurstAccumulatedSeconds + Time.bucketSeconds)
            : ClampNonNegativeFinite(previous.MicroBurstAccumulatedSeconds - Time.bucketSeconds);

        if (microBurstAccSec >= PortaExecutionConstants.DistractionEpisodes.MicroBurstMinTriggerSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.RollingMicroBurst, SegmentSeconds: microBurstAccSec,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: microBurstAccSec,
                ReasonCode: "rolling_micro_burst", ReasonText: "Rapid repeated app switching detected.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: previous.RecentDominantHashes,
                NewPingPongCycleCount: previous.PingPongCycleCount, NewPingPongWindowStartSeconds: previous.PingPongWindowStartSeconds,
                NewSessionActiveInteractionSeconds: previous.SessionActiveInteractionSeconds + Time.bucketSeconds,
                NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds, NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds, NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Pattern 3: Ping-pong revisit ──────────────────────────────────────
        string currentDom = evidence.FocusAvailable ? bucket.DominantAppHash : string.Empty;
        double sessionActiveSec = ClampNonNegativeFinite(previous.SessionActiveInteractionSeconds + Time.bucketSeconds);

        // Update ring buffer (keep last 4 unique-consecutive entries)
        List<(string Hash, double T)> ring = ParseRingBuffer(previous.RecentDominantHashes);
        if (ring.Count > 0 && string.Equals(ring[^1].Hash, currentDom, StringComparison.Ordinal))
            ring[^1] = (currentDom, sessionActiveSec);
        else
        {
            if (!string.IsNullOrWhiteSpace(currentDom)) ring.Add((currentDom, sessionActiveSec));
            if (ring.Count > 4) ring.RemoveAt(0);
        }
        string updatedHashesJson = SerializeRingBuffer(ring);

        int pingPongCycles = previous.PingPongCycleCount;
        double pingPongWindowStart = previous.PingPongWindowStartSeconds;
        // Reset window if expired
        if (sessionActiveSec - pingPongWindowStart > PortaExecutionConstants.DistractionEpisodes.PingPongWindowSeconds * mult)
        {
            pingPongCycles = 0;
            pingPongWindowStart = sessionActiveSec;
        }
        // Detect ping-pong: last 3+ entries alternate between exactly 2 hashes
        if (ring.Count >= 3)
        {
            var distinct = ring.Select(r => r.Hash).Distinct().ToList();
            if (distinct.Count == 2)
            {
                bool alternates = true;
                for (int i = 1; i < ring.Count; i++)
                    if (string.Equals(ring[i].Hash, ring[i - 1].Hash, StringComparison.Ordinal)) { alternates = false; break; }
                // Editor <-> browser cycling while working is normal, not ping-pong distraction:
                // suppress when either side of the loop is a focus-positive (Dev/Productivity) app.
                bool eitherFocusPositive = distinct.Any(h =>
                    AppCategoryClassifier.IsFocusPositive(AppCategoryClassifier.GetCategoryForHash(h)));
                if (alternates && !eitherFocusPositive)
                {
                    double totalTime = ring[^1].T - ring[0].T;
                    double avgDwell = totalTime / Math.Max(ring.Count - 1, 1);
                    if (avgDwell <= PortaExecutionConstants.DistractionEpisodes.PingPongMaxDwellPerHopSeconds)
                        pingPongCycles++;
                }
            }
        }
        if (pingPongCycles >= PortaExecutionConstants.DistractionEpisodes.PingPongMinCycles)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.ReturnLoopDistraction, SegmentSeconds: sessionActiveSec - pingPongWindowStart,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: sessionActiveSec - pingPongWindowStart,
                ReasonCode: "return_loop_distraction", ReasonText: "Repetitive app ping-pong detected.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson, NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: previous.UnfocusedSessionFired,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds, NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds, NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Pattern 4: Never settled ──────────────────────────────────────────
        bool anchorEverLocked = !string.IsNullOrWhiteSpace(continuity.AnchorAppHash) || continuity.AnchorLockedSeconds > 0d;
        bool unfocusedFired = previous.UnfocusedSessionFired;
        if (!unfocusedFired
            && !anchorEverLocked
            && sessionActiveSec >= PortaExecutionConstants.DistractionEpisodes.NeverSettledActiveInteractionMinutes * 60d * mult)
        {
            unfocusedFired = true;
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.UnfocusedSession, SegmentSeconds: sessionActiveSec,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: sessionActiveSec,
                ReasonCode: "unfocused_session", ReasonText: "Active session without a stable focus anchor.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson, NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: true,
                NewEwmaAverageDwellSeconds: previous.EwmaAverageDwellSeconds, NewShallowDwellAccumulatedSeconds: previous.ShallowDwellAccumulatedSeconds,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds, NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Pattern 5: Shallow dwell ──────────────────────────────────────────
        double ewmaDwell = previous.EwmaAverageDwellSeconds;
        double shallowDwellAcc = previous.ShallowDwellAccumulatedSeconds;
        if (bucket.FocusSwitchCount >= PortaExecutionConstants.DistractionEpisodes.ShallowDwellMinSwitchesPerBucket)
        {
            double currentDwell = Time.bucketSeconds / Math.Max(bucket.FocusSwitchCount, 1d);
            ewmaDwell = PortaExecutionConstants.DistractionEpisodes.ShallowDwellEwmaAlpha * currentDwell
                + (1d - PortaExecutionConstants.DistractionEpisodes.ShallowDwellEwmaAlpha) * previous.EwmaAverageDwellSeconds;
            shallowDwellAcc = ewmaDwell < PortaExecutionConstants.DistractionEpisodes.ShallowDwellCeilingSeconds
                ? ClampNonNegativeFinite(shallowDwellAcc + Time.bucketSeconds)
                : ClampNonNegativeFinite(shallowDwellAcc - Time.bucketSeconds);
        }
        else
        {
            shallowDwellAcc = ClampNonNegativeFinite(shallowDwellAcc - Time.bucketSeconds);
        }
        if (shallowDwellAcc >= PortaExecutionConstants.DistractionEpisodes.ShallowDwellMinTriggerSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.ShallowDwellDistraction, SegmentSeconds: shallowDwellAcc,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: shallowDwellAcc,
                ReasonCode: "shallow_dwell_distraction", ReasonText: "Sustained grazing behaviour detected.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson, NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds, NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: previous.FocusCategoryAbsentSeconds,
                NewSessionStartDistractionSeconds: previous.SessionStartDistractionSeconds,
                NewSessionStartChecked: previous.SessionStartChecked);
        }

        // ── Pattern 1 & 7: Category-based state update ────────────────────────
        AppCategory currentCategory = AppCategoryClassifier.GetCategoryForHash(currentDom);

        // ── Phase 3: Session-level state (focus-category absence + session-start quality) ──
        // Pattern 13 — Focus category absent.
        // Only KNOWN-distracting categories accumulate focus-absence. Unknown (e.g. browsers we
        // cannot classify yet) and Communication are treated as NEUTRAL — neither focus nor
        // distraction — so ordinary browser/research/chat time is no longer mislabeled as
        // "no productive app in focus". (Focus-positive Dev/Productivity also resets, as before.)
        bool distractingCategory = currentCategory is AppCategory.Entertainment
            or AppCategory.SocialMedia
            or AppCategory.Gaming
            or AppCategory.GamingLauncher;
        double focusCategoryAbsSec = distractingCategory
            ? ClampNonNegativeFinite(previous.FocusCategoryAbsentSeconds + Time.bucketSeconds)
            : 0d;

        // Pattern 14 — Bad session start: one-shot check after first 10 min of session.
        bool sessionStartChecked = previous.SessionStartChecked;
        double sessionStartDistrSec = previous.SessionStartDistractionSeconds;
        if (!sessionStartChecked)
        {
            bool isDistractionStart = currentCategory == AppCategory.SocialMedia
                || currentCategory == AppCategory.Entertainment
                || currentCategory == AppCategory.Gaming
                || currentCategory == AppCategory.GamingLauncher;
            if (sessionActiveSec <= PortaExecutionConstants.DistractionEpisodes.SessionStartQualityWindowSeconds && isDistractionStart)
                sessionStartDistrSec = ClampNonNegativeFinite(sessionStartDistrSec + Time.bucketSeconds);
        }

        // Pattern 1 — Passive consumption accumulator
        double passiveConsumptionAcc = previous.PassiveConsumptionAccumulatedSeconds;
        if (currentCategory == AppCategory.Entertainment && evidence.AnyInteractionActive)
            passiveConsumptionAcc = ClampNonNegativeFinite(passiveConsumptionAcc + Time.bucketSeconds);
        else
            passiveConsumptionAcc = ClampNonNegativeFinite(passiveConsumptionAcc - Time.bucketSeconds);

        // Pattern 7 — Communication monitoring loop
        int commReturnCount = previous.CommMonitoringReturnCount;
        double commWindowStart = previous.CommMonitoringWindowStartSeconds;
        int commLastCategory = previous.CommMonitoringLastDominantCategory;

        // Reset comm window if expired
        if (sessionActiveSec - commWindowStart > PortaExecutionConstants.DistractionEpisodes.CommMonitoringWindowSeconds * mult)
        {
            commReturnCount = 0;
            commWindowStart = sessionActiveSec;
        }
        // Detect transition INTO Communication from a different category
        bool returnedToComm = currentCategory == AppCategory.Communication
            && commLastCategory != (int)AppCategory.Communication;
        if (returnedToComm)
            commReturnCount++;
        commLastCategory = (int)currentCategory;

        // ── Pattern 1: Passive consumption ────────────────────────────────────
        if (passiveConsumptionAcc >= PortaExecutionConstants.DistractionEpisodes.PassiveConsumptionMinTriggerSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.PassiveConsumption, SegmentSeconds: passiveConsumptionAcc,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: passiveConsumptionAcc,
                ReasonCode: "passive_consumption", ReasonText: "Passive media consumption during work time.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson,
                NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
                NewCommMonitoringReturnCount: commReturnCount,
                NewCommMonitoringWindowStartSeconds: commWindowStart,
                NewCommMonitoringLastDominantCategory: commLastCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
                NewSessionStartDistractionSeconds: sessionStartDistrSec,
                NewSessionStartChecked: sessionStartChecked);
        }

        // ── Pattern 7: Communication monitoring loop ───────────────────────────
        if (commReturnCount >= PortaExecutionConstants.DistractionEpisodes.CommMonitoringMinReturns)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.CommMonitoringLoop, SegmentSeconds: sessionActiveSec - commWindowStart,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: sessionActiveSec - commWindowStart,
                ReasonCode: "comm_monitoring_loop", ReasonText: "Repeated check-in loop to communication apps.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson,
                NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
                NewCommMonitoringReturnCount: commReturnCount,
                NewCommMonitoringWindowStartSeconds: commWindowStart,
                NewCommMonitoringLastDominantCategory: commLastCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
                NewSessionStartDistractionSeconds: sessionStartDistrSec,
                NewSessionStartChecked: sessionStartChecked);
        }

        // ── Pattern 13: Focus category absent ────────────────────────────────
        if (focusCategoryAbsSec >= PortaExecutionConstants.DistractionEpisodes.FocusCategoryAbsentMinSeconds * mult)
        {
            return new AttentionSegmentDecision(
                Kind: AttentionSegmentKinds.FocusCategoryAbsent, SegmentSeconds: focusCategoryAbsSec,
                DistractionEpisodeActive: true, DistractionEpisodeSeconds: focusCategoryAbsSec,
                ReasonCode: "focus_category_absent",
                ReasonText: $"No productive app in focus for {FormatEpisodeDuration(focusCategoryAbsSec)}.",
                NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                NewRecentDominantHashesJson: updatedHashesJson,
                NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
                NewCommMonitoringReturnCount: commReturnCount,
                NewCommMonitoringWindowStartSeconds: commWindowStart,
                NewCommMonitoringLastDominantCategory: commLastCategory,
                NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
                NewSessionStartDistractionSeconds: sessionStartDistrSec,
                NewSessionStartChecked: sessionStartChecked);
        }

        // ── Pattern 14: Bad session start (one-shot) ──────────────────────────
        if (!sessionStartChecked
            && sessionActiveSec >= PortaExecutionConstants.DistractionEpisodes.SessionStartQualityWindowSeconds)
        {
            sessionStartChecked = true;
            double startRatio = sessionStartDistrSec /
                PortaExecutionConstants.DistractionEpisodes.SessionStartQualityWindowSeconds;
            if (startRatio >= PortaExecutionConstants.DistractionEpisodes.SessionStartQualityMinDistractionRatio)
            {
                return new AttentionSegmentDecision(
                    Kind: AttentionSegmentKinds.BadSessionStart, SegmentSeconds: sessionStartDistrSec,
                    DistractionEpisodeActive: true, DistractionEpisodeSeconds: sessionStartDistrSec,
                    ReasonCode: "bad_session_start",
                    ReasonText: "Session opened with high distraction activity.",
                    NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                    NewRecentDominantHashesJson: updatedHashesJson,
                    NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                    NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                    NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                    NewWindowsOffAnchorSeconds: previous.WindowsOffAnchorSeconds,
                    NewWindowsOffAnchorBucketsBack: previous.WindowsOffAnchorBucketsBack,
                    NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
                    NewCommMonitoringReturnCount: commReturnCount,
                    NewCommMonitoringWindowStartSeconds: commWindowStart,
                    NewCommMonitoringLastDominantCategory: commLastCategory,
                    NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                    NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                    NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                    NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
                    NewSessionStartDistractionSeconds: sessionStartDistrSec,
                    NewSessionStartChecked: true);
            }
        }

        // ── Pattern 8: Focus app absence (only when anchor is locked) ─────────
        double offAnchorSec = previous.WindowsOffAnchorSeconds;
        int bucketsBack = previous.WindowsOffAnchorBucketsBack;
        if (!string.IsNullOrWhiteSpace(continuity.AnchorAppHash))
        {
            bool onAnchor = string.Equals(currentDom, continuity.AnchorAppHash, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(currentDom);
            if (!onAnchor)
            {
                offAnchorSec = ClampNonNegativeFinite(offAnchorSec + Time.bucketSeconds);
                bucketsBack = 0;
            }
            else
            {
                bucketsBack++;
                if (bucketsBack >= PortaExecutionConstants.DistractionEpisodes.FocusDepartureReturnBuckets)
                    offAnchorSec = 0d;
            }
            // Don't fire if off_task_distraction is already the active kind (it fires faster at 90s)
            bool alreadyOffTask = string.Equals(continuity.ReasonCode, "context_excursion", StringComparison.Ordinal)
                || string.Equals(continuity.ReasonCode, "context_excursion_decay", StringComparison.Ordinal);
            if (!alreadyOffTask
                && offAnchorSec >= PortaExecutionConstants.DistractionEpisodes.FocusDepartureThresholdSeconds * mult)
            {
                return new AttentionSegmentDecision(
                    Kind: AttentionSegmentKinds.FocusDepartureDistraction, SegmentSeconds: offAnchorSec,
                    DistractionEpisodeActive: true, DistractionEpisodeSeconds: offAnchorSec,
                    ReasonCode: "focus_departure_distraction", ReasonText: $"Away from focus app for {FormatEpisodeDuration(offAnchorSec)}.",
                    NewEwmaSwitchRate: ewmaSwitchRate, NewMicroBurstAccumulatedSeconds: microBurstAccSec,
                    NewRecentDominantHashesJson: updatedHashesJson, NewPingPongCycleCount: pingPongCycles, NewPingPongWindowStartSeconds: pingPongWindowStart,
                    NewSessionActiveInteractionSeconds: sessionActiveSec, NewUnfocusedSessionFired: unfocusedFired,
                    NewEwmaAverageDwellSeconds: ewmaDwell, NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
                    NewWindowsOffAnchorSeconds: offAnchorSec, NewWindowsOffAnchorBucketsBack: bucketsBack,
                    NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
                    NewCommMonitoringReturnCount: commReturnCount,
                    NewCommMonitoringWindowStartSeconds: commWindowStart,
                    NewCommMonitoringLastDominantCategory: commLastCategory,
                    NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
                    NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
                    NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
                    NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
                    NewSessionStartDistractionSeconds: sessionStartDistrSec,
                    NewSessionStartChecked: sessionStartChecked);
            }
        }

        // ── No new pattern fired — steady focus ───────────────────────────────
        return new AttentionSegmentDecision(
            Kind: AttentionSegmentKinds.Focus, SegmentSeconds: 0d, DistractionEpisodeActive: false, DistractionEpisodeSeconds: 0d,
            ReasonCode: ResolveSteadyFocusReasonCode(continuity.ReasonCode),
            ReasonText: ResolveSteadyFocusReasonText(continuity.ReasonCode),
            NewEwmaSwitchRate: ewmaSwitchRate,
            NewMicroBurstAccumulatedSeconds: microBurstAccSec,
            NewRecentDominantHashesJson: updatedHashesJson,
            NewPingPongCycleCount: pingPongCycles,
            NewPingPongWindowStartSeconds: pingPongWindowStart,
            NewSessionActiveInteractionSeconds: sessionActiveSec,
            NewUnfocusedSessionFired: unfocusedFired,
            NewEwmaAverageDwellSeconds: ewmaDwell,
            NewShallowDwellAccumulatedSeconds: shallowDwellAcc,
            NewWindowsOffAnchorSeconds: offAnchorSec,
            NewWindowsOffAnchorBucketsBack: bucketsBack,
            NewPassiveConsumptionAccumulatedSeconds: passiveConsumptionAcc,
            NewCommMonitoringReturnCount: commReturnCount,
            NewCommMonitoringWindowStartSeconds: commWindowStart,
            NewCommMonitoringLastDominantCategory: commLastCategory,
            NewSocialMediaAccumulatedSeconds: socialMediaAcc, NewGamingLauncherAccumulatedSeconds: gamingLauncherAcc,
            NewGamingActiveAccumulatedSeconds: gamingActiveAcc, NewSocialMediaReturnCount: socialMediaReturnCount,
            NewSocialMediaWindowStartSeconds: socialMediaWindowStart, NewSocialMediaLastDominantCategory: socialMediaLastCategory,
            NewFocusCategoryAbsentSeconds: focusCategoryAbsSec,
            NewSessionStartDistractionSeconds: sessionStartDistrSec,
            NewSessionStartChecked: sessionStartChecked);
    }

    private static List<(string Hash, double T)> ParseRingBuffer(string json)
    {
        var result = new List<(string, double)>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
                string? hash = entry[0].GetString();
                if (!string.IsNullOrWhiteSpace(hash) && entry[1].TryGetDouble(out double t))
                    result.Add((hash, t));
            }
        }
        catch { /* return what we have */ }
        return result;
    }

    private static string SerializeRingBuffer(List<(string Hash, double T)> ring)
    {
        if (ring.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder("[");
        for (int i = 0; i < ring.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"[\"{ring[i].Hash}\",{ring[i].T.ToString(CultureInfo.InvariantCulture)}]");
        }
        sb.Append(']');
        return sb.ToString();
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
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0, false, 0d, 0, 0d, 0,
                0d, 0d, 0d, 0, 0d, 0, 0d, 0d, false);
        }

        string json = Convert.ToString(scalar, CultureInfo.InvariantCulture) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0, false, 0d, 0, 0d, 0,
                0d, 0d, 0d, 0, 0d, 0, 0d, 0d, false);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0, false, 0d, 0, 0d, 0,
                0d, 0d, 0d, 0, 0d, 0, 0d, 0d, false);
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
            bool unfocusedSessionFired = ReadBool(root, "unfocused_session_fired");
            double passiveConsumptionAccumulatedSeconds = ReadNonNegativeDouble(root, "passive_consumption_accumulated_seconds");
            int commMonitoringReturnCount = (int)ReadNonNegativeInt64(root, "comm_monitoring_return_count");
            double commMonitoringWindowStartSeconds = ReadNonNegativeDouble(root, "comm_monitoring_window_start_seconds");
            int commMonitoringLastDominantCategory = (int)ReadNonNegativeInt64(root, "comm_monitoring_last_dominant_category");
            double socialMediaAccumulatedSeconds = ReadNonNegativeDouble(root, "social_media_accumulated_seconds");
            double gamingLauncherAccumulatedSeconds = ReadNonNegativeDouble(root, "gaming_launcher_accumulated_seconds");
            double gamingActiveAccumulatedSeconds = ReadNonNegativeDouble(root, "gaming_active_accumulated_seconds");
            int socialMediaReturnCount = (int)ReadNonNegativeInt64(root, "social_media_return_count");
            double socialMediaWindowStartSeconds = ReadNonNegativeDouble(root, "social_media_window_start_seconds");
            int socialMediaLastDominantCategory = (int)ReadNonNegativeInt64(root, "social_media_last_dominant_category");
            double focusCategoryAbsentSeconds = ReadNonNegativeDouble(root, "focus_category_absent_seconds");
            double sessionStartDistractionSeconds = ReadNonNegativeDouble(root, "session_start_distraction_seconds");
            bool sessionStartChecked = ReadBool(root, "session_start_checked");
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
                WindowsOffAnchorBucketsBack: windowsOffAnchorBucketsBack,
                UnfocusedSessionFired: unfocusedSessionFired,
                PassiveConsumptionAccumulatedSeconds: passiveConsumptionAccumulatedSeconds,
                CommMonitoringReturnCount: commMonitoringReturnCount,
                CommMonitoringWindowStartSeconds: commMonitoringWindowStartSeconds,
                CommMonitoringLastDominantCategory: commMonitoringLastDominantCategory,
                SocialMediaAccumulatedSeconds: socialMediaAccumulatedSeconds,
                GamingLauncherAccumulatedSeconds: gamingLauncherAccumulatedSeconds,
                GamingActiveAccumulatedSeconds: gamingActiveAccumulatedSeconds,
                SocialMediaReturnCount: socialMediaReturnCount,
                SocialMediaWindowStartSeconds: socialMediaWindowStartSeconds,
                SocialMediaLastDominantCategory: socialMediaLastDominantCategory,
                FocusCategoryAbsentSeconds: focusCategoryAbsentSeconds,
                SessionStartDistractionSeconds: sessionStartDistractionSeconds,
                SessionStartChecked: sessionStartChecked);
        }
        catch
        {
            return new DerivedSessionRuntimeState(0d, 0d, string.Empty, 0, string.Empty, 0d, 0d, "reset", 0d, 0d, 0d,
                0d, 0d, string.Empty, 0, 0d, string.Empty, 0d, 0d, 0d, 0d, 0d, 0d, 0, false, 0d, 0, 0d, 0,
                0d, 0d, 0d, 0, 0d, 0, 0d, 0d, false);
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

    private static bool ReadBool(JsonElement root, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement value))
                continue;
            if (value.ValueKind == JsonValueKind.True)  return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)) return n != 0;
        }
        return false;
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
        int    WindowsOffAnchorBucketsBack,
        // Pattern 4 suppression flag — prevents re-firing every bucket after unfocused fires once
        bool   UnfocusedSessionFired,
        // Phase 2 — category-based pattern state
        double PassiveConsumptionAccumulatedSeconds,
        int    CommMonitoringReturnCount,
        double CommMonitoringWindowStartSeconds,
        int    CommMonitoringLastDominantCategory,
        // Phase 3 — unconditional + session-level pattern state
        double SocialMediaAccumulatedSeconds,
        double GamingLauncherAccumulatedSeconds,
        double GamingActiveAccumulatedSeconds,
        int    SocialMediaReturnCount,
        double SocialMediaWindowStartSeconds,
        int    SocialMediaLastDominantCategory,
        double FocusCategoryAbsentSeconds,
        double SessionStartDistractionSeconds,
        bool   SessionStartChecked);

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
        string ReasonCode,
        // Block D — anchor state carried through all decision paths (default = cleared)
        string CandidateAnchorAppHash = "",
        double CandidateAnchorAccumulatedSeconds = 0d,
        double AnchorLockedSeconds = 0d);

    private readonly record struct AttentionSegmentDecision(
        string Kind,
        double SegmentSeconds,
        bool   DistractionEpisodeActive,
        double DistractionEpisodeSeconds,
        string ReasonCode,
        string ReasonText,
        // Block E — updated pattern state carried out of EvaluateAttentionSegment
        double NewEwmaSwitchRate = 0d,
        double NewMicroBurstAccumulatedSeconds = 0d,
        string NewRecentDominantHashesJson = "",
        int    NewPingPongCycleCount = 0,
        double NewPingPongWindowStartSeconds = 0d,
        double NewSessionActiveInteractionSeconds = 0d,
        bool   NewUnfocusedSessionFired = false,
        double NewEwmaAverageDwellSeconds = 0d,
        double NewShallowDwellAccumulatedSeconds = 0d,
        double NewWindowsOffAnchorSeconds = 0d,
        int    NewWindowsOffAnchorBucketsBack = 0,
        double NewPassiveConsumptionAccumulatedSeconds = 0d,
        int    NewCommMonitoringReturnCount = 0,
        double NewCommMonitoringWindowStartSeconds = 0d,
        int    NewCommMonitoringLastDominantCategory = 0,
        // Phase 3 — unconditional + session-level state
        double NewSocialMediaAccumulatedSeconds = 0d,
        double NewGamingLauncherAccumulatedSeconds = 0d,
        double NewGamingActiveAccumulatedSeconds = 0d,
        int    NewSocialMediaReturnCount = 0,
        double NewSocialMediaWindowStartSeconds = 0d,
        int    NewSocialMediaLastDominantCategory = 0,
        double NewFocusCategoryAbsentSeconds = 0d,
        double NewSessionStartDistractionSeconds = 0d,
        bool   NewSessionStartChecked = false);

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
