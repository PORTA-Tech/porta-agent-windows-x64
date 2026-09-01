using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Porta.Agent.Service.Fleet;

internal sealed record MachineSummaryReportPayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("policyVersion")] string PolicyVersion,
    [property: JsonPropertyName("reportedAtUtc")] string ReportedAtUtc,
    [property: JsonPropertyName("agentRunning")] bool AgentRunning,
    [property: JsonPropertyName("userSessionConnected")] bool UserSessionConnected,
    [property: JsonPropertyName("activityDetected")] bool ActivityDetected,
    [property: JsonPropertyName("lastActivityUtc")] string? LastActivityUtc,
    [property: JsonPropertyName("userAgentHeartbeatUtc")] string? UserAgentHeartbeatUtc,
    [property: JsonPropertyName("userAgentHeartbeatAgeSeconds")] double? UserAgentHeartbeatAgeSeconds,
    [property: JsonPropertyName("lastAcceptedInputAgeSeconds")] double? LastAcceptedInputAgeSeconds,
    [property: JsonPropertyName("userAgentStartupSource")] string? UserAgentStartupSource,
    [property: JsonPropertyName("userAgentStartupState")] string? UserAgentStartupState,
    [property: JsonPropertyName("userAgentLastLaunchReason")] string? UserAgentLastLaunchReason,
    [property: JsonPropertyName("userAgentHeartbeatWriteFailureCount")] int? UserAgentHeartbeatWriteFailureCount,
    [property: JsonPropertyName("captureContext")] string CaptureContext,
    [property: JsonPropertyName("keyboardSignalActive")] bool KeyboardSignalActive,
    [property: JsonPropertyName("mouseSignalActive")] bool MouseSignalActive,
    [property: JsonPropertyName("ownershipWarning")] string? OwnershipWarning,
    [property: JsonPropertyName("captureFailureReason")] string? CaptureFailureReason,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("backendConnected")] bool BackendConnected,
    [property: JsonPropertyName("localApiAuthReady")] bool LocalApiAuthReady,
    [property: JsonPropertyName("dbMigrationFailed")] bool DbMigrationFailed,
    [property: JsonPropertyName("focusEpisodeQuality")] double FocusEpisodeQuality,
    [property: JsonPropertyName("focusEpisodeSeconds")] double FocusEpisodeSeconds,
    [property: JsonPropertyName("focusEpisodeState")] string FocusEpisodeState,
    [property: JsonPropertyName("focusEpisodeReasonCode")] string? FocusEpisodeReasonCode,
    [property: JsonPropertyName("focusEpisodeReasonText")] string? FocusEpisodeReasonText,
    [property: JsonPropertyName("attentionSegmentKind")] string AttentionSegmentKind,
    [property: JsonPropertyName("attentionSegmentSeconds")] double AttentionSegmentSeconds,
    [property: JsonPropertyName("attentionSegmentReasonCode")] string? AttentionSegmentReasonCode,
    [property: JsonPropertyName("attentionSegmentReasonText")] string? AttentionSegmentReasonText,
    [property: JsonPropertyName("distractionEpisodeActive")] bool DistractionEpisodeActive,
    [property: JsonPropertyName("distractionEpisodeSeconds")] double DistractionEpisodeSeconds,
    [property: JsonPropertyName("taskContinuityScore")] double TaskContinuityScore,
    [property: JsonPropertyName("taskContinuitySeconds")] double TaskContinuitySeconds,
    [property: JsonPropertyName("continuityState")] string ContinuityState,
    [property: JsonPropertyName("deepWorkActive")] bool DeepWorkActive,
    [property: JsonPropertyName("pressureLevel")] double PressureLevel,
    [property: JsonPropertyName("pressureConfidence")] double PressureConfidence,
    [property: JsonPropertyName("overloadLikelihood")] double OverloadLikelihood,
    [property: JsonPropertyName("overloadActive")] bool OverloadActive,
    [property: JsonPropertyName("breakNeeded")] bool BreakNeeded,
    [property: JsonPropertyName("breakPendingAction")] string? BreakPendingAction,
    [property: JsonPropertyName("breakPendingKind")] string? BreakPendingKind,
    [property: JsonPropertyName("breakPendingReasonCode")] string? BreakPendingReasonCode,
    [property: JsonPropertyName("calibrationStage")] string CalibrationStage,
    [property: JsonPropertyName("trustTier")] string TrustTier,
    [property: JsonPropertyName("modelConfidence")] double ModelConfidence,
    [property: JsonPropertyName("signalQualityScore")] double? SignalQualityScore,
    [property: JsonPropertyName("partialData")] bool PartialData,
    [property: JsonPropertyName("missingMajorChannels")] IReadOnlyList<string> MissingMajorChannels,
    [property: JsonPropertyName("userAgentCaptureFailureCode")] string? UserAgentCaptureFailureCode = null,
    [property: JsonPropertyName("watchdogLaunchFailureCode")] string? WatchdogLaunchFailureCode = null,
    [property: JsonPropertyName("userAgentSelfTestState")] string? UserAgentSelfTestState = null,
    [property: JsonPropertyName("userAgentSelfTestErrorCode")] string? UserAgentSelfTestErrorCode = null,
    [property: JsonPropertyName("userAgentLogTail")] string? UserAgentLogTail = null,
    [property: JsonPropertyName("windowsUpdateStatus")] string? WindowsUpdateStatus = null,
    [property: JsonPropertyName("userAgentSessionConnectState")] string? UserAgentSessionConnectState = null,
    [property: JsonPropertyName("focusAppIdentity")] string? FocusAppIdentity = null,
    [property: JsonPropertyName("focusAppLabel")] string? FocusAppLabel = null);

internal sealed record WindowsReleaseManifestPayload(
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("msiUrl")] string? MsiUrl,
    [property: JsonPropertyName("publishedAtUtc")] string? PublishedAtUtc,
    [property: JsonPropertyName("minimumSupportedVersion")] string? MinimumSupportedVersion,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("summary")] string? Summary = null,
    [property: JsonPropertyName("sha256")] string? Sha256 = null,
    [property: JsonPropertyName("requireSignature")] bool RequireSignature = false,
    [property: JsonPropertyName("rolloutPercent")] int RolloutPercent = 100);

internal sealed record DeviceRegistrationRequestPayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("deviceDisplayName")] string? DeviceDisplayName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("deviceCaptureConsentState")] string? DeviceCaptureConsentState,
    [property: JsonPropertyName("deviceCaptureConsentVersion")] string? DeviceCaptureConsentVersion,
    [property: JsonPropertyName("deviceCaptureConsentedAt")] string? DeviceCaptureConsentedAt,
    [property: JsonPropertyName("deviceCaptureTelemetryLevel")] string? DeviceCaptureTelemetryLevel,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken,
    /// <summary>
    /// Tells the control plane this build stores a credential handed back during registration.
    /// Without it the control plane must not issue one: the secret is returned exactly once, so
    /// giving it to a build that ignores the field burns the device's issuance and strands it.
    /// Constant true from 1.2.124 -- the version gate is the fallback, this is the explicit signal.
    /// </summary>
    [property: JsonPropertyName("supportsCredentialStorage")] bool SupportsCredentialStorage = true);

internal sealed record DeviceAccountLinkRequestPayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record DeviceAccountUnlinkRequestPayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record DeviceProfilePayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("deviceDisplayName")] string DeviceDisplayName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("firstSeenAt")] string FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] string LastSeenAt,
    [property: JsonPropertyName("currentAccountId")] string? CurrentAccountId,
    [property: JsonPropertyName("revokedAt")] string? RevokedAt,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt);

internal sealed record DeviceLinkResultPayload(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("linkState")] string LinkState,
    [property: JsonPropertyName("device")] DeviceProfilePayload Device,
    /// <summary>
    /// Present only on the one registration where the control plane bootstraps a credential for
    /// an already-linked device that had none. Returned once and never again, so it must be
    /// stored on receipt -- there is no way to ask for it a second time.
    /// </summary>
    [property: JsonPropertyName("deviceSecret")] string? DeviceSecret = null);

/// <summary>One app/site's foreground time for the day — coarse label + seconds. Powers the
/// "where your time went" section of the digest. Label is a coarse token only (e.g. "youtube").</summary>
internal sealed record FocusAppUsagePayload(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("seconds")] double Seconds,
    // Coarse AppCategory enum value (0=Unknown) so distraction can be attributed to specific
    // apps/sites (entertainment/social/gaming = distracting) without re-deriving categories server-side.
    [property: JsonPropertyName("category")] int Category = 0);

// Part 5 — live narrative line request (POST api/agent/narrative). Situation carries the same
// metadata classes the digest already uploads: goal/plan text, numbers, coarse app labels — no
// content beyond what the user typed into the check-in card itself.
internal sealed record NarrativeSituationPayload(
    [property: JsonPropertyName("goalToday")] string? GoalToday,
    [property: JsonPropertyName("carriedPlan")] string? CarriedPlan,
    [property: JsonPropertyName("middayAnswer")] string? MiddayAnswer,
    [property: JsonPropertyName("trackedSeconds")] double TrackedSeconds,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("focusQualityPct")] double FocusQualityPct,
    [property: JsonPropertyName("distractionSeconds")] double DistractionSeconds,
    [property: JsonPropertyName("awaySeconds")] double AwaySeconds,
    [property: JsonPropertyName("longestStreakSeconds")] double LongestStreakSeconds,
    [property: JsonPropertyName("topApps")] IReadOnlyList<FocusAppUsagePayload>? TopApps,
    [property: JsonPropertyName("localTimeHHmm")] string? LocalTimeHHmm,
    [property: JsonPropertyName("userText")] string? UserText,
    // Part 1b — the EOD wrap's whole-day facts: midday was skipped (morning goal set, no midday
    // answer) and there was a substantial away gap. Default false so non-EOD moments are unaffected.
    [property: JsonPropertyName("middaySkipped")] bool MiddaySkipped = false,
    [property: JsonPropertyName("awayGap")] bool AwayGap = false,
    // 2026-08-10 — what actually HAPPENED, not just what was planned. GoalToday is the morning
    // sentence and is never revised, so a task the user abandoned still reads as today's plan: on
    // 2026-08-10 Ari opened by proposing "writting" back, which had been dropped the previous
    // afternoon after 20 seconds. The model could not know, because no task outcome had ever
    // reached it. Null when there is nothing to report, so the fact sheet simply omits the section.
    [property: JsonPropertyName("tasks")] IReadOnlyList<NarrativeTaskPayload>? Tasks = null);

/// <summary>
/// One task on the narrative fact sheet. Deliberately four fields: the title the user typed, what
/// became of it, what they intended to spend, and what was actually measured.
/// </summary>
/// <remarks>
/// OfftaskSeconds is carried by the digest but NOT here: the line needs to know a task was dropped,
/// not how its minutes were split. Every field on this payload has to earn the trip.
/// </remarks>
internal sealed record NarrativeTaskPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,          // done | abandoned | active | pending
    [property: JsonPropertyName("plannedSeconds")] int PlannedSeconds,
    [property: JsonPropertyName("actualSeconds")] double ActualSeconds);

internal sealed record NarrativeRequestPayload(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("dateLocal")] string DateLocal,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("situation")] NarrativeSituationPayload Situation);

internal sealed record NarrativeResponsePayload(
    [property: JsonPropertyName("line")] string? Line);

/// <summary>The narrative line, or a code describing exactly how the call failed.</summary>
internal readonly record struct NarrativeCallOutcome(string? Line, string Reason);

internal sealed record DailyDigestReportPayload(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("dateLocal")] string DateLocal,
    [property: JsonPropertyName("timezoneOffsetMinutes")] int TimezoneOffsetMinutes,
    [property: JsonPropertyName("reportedAtUtc")] string ReportedAtUtc,
    [property: JsonPropertyName("trackedSeconds")] double TrackedSeconds,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("focusQualityScore")] double FocusQualityScore,
    [property: JsonPropertyName("distractionSeconds")] double DistractionSeconds,
    [property: JsonPropertyName("awaySeconds")] double AwaySeconds,
    [property: JsonPropertyName("contextExcursionSeconds")] double ContextExcursionSeconds,
    [property: JsonPropertyName("longestFocusStreakSeconds")] double LongestFocusStreakSeconds,
    [property: JsonPropertyName("fragmentationEpisodeCount")] int FragmentationEpisodeCount,
    [property: JsonPropertyName("socialMediaLoopCount")] int SocialMediaLoopCount,
    [property: JsonPropertyName("gamingEpisodeCount")] int GamingEpisodeCount,
    [property: JsonPropertyName("socialMediaSeconds")] double SocialMediaSeconds,
    [property: JsonPropertyName("gamingLauncherSeconds")] double GamingLauncherSeconds,
    [property: JsonPropertyName("gamingActiveSeconds")] double GamingActiveSeconds,
    [property: JsonPropertyName("passiveConsumptionSeconds")] double PassiveConsumptionSeconds,
    [property: JsonPropertyName("commMonitoringReturnCount")] int CommMonitoringReturnCount,
    [property: JsonPropertyName("socialMediaReturnCount")] int SocialMediaReturnCount,
    [property: JsonPropertyName("overloadEpisodeCount")] int OverloadEpisodeCount,
    [property: JsonPropertyName("breaksTakenCount")] int BreaksTakenCount,
    [property: JsonPropertyName("breaksSuggestedCount")] int BreaksSuggestedCount,
    [property: JsonPropertyName("mostDistractedHourLabel")] string MostDistractedHourLabel,
    [property: JsonPropertyName("developmentSeconds")] double DevelopmentSeconds,
    [property: JsonPropertyName("productivitySeconds")] double ProductivitySeconds,
    [property: JsonPropertyName("communicationSeconds")] double CommunicationSeconds,
    [property: JsonPropertyName("entertainmentSeconds")] double EntertainmentSeconds,
    // Phase 1A — new timeline fields for messaging system
    [property: JsonPropertyName("firstFocusBlockUtc")] string? FirstFocusBlockUtc = null,
    [property: JsonPropertyName("peakPressureUtc")] string? PeakPressureUtc = null,
    [property: JsonPropertyName("avgDistractionEpisodeDurationSeconds")] double AvgDistractionEpisodeDurationSeconds = 0d,
    [property: JsonPropertyName("contextSwitchCount")] int ContextSwitchCount = 0,
    [property: JsonPropertyName("preLongestDistractionFocusSeconds")] double PreLongestDistractionFocusSeconds = 0d,
    // Callers that know certain metrics are unreliable declare them here so the
    // email builder can render "no data" instead of fabricated numbers.
    [property: JsonPropertyName("missingFields")] IReadOnlyList<string>? MissingFields = null,
    [property: JsonPropertyName("topApps")] IReadOnlyList<FocusAppUsagePayload>? TopApps = null,
    // Workstream 4 — plan vs. reality: today's closed (done/abandoned) timed tasks, exactly
    // the measured numbers. Empty/null → the email's tasks section is omitted entirely.
    [property: JsonPropertyName("tasks")] IReadOnlyList<DigestTaskPayload>? Tasks = null,
    // Digest C1 — the day's stated goal + the midday yes/partly/not_yet answer (Workstream D).
    // The digest leads with goal-vs-reality; null/empty → the Goal section is omitted entirely
    // (a user who never set a goal must never see an empty or invented one).
    [property: JsonPropertyName("goal")] string? Goal = null,
    [property: JsonPropertyName("middayAnswer")] string? MiddayAnswer = null,
    // Digest redesign (2026-08-11) — the shape of the day for the email's timeline strip. Hours that
    // recorded nothing are absent, not zeroed; null/empty → the timeline section is omitted entirely.
    [property: JsonPropertyName("dayTimeline")] IReadOnlyList<DigestTimelineHourPayload>? DayTimeline = null);

// Workstream 4 — one task in the digest payload. Since the 2026-08-11 redesign this carries OPEN
// tasks too, not only closed ones: a task moved to tomorrow is still open, so closed-only made
// "moved on" unrenderable and made the planned-vs-finished count disagree with the evening card.
internal sealed record DigestTaskPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("plannedSeconds")] int PlannedSeconds,
    [property: JsonPropertyName("actualSeconds")] double ActualSeconds,
    [property: JsonPropertyName("offtaskSeconds")] double OfftaskSeconds,
    [property: JsonPropertyName("state")] string State,
    // Digest redesign (2026-08-11) — the goal-day this task was moved to by the close-out card.
    // Empty = not moved. The email renders a moved task as "moved on", never as a failure, and it
    // is why OPEN tasks are now sent at all: a moved task is still open, so filtering to closed
    // tasks made "moved on" unrenderable and the planned-vs-finished count wrong.
    [property: JsonPropertyName("deferredUntilDay")] string DeferredUntilDay = "");

/// <summary>
/// Digest redesign (2026-08-11) — one local hour of the day's shape, for the email's timeline.
/// NUMBERS ONLY: no titles, no site tokens. Focus + Active + Away sum to at most 3600.
///
/// An hour that recorded nothing is ABSENT from the list rather than sent as zeros. "Nothing
/// recorded" (the machine was off) is a different claim from "away" (there, but idle), and the
/// email renders the difference: an absent hour is empty track, so nothing there reads as nothing.
/// </summary>
public sealed record DigestTimelineHourPayload(
    [property: JsonPropertyName("hourLocal")] int HourLocal,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("activeSeconds")] double ActiveSeconds,
    [property: JsonPropertyName("awaySeconds")] double AwaySeconds);

/// <summary>
/// Lightweight periodic stats payload POSTed to /api/agent/daily-stats every 2 hours.
/// The server stores these in daily_stats so the server-side digest cron has data
/// even when the machine is off at the 19:30 UTC primary send time.
/// </summary>
internal sealed record DailyStatsReportPayload(
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("dateLocal")] string DateLocal,
    [property: JsonPropertyName("timezoneOffsetMinutes")] int TimezoneOffsetMinutes,
    [property: JsonPropertyName("reportedAtUtc")] string ReportedAtUtc,
    [property: JsonPropertyName("trackedSeconds")] double TrackedSeconds,
    [property: JsonPropertyName("focusSeconds")] double FocusSeconds,
    [property: JsonPropertyName("focusQualityScore")] double FocusQualityScore,
    [property: JsonPropertyName("distractionSeconds")] double DistractionSeconds,
    [property: JsonPropertyName("awaySeconds")] double AwaySeconds,
    [property: JsonPropertyName("contextExcursionSeconds")] double ContextExcursionSeconds,
    [property: JsonPropertyName("longestFocusStreakSeconds")] double LongestFocusStreakSeconds,
    [property: JsonPropertyName("fragmentationEpisodeCount")] int FragmentationEpisodeCount,
    [property: JsonPropertyName("socialMediaLoopCount")] int SocialMediaLoopCount,
    [property: JsonPropertyName("socialMediaSeconds")] double SocialMediaSeconds,
    [property: JsonPropertyName("overloadEpisodeCount")] int OverloadEpisodeCount,
    [property: JsonPropertyName("breaksTakenCount")] int BreaksTakenCount,
    [property: JsonPropertyName("breaksSuggestedCount")] int BreaksSuggestedCount,
    [property: JsonPropertyName("mostDistractedHourLabel")] string MostDistractedHourLabel,
    [property: JsonPropertyName("firstFocusBlockUtc")] string? FirstFocusBlockUtc = null,
    [property: JsonPropertyName("peakPressureUtc")] string? PeakPressureUtc = null,
    [property: JsonPropertyName("avgDistractionEpisodeDurationSeconds")] double AvgDistractionEpisodeDurationSeconds = 0d,
    [property: JsonPropertyName("contextSwitchCount")] int ContextSwitchCount = 0,
    // Coarse per-app foreground time for the day (label + seconds). Powers the console
    // per-app view and the server-fallback digest's "where your time went" section.
    [property: JsonPropertyName("topApps")] IReadOnlyList<FocusAppUsagePayload>? TopApps = null,
    // Phase 1b — Step 4c: privacy-safe ground-truth label engagement (counts only, never content).
    // Lets the console show whether users are answering prompts and whether Ari was right (net).
    [property: JsonPropertyName("labelsToday")] int LabelsToday = 0,
    [property: JsonPropertyName("labelsThisWeek")] int LabelsThisWeek = 0,
    [property: JsonPropertyName("labelsByTarget")] IReadOnlyList<LabelTargetCountPayload>? LabelsByTarget = null,
    // Prompt response rate (shown vs answered) so the console can show whether people actually engage.
    [property: JsonPropertyName("promptsShownToday")] int PromptsShownToday = 0,
    [property: JsonPropertyName("promptsAnsweredToday")] int PromptsAnsweredToday = 0,
    [property: JsonPropertyName("promptsShownThisWeek")] int PromptsShownThisWeek = 0,
    [property: JsonPropertyName("promptsAnsweredThisWeek")] int PromptsAnsweredThisWeek = 0,
    // B3 (v1.2.112) — label-persistence health from the user agent's heartbeat: consecutive
    // shared-file write failures (0 = healthy) + last exception type. Lets the ops console
    // show "label persistence failing" per machine instead of the failure staying invisible.
    [property: JsonPropertyName("labelPersistFailureCount")] long LabelPersistFailureCount = 0,
    [property: JsonPropertyName("labelPersistLastError")] string? LabelPersistLastError = null,
    // Digest C1 — the day's stated goal + the midday yes/partly/not_yet answer (Workstream D).
    // Carried on the 2-hourly stats upload because daily_goals is an AGENT-LOCAL table: without
    // this the server-cron fallback digest (built from daily_stats) has no way to lead with the
    // goal, which is the digest's headline section.
    [property: JsonPropertyName("goal")] string? Goal = null,
    [property: JsonPropertyName("middayAnswer")] string? MiddayAnswer = null,
    // Task attribution (2026-09-01) — how the drift gate is behaving in the field, in two numbers.
    //
    // The gate stops crediting a task once the user has spent ten continuous minutes working in
    // something that does not look like that task's work. That is a real behaviour change to the
    // number under a client's invoice, shipped to everyone at once, so it needs a signal that
    // arrives BEFORE a complaint does.
    //
    // Seconds alone are not enough. A day with 90 minutes held in one stretch is the feature
    // working; a day with 90 minutes held across fifteen separate stretches is the gate thrashing
    // and eating somebody's afternoon in slices. Only the pair tells them apart.
    //
    // Counts and seconds only — no application, no hash, no task, no title. Nothing here says what
    // anybody was doing, which is the same contract the rest of this payload already keeps.
    [property: JsonPropertyName("unassignedSecondsToday")] double UnassignedSecondsToday = 0d,
    [property: JsonPropertyName("driftHoldCountToday")] int DriftHoldCountToday = 0);

internal sealed record LabelTargetCountPayload(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("net")] int Net);

internal sealed record MachineActionEventPayload(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("interventionId")] string InterventionId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("actionCategory")] string ActionCategory,
    [property: JsonPropertyName("decisionState")] string DecisionState,
    [property: JsonPropertyName("executionState")] string ExecutionState,
    [property: JsonPropertyName("verifiedState")] string VerifiedState,
    [property: JsonPropertyName("timestampUtc")] string TimestampUtc,
    [property: JsonPropertyName("appliedAtUtc")] string? AppliedAtUtc,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("deliverySurface")] string? DeliverySurface,
    [property: JsonPropertyName("interactionKind")] string? InteractionKind,
    [property: JsonPropertyName("breakKind")] string? BreakKind,
    [property: JsonPropertyName("promptOutcome")] string? PromptOutcome,
    [property: JsonPropertyName("overlayState")] string? OverlayState,
    [property: JsonPropertyName("policyId")] string PolicyId,
    [property: JsonPropertyName("whatChanged")] string? WhatChanged,
    [property: JsonPropertyName("howToUndo")] string? HowToUndo);

// Response from GET /api/agent/user-context
internal sealed record UserContextPayload(
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("weakHour")] int? WeakHour,
    [property: JsonPropertyName("avgStreakMin")] int? AvgStreakMin,
    [property: JsonPropertyName("strongWindowStart")] int? StrongWindowStart,
    [property: JsonPropertyName("strongWindowEnd")] int? StrongWindowEnd,
    [property: JsonPropertyName("daysOfData")] int DaysOfData);

// 2d: ask the control plane to categorize coarse tokens the local curated rules left Unknown.
internal sealed record AppCategoryResolveRequestPayload(
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("tokens")] IReadOnlyList<string> Tokens);

// 2d: control-plane response — token → category name for already-resolved tokens; the rest are
// queued server-side and reported back in `pending` (we simply retry them on the next sync).
internal sealed record AppCategoryResolveResultPayload(
    [property: JsonPropertyName("mappings")] Dictionary<string, string>? Mappings,
    [property: JsonPropertyName("pending")] List<string>? Pending);

// ── Step 10 phase C/D (2026-08-14): the client report ────────────────────────────────────────────
//
// THE APPROVED FIELD LIST, and nothing outside it (spec §12.2). It is closed: no field is added
// here without going through the standing rule again.
//
// Notably ABSENT, and deliberately: senderName and senderEmail. The control plane resolves both
// from the account profile it already holds, because two copies of an identity can disagree and a
// client asserting its own sender identity is the worse of the two failures. Also absent: the rate
// or any money, the raw task title, focus quality, pressure, distraction, app names, plannedSeconds
// or anything remaining, and any flag for an excluded line — an excluded line is gone, not marked.
//
// This root is deliberately NOT walked by DigestStaysProjectFreeTests: the digest must stay
// project-free, and the report is the one payload that carries a project name on purpose.
internal sealed record ReportLinePayload(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("clientLabel")] string ClientLabel,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("hours")] double Hours,
    // Phase 2b, approved 2026-08-17. These must be DECLARED here or they are silently dropped:
    // the gateway round-trips the payload through this record on its way to the server.
    [property: JsonPropertyName("status")] string Status = "planned",
    [property: JsonPropertyName("dates")] string Dates = "",
    [property: JsonPropertyName("result")] string Result = "",
    [property: JsonPropertyName("blockedReason")] string BlockedReason = "",
    [property: JsonPropertyName("expectedFinish")] string ExpectedFinish = "",
    [property: JsonPropertyName("colourSlot")] int ColourSlot = 0);

/// Phase 2b — one task's share of one day, from task_day_hours.
internal sealed record ReportDaySegmentPayload(
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("hours")] double Hours);

// A day with 0.0 hours is SENT, never omitted: §4 requires days not worked to appear as gaps, and a
// gap the payload does not carry cannot be drawn.
internal sealed record ReportDayPayload(
    [property: JsonPropertyName("dateLocal")] string DateLocal,
    [property: JsonPropertyName("hours")] double Hours,
    [property: JsonPropertyName("segments")] IReadOnlyList<ReportDaySegmentPayload>? Segments = null);

internal sealed record ReportSendPayload(
    [property: JsonPropertyName("reportId")] string ReportId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("machineId")] string MachineId,
    // When the MACHINE asked. The server records when Resend accepted, and that is the timestamp
    // that answers a dispute.
    [property: JsonPropertyName("requestedAtUtc")] string RequestedAtUtc,
    [property: JsonPropertyName("timezoneOffsetMinutes")] int TimezoneOffsetMinutes,
    // The client-facing project name (§12.7), never the local one.
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("rangeStartLocal")] string RangeStartLocal,
    [property: JsonPropertyName("rangeEndLocal")] string RangeEndLocal,
    [property: JsonPropertyName("recipientEmail")] string RecipientEmail,
    [property: JsonPropertyName("recipientName")] string RecipientName,
    [property: JsonPropertyName("totalHours")] double TotalHours,
    [property: JsonPropertyName("daysWorked")] int DaysWorked,
    [property: JsonPropertyName("tasksCompleted")] int TasksCompleted,
    [property: JsonPropertyName("tasksTotal")] int TasksTotal,
    [property: JsonPropertyName("lines")] IReadOnlyList<ReportLinePayload> Lines,
    [property: JsonPropertyName("daily")] IReadOnlyList<ReportDayPayload> Daily,
    // Phase 2b — the paragraph the USER approved. Empty omits the section on the page.
    [property: JsonPropertyName("summary")] string Summary = "");

internal sealed record ReportPreviewResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("fromName")] string? FromName,
    [property: JsonPropertyName("fromAddress")] string? FromAddress,
    [property: JsonPropertyName("replyTo")] string? ReplyTo,
    [property: JsonPropertyName("reason")] string? Reason);

/// Phase 0.2 — the account's own sender identity, read back from the server that owns it.
internal sealed record ReportSenderResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("fromName")] string? FromName,
    [property: JsonPropertyName("fromAddress")] string? FromAddress,
    [property: JsonPropertyName("replyTo")] string? ReplyTo,
    [property: JsonPropertyName("reason")] string? Reason);

/// Phase 1.4 / 2b — the drafted paragraph, or a reason it could not be drafted.
internal sealed record ReportSummaryResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("reason")] string? Reason);

internal sealed record ReportSendResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("reportId")] string? ReportId,
    // Phase 2b — the permanent link, so the app can offer copy-link without inventing the URL.
    [property: JsonPropertyName("reportUrl")] string? ReportUrl,
    // The SERVER's timestamp — the one that corresponds to a real delivery.
    [property: JsonPropertyName("sentAtUtc")] string? SentAtUtc,
    [property: JsonPropertyName("reason")] string? Reason);
