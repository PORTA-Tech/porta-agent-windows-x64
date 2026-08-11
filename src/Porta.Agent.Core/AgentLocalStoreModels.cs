namespace Porta.Agent.Core;

public sealed record AgentControlEvent(
    DateTimeOffset TimestampUtc,
    string Action,
    string TriggerState,
    double TriggerValue,
    double Confidence,
    int? DurationSeconds,
    string EscalationLevel,
    bool Reversible,
    string Reason,
    IReadOnlyList<string> Contributors,
    string InterventionId = "",
    string PolicyId = "n/a",
    string Domain = "control",
    DateTimeOffset? PlannedEndTimestampUtc = null,
    DateTimeOffset? ActualEndTimestampUtc = null,
    string PreviousSystemStateJson = "{}",
    string WhatChanged = "",
    string HowToUndo = "",
    bool ReversalApplied = false,
    DateTimeOffset? CooldownEndTimestampUtc = null,
    string LifecycleState = "activated",
    string? WhyJsonOverride = null,
    long EventId = 0,
    string IdempotencyKey = "",
    string WhyJson = "{\"schema_ver\":1}",
    string StateFlagsJson = "{\"schema_ver\":1}",
    string CommandType = "",
    string ExecutionState = "decision_only",
    DateTimeOffset? DecidedAtUtc = null,
    DateTimeOffset? AppliedAtUtc = null,
    DateTimeOffset? AckedAtUtc = null,
    string ExecutionErrorCode = "",
    string ExecutorAgentInstanceId = "");

public sealed record AgentPendingIntervention(
    long RowId,
    string InterventionId,
    string SessionId,
    string Action,
    string CommandType,
    string PayloadJson,
    DateTimeOffset DecidedAtUtc,
    string ExecutionState,
    string PolicyId,
    string Domain,
    double Confidence,
    string EscalationLevel,
    string WhatChanged,
    string? InteractionKind = null,
    string? DeliverySurface = null,
    string? TipKey = null,
    string? BreakKind = null,
    int? CountdownSeconds = null);

public sealed record AgentInterventionAck(
    string InterventionId,
    string SessionId,
    string AgentInstanceId,
    string Result,
    string? ErrorCode,
    DateTimeOffset AppliedAtUtc,
    string? InteractionKind = null,
    string? DeliverySurface = null,
    string? BreakKind = null);

// Phase 1b: a single durable ground-truth label — a self-contained training example
// (full feature snapshot + the answer). Stored in the prune-exempt ground_truth_labels table.
public sealed record GroundTruthLabel(
    DateTimeOffset CreatedAtUtc,
    long BucketSeq,
    string BucketTimestampUtc,
    string LabelSource,     // break_nudge | recovery_nudge | focus_check | overload_check | distraction_check
    string PromptKind,      // what was asked, e.g. accept_break | focused_now | on_task_now | overload_right
    string ResponseKind,    // raw answer, e.g. accepted | dismissed | skipped | snoozed | focused | not_focused | on_task | off_task
    string LabelTarget,     // what it teaches: break_need | focus_state | distraction | overload
    int LabelValue,         // normalized: +1 positive / -1 negative / 0 neutral
    int ResponseLatencyMs,
    string InterventionId,
    string FeaturesJson,
    double PressureLevel,
    double OverloadLikelihood,
    double Confidence,
    string DominantState,
    string CalibrationStage,
    string TrustTier,
    int SchemaVer = 1);

// Phase 1b: lightweight readout so we can confirm labels are actually accumulating.
// The Prompts* fields (from prompt_events) let the operator see the RESPONSE RATE — how many prompts
// were shown vs actually answered — which is the real "are people engaging?" signal. Counts only.
public sealed record AgentLabelDiagnostics(
    long TotalLabels,
    long LabelsLast24h,
    long LabelsLast7d,
    IReadOnlyList<AgentLabelTargetCount> Last24hByTarget,
    long PromptsShownLast24h = 0,
    long PromptsAnsweredLast24h = 0,
    long PromptsShownLast7d = 0,
    long PromptsAnsweredLast7d = 0);

// Per-target rollup for the last 24 hours. NetValue = SUM(label_value): positive means Ari's guesses
// were mostly confirmed right, negative mostly wrong. Counts only — never label content.
public sealed record AgentLabelTargetCount(
    string Target,
    long Count,
    long NetValue);

// Phase 1b — Step 3: recent cadence of the ACTIVE one-tap prompts (focus/distraction/overload
// checks only), used by the governor's daily-cap and min-interval gates.
public sealed record AgentLabelPromptCadence(
    int ActivePromptsLast24h,
    DateTimeOffset? LastActivePromptAtUtc,
    int UnansweredPromptsLast24h = 0);

// Phase 1b — Step 3b: public projection of the governor's decision, returned by the prompt-due endpoint.
public sealed record AgentLabelPromptDecision(
    bool ShouldPrompt,
    string LabelTarget,
    string PromptKind,
    string ReasonCode);

// Phase 1c — signup nudge cadence: how many days of local data exist, and the nudge caps state.
public sealed record AgentSignupNudgeContext(
    int DaysOfData,
    int NudgesShownTotal,
    DateTimeOffset? LastNudgeAtUtc);

// Phase 1c — public projection of the signup-nudge governor's decision, returned by the signup-due endpoint.
public sealed record AgentSignupNudgeDecision(
    bool ShouldNudge,
    string ReasonCode);

// Phase 1b — Step 3b: a widget one-tap answer to record as a label.
public sealed record AgentFocusLabelRequest(
    string LabelTarget,   // focus_state | distraction | overload
    string PromptKind,    // focused_now | on_task_now | overload_now
    string Answer,        // focused | not_focused | working | break | overloaded | fine ...
    int ResponseLatencyMs);

public sealed record AgentDiagnosticsSnapshot(
    DateTimeOffset TimestampUtc,
    bool BackendConnected,
    bool ConsentActive,
    bool Paused,
    string? LastError,
    string PolicyVersion,
    string SchemaVersion,
    string AgentVersion,
    DateTimeOffset? LastEventTimestampUtc,
    DateTimeOffset? LastBackendOkUtc);

public sealed record AgentDistractionSummary(
    double SocialMediaAccumulatedSeconds,
    double GamingLauncherAccumulatedSeconds,
    double GamingActiveAccumulatedSeconds,
    double PassiveConsumptionAccumulatedSeconds,
    int CommMonitoringReturnCount,
    int SocialMediaReturnCount);

public sealed record AgentTodaySummary(
    DateOnly DateUtc,
    int TotalInterventions,
    double AverageFocusStrength,
    double PeakOverloadLikelihood,
    double IdleFragmentationIndex,
    int? MostVolatileHourUtc);

public sealed record AgentStorageInfo(
    int RetentionDays,
    long DatabaseBytes);

public sealed record AgentInputSummary(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int WindowSeconds,
    int SampleCount,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double IdleFragmentationIndex,
    double NotificationLatencyIndex);

public sealed record AgentInputSignalWindow(
    DateTimeOffset AcceptedAtUtc,
    int SchemaVer,
    string AgentInstanceId,
    string SessionId,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int KeyboardKeydownCount,
    int KeyboardBackspaceCount,
    int MouseWheelDelta,
    double MouseMoveDx,
    double MouseMoveDy,
    string? FocusAppHash,
    double IdleSeconds,
    DateTimeOffset? LastInputEventUtc,
    double? KeyboardIkiMeanMs = null,
    double? KeyboardIkiStdMs = null,
    double? KeyboardIkiCv = null,
    int KeyboardBurstCount = 0,
    double? KeyboardBurstRate = null,
    int KeyboardCorrectionSpikeCount = 0,
    double? KeyboardCorrectionSpikeRate = null,
    int MouseMoveSampleCount = 0,
    double? MousePathLengthPx = null,
    double? MouseSpeedMeanPxPerSec = null,
    double? MouseJerkP95 = null,
    int MouseMicroCorrectionCount = 0,
    double? MouseMicroCorrectionRate = null,
    int MouseWheelEventCount = 0,
    int MouseScrollBurstCount = 0,
    double? MouseScrollBurstRate = null,
    double? MouseScrollSpeedMean = null,
    int FocusObservationCount = 0,
    int FocusSwitchCount = 0,
    int FocusActiveAppCount = 0,
    double? FocusAppEntropy = null,
    int IdleObservationCount = 0,
    double? IdleSecondsWithinWindow = null,
    int MicroIdleCount = 0,
    double? MicroIdleSeconds = null,
    double? BreakSecondsWithinWindow = null,
    bool ShortIdleBoundaryObserved = false,
    bool? NotificationToastsEnabled = null);

public sealed record AgentRecordedSignalSample(
    string SignalKind,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset TimestampUtc,
    string? AgentInstanceId = null,
    string? SessionId = null,
    int? SchemaVer = null,
    double? TypingBurstIntensity = null,
    double? ScrollVelocityIndex = null,
    double? FocusSwitchRate = null,
    int? KeyboardKeydownCount = null,
    int? KeyboardBackspaceCount = null,
    double? KeyboardBackspaceRate = null,
    double? KeyboardIkiMeanMs = null,
    double? KeyboardIkiStdMs = null,
    double? KeyboardIkiCv = null,
    int? KeyboardBurstCount = null,
    double? KeyboardBurstRate = null,
    int? KeyboardCorrectionSpikeCount = null,
    double? KeyboardCorrectionSpikeRate = null,
    int? MouseWheelDelta = null,
    int? MouseWheelEventCount = null,
    double? MouseMoveDx = null,
    double? MouseMoveDy = null,
    int? MouseMoveSampleCount = null,
    double? MousePathLengthPx = null,
    double? MouseSpeedMeanPxPerSec = null,
    double? MouseJerkP95 = null,
    int? MouseMicroCorrectionCount = null,
    double? MouseMicroCorrectionRate = null,
    int? MouseScrollBurstCount = null,
    double? MouseScrollBurstRate = null,
    double? MouseScrollSpeedMean = null,
    int? FocusObservationCount = null,
    int? FocusSwitchCount = null,
    double? FocusActiveAppCount = null,
    double? FocusAppEntropy = null,
    string? FocusAppHash = null,
    double? IdleSeconds = null,
    int? IdleObservationCount = null,
    double? IdleSecondsWithinWindow = null,
    double? MicroIdleCount = null,
    double? MicroIdleSeconds = null,
    double? TrueBreakSeconds = null,
    bool? TrueBreakFlag = null,
    bool? ShortIdleBoundaryObserved = null,
    bool? NotificationToastsEnabled = null,
    double? QualityScore = null,
    bool? Partial = null,
    string? ContributorsJson = null);

public sealed record AgentLatestStateDetails(
    DateTimeOffset TimestampUtc,
    double OverloadLikelihood,
    double Confidence,
    double SustainedAttentionDurationSec,
    double IdleFragmentationIndex,
    double FocusAppEntropy,
    bool OverloadActive,
    string StateFlagsJson,
    string ContributorsJson,
    bool? BaselineReady = null,
    double? BaselineReadyPct = null,
    double? AvgQuality2m = null,
    bool? ColdStart = null,
    string? CalibrationStage = null,
    bool? SystemActionsEnabled = null,
    string? TrustTier = null,
    double? CurrentQualityScore = null,
    bool? PartialData = null,
    IReadOnlyList<string>? MissingMajorChannels = null,
    string? BreakPendingAction = null,
    string? BreakPendingKind = null,
    string? BreakPendingReasonCode = null,
    double? BreakPendingEffectiveFocusSeconds = null,
    int? BreakPendingEffectiveFocusWindowSeconds = null,
    bool? BreakDeliveryBoundaryReached = null,
    string? BreakDeliveryBoundaryKind = null,
    double? BreakShortBreakEffectiveFocusSeconds = null,
    double? BreakLongBreakEffectiveFocusSeconds = null,
    double? TaskContinuityScore = null,
    double? TaskContinuitySeconds = null,
    string? ContinuityState = null);

public sealed record AgentSettingsState(
    string RegulationIntensity,
    string PrivacyLevel,
    bool NotificationReductionEnabled,
    bool FocusModeEnabled,
    bool AnimationReductionEnabled,
    bool TemporaryAppRestrictionEnabled,
    bool BaselineRecalibrationActive,
    DateTimeOffset? BaselineRecalibrationStartedUtc,
    DateTimeOffset? BaselineRecalibrationUntilUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentSettingsUpdate(
    string? RegulationIntensity,
    string? PrivacyLevel,
    bool? NotificationReductionEnabled,
    bool? FocusModeEnabled,
    bool? AnimationReductionEnabled,
    bool? TemporaryAppRestrictionEnabled);

public sealed record AgentDataRetentionInfo(
    int RetentionDays,
    long DatabaseBytes,
    long LogBytes,
    long ConsentBytes,
    long TotalBytes,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentTimelineData(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketSeconds,
    IReadOnlyList<AgentBrainSnapshot> Snapshots,
    IReadOnlyList<AgentControlEvent> Interventions);

public sealed record AgentRecalibrationResult(
    bool Active,
    DateTimeOffset StartedUtc,
    DateTimeOffset UntilUtc);

public sealed record AgentSystemChange(
    DateTimeOffset TimestampUtc,
    string ChangeType,
    string PreviousValueJson,
    string NewValueJson,
    string UndoInfo,
    string Reason);

public sealed record AgentConsentRecord(
    DateTimeOffset TimestampUtc,
    bool Active,
    string Version,
    string DisclosureHash);

public sealed record AgentSettingsRecord(
    DateTimeOffset TimestampUtc,
    string PrivacyLevel,
    string RegulationIntensity,
    IReadOnlyList<string> DisabledCategories,
    bool NotificationReductionEnabled,
    bool FocusModeEnabled,
    bool AnimationReductionEnabled,
    bool TemporaryAppRestrictionEnabled,
    DateTimeOffset? BaselineRecalibrationStartedUtc,
    DateTimeOffset? BaselineRecalibrationUntilUtc);

// Phase 2c — a user reminder. state: pending | fired (due, awaiting ack) | done | dismissed.
public sealed record AgentReminder(
    long Id,
    string Title,
    DateTimeOffset DueAtUtc,
    DateTimeOffset CreatedAtUtc,
    string State,
    int SnoozeCount,
    DateTimeOffset? FiredAtUtc);

// Workstream 4 — a timed task: the user's plan (title + planned duration) held against
// measured reality. state: pending | active (at most one) | done | abandoned.
// ActiveSeconds = attended time while active (away contributes nothing); Focused/Offtask
// split it using the bucket pipeline's own attention classification — no new inference.
public sealed record AgentTask(
    long Id,
    string Title,
    int PlannedSeconds,
    string State,
    int OrderIndex,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    double ActiveSeconds,
    double FocusedSeconds,
    double OfftaskSeconds,
    int ExtendedSeconds,
    // Complaint 3 (2026-08-11) — the goal-day ("yyyy-MM-dd") this task is next due back, set by the
    // close-out card's "Tomorrow". Empty = not deferred. Defaulted so every existing construction
    // and every already-stored row keeps working untouched.
    string DeferredUntilDay = "");

// Workstream D — the day's conversational check-in state. One row per local "goal day"
// (LocalDate = the 5:00-boundary day, "yyyy-MM-dd"). GoalText/TrackingPref set by the morning
// check-in; MiddayAnswer (yes|partly|not_yet) by midday; PlanTomorrow/EndOfDayAtUtc by EOD.
public sealed record AgentDailyGoal(
    long Id,
    string LocalDate,
    string GoalText,
    string TrackingPref,
    string MiddayAnswer,
    string PlanTomorrow,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? MiddayAtUtc,
    DateTimeOffset? EndOfDayAtUtc);
