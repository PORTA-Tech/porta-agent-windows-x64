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
    string DeferredUntilDay = "",
    // Step 9a (2026-08-12) — the project this task belongs to. 0 = untagged, which is a permanent
    // valid state and never nagged about. ProjectName is denormalised on the read so the widget,
    // which repaints every 4s, never needs a second call to draw a row.
    long ProjectId = 0,
    string ProjectName = "",
    // Step 10 phase A (2026-08-13) — what a paying client is shown for this line, given once by the
    // user and then remembered forever. Empty = never given. The raw Title above stays local and is
    // never replaced by it: the label is for the report, the title is for the person doing the work
    // (Step10_Report_Spec.md §3).
    string ClientLabel = "");

/// <summary>
/// Step 9a — a project. On a card a project is ONLY a name; client, colour and rate belong in
/// Control Center, because the morning card must not become a form.
///
/// Archived rather than deleted: a project's tasks keep pointing at it, so deleting one would
/// orphan measured time that may already have been reported. Archiving hides it from the picker
/// and leaves the history intact.
/// </summary>
/// <summary>
/// 2026-08-12 — one hour of one application's time, already resolved to its project. The grain the
/// projects page reads: hourly rather than daily, because detail you did not capture is gone
/// forever and detail you have can always be ignored (Yadin, 2026-08-12).
///
/// Backfilled = reconstructed from input_signal_windows rather than measured live. Those rows
/// carry ActiveSeconds ONLY — the focused/off-task split and the task were not recoverable after
/// the fact, so a zero in those fields is "not known", never "measured as zero". Anything that
/// renders them must say so or omit them.
/// </summary>
public sealed record AgentAppUsageHour(
    DateTimeOffset HourStartUtc,
    string LocalDate,
    int HourLocal,
    long ProjectId,
    string ProjectName,
    long TaskId,
    string AppHash,
    double FocusedSeconds,
    double OfftaskSeconds,
    double ActiveSeconds,
    bool Backfilled);

/// <summary>
/// Step 10 phase B (2026-08-13) — a report as it stands before anyone has looked at it. DERIVED,
/// never stored: the stored thing is the snapshot taken at the moment of sending, and re-deriving
/// this tomorrow may legitimately give a different answer because re-tagging is allowed at any time.
///
/// <b>Every number here comes from <c>app_usage_rollup</c></b>, resolved to a project at read time
/// through <c>tasks.project_id</c>. That is not a preference: the rollup is the only record with a
/// per-day breakdown, and a report whose daily bars did not sum to its own total is the exact defect
/// this product exists to avoid. Taking the line totals from one source and the bars from another
/// would guarantee it.
///
/// <b>It can therefore read LOWER than the widget for the same task</b>, and never higher: a bucket
/// with no identifiable application gets no rollup row (<c>AccumulateAppUsageRollup</c>) while the
/// task itself is still credited. The difference per line is carried as
/// <see cref="AgentReportLine.UnattributedHours"/> — <b>shown to the user, never to the client</b> —
/// so a report that disagrees with the widget explains itself on screen instead of being discovered
/// by a client. Under-counting is the safe direction and is what the honesty line already claims.
/// </summary>
public sealed record AgentReportDraft(
    long ProjectId,
    // The local name, for the user's own screen. It never leaves the machine.
    string ProjectName,
    // What a client would see in the header. Empty = never given, and the report cannot be sent
    // until it is: the alternative is falling back to a name typed on a morning card.
    string ClientName,
    string RangeStartLocal,
    string RangeEndLocal,
    // The sum of the ROUNDED line hours, so the arithmetic on the page checks out by hand. A total
    // computed from raw seconds can differ from its own visible lines by a tenth, and "your figures
    // do not add up" is a conversation this document exists to prevent.
    double TotalHours,
    int DaysWorked,
    int LinesFinished,
    IReadOnlyList<AgentReportLine> Lines,
    IReadOnlyList<AgentReportDay> Days);

/// <summary>One line of the report: one task, its client-facing label, and its measured hours.</summary>
public sealed record AgentReportLine(
    long TaskId,
    // The raw title, for the user's screen only, so they can tell which line is which. Never sent.
    string Title,
    // What the client reads. Empty = not named yet, and the report cannot be sent until it is.
    string ClientLabel,
    bool Finished,
    double Hours,
    // USER-SIDE DIAGNOSTIC ONLY, and never rendered into anything a client sees: measured time on
    // this task that carried no identifiable application, so it is absent from the hours above.
    //
    // Read <see cref="UnattributedMeasurable"/> FIRST. This is a property of the task's whole life,
    // not of the report's range, because tasks.active_seconds is a running total with no time
    // dimension — there is no windowed source for it and inventing one would be inference.
    double UnattributedHours,
    // Whether the gap above means anything for THIS report. False = not knowable, and a zero
    // alongside it means "not known", never "measured as zero" — the same rule the backfilled
    // rollup rows already carry. Two things make it unknowable, and both are window mismatches:
    //
    //   1. The task has rollup activity OUTSIDE this range, so its lifetime gap cannot be
    //      attributed to the hours shown on this line. Some of it is counted, just not here.
    //   2. The task was started before the rollup existed on this machine, so its credited
    //      seconds include time the rollup could never have seen. Comparing the two would
    //      report the rollup's own start date as missing app time.
    //
    // Anything rendering this must say so or omit it. It must never print a number.
    bool UnattributedMeasurable);

/// <summary>
/// One day of the range. Days with no work are present with zero hours, never skipped — §4: a chart
/// that hides empty days is the kind of thing a client notices, and it costs trust permanently.
/// </summary>
public sealed record AgentReportDay(string DateLocal, double Hours);

/// <summary>
/// Step 10 phase C/D — one entry in the address book. LOCAL ONLY (§12.4): a list of other people's
/// names and addresses, and only the one address on the report being sent ever leaves the machine.
/// </summary>
public sealed record AgentReportRecipient(
    long Id,
    string Name,
    string Email,
    DateTimeOffset CreatedAtUtc,
    string LastUsedUtc);

/// <summary>
/// Step 10 phase C/D — the local record of a report that was sent (§5). Without it a challenge
/// cannot be answered, and it is a DEAD COPY: re-tagging or re-labelling afterwards changes the next
/// report, never a delivered one.
///
/// <c>FromName</c> and <c>ReplyTo</c> are recorded but never sent — the payload carries no sender
/// fields (§12.2), yet a dispute asks "who did this appear to come from?" and the answer has to
/// exist somewhere.
/// </summary>
public sealed record AgentSentReport(
    string ReportId,
    long ProjectId,
    string ProjectName,
    string ClientName,
    string RangeStartLocal,
    string RangeEndLocal,
    string RecipientName,
    string RecipientEmail,
    string FromName,
    string ReplyTo,
    double TotalHours,
    int DaysWorked,
    int LinesFinished,
    int LinesTotal,
    DateTimeOffset RequestedAtUtc,
    string SentAtUtc,
    /// <summary>pending | sent | failed. 'pending' is recorded-but-unconfirmed.</summary>
    string SendState,
    string SendError,
    /// <summary>present | delete_pending. A half-delete stays listed as present (§12.6.5).</summary>
    string ServerState,
    IReadOnlyList<AgentSentReportLine> Lines,
    IReadOnlyList<AgentReportDay> Days);

public sealed record AgentSentReportLine(string LineId, string ClientLabel, bool Finished, double Hours);

public sealed record AgentProject(
    long Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    bool Archived,
    int OrderIndex,
    // Step 10 phase A (2026-08-13) — the name a client sees in the report header, the largest text
    // on the page. Empty = never given, and the report must ask before it can be sent rather than
    // falling back to Name: "nolan - chase invoice" is a plausible thing to type on a morning card
    // (Step10_Report_Spec.md §12.7).
    string ClientName = "",
    // 0 = use the raw title for new tasks, 1 = ask for a client-facing label. Decides only what a
    // NEW task starts as; a label already written is never overwritten by this changing (§3).
    bool UseClientLabels = false,
    // §7 — minor units, integer, and USER-SIDE ONLY. 0 = no rate. Ari does the arithmetic and the
    // user carries the figure into whatever they already invoice with. This must never reach any
    // payload: a document with a money total starts being treated as an invoice, and that is a
    // different product.
    long RateMinor = 0,
    string RateCurrency = "");

// Digest redesign (2026-08-11) — one local hour of the day's shape. Seconds only, never a title:
// this describes WHEN work happened, not what was open. Focus + Active + Away sum to at most 3600;
// an hour that recorded nothing is ABSENT rather than zero, because "nothing recorded" (machine off)
// is a different statement from "away" (there, but idle).
public sealed record AgentDayTimelineHour(
    int HourLocal,
    double FocusSeconds,
    double ActiveSeconds,
    double AwaySeconds);

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
