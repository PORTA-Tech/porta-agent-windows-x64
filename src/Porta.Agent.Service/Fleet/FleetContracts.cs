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
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

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
    [property: JsonPropertyName("device")] DeviceProfilePayload Device);

/// <summary>One app/site's foreground time for the day — coarse label + seconds. Powers the
/// "where your time went" section of the digest. Label is a coarse token only (e.g. "youtube").</summary>
internal sealed record FocusAppUsagePayload(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("seconds")] double Seconds,
    // Coarse AppCategory enum value (0=Unknown) so distraction can be attributed to specific
    // apps/sites (entertainment/social/gaming = distracting) without re-deriving categories server-side.
    [property: JsonPropertyName("category")] int Category = 0);

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
    [property: JsonPropertyName("topApps")] IReadOnlyList<FocusAppUsagePayload>? TopApps = null);

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
    [property: JsonPropertyName("labelsByTarget")] IReadOnlyList<LabelTargetCountPayload>? LabelsByTarget = null);

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
