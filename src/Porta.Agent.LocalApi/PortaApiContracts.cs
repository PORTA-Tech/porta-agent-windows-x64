using Porta.Agent.Common.Contracts;

namespace Porta.Agent.LocalApi;

public sealed record LocalApiStatusResponse(
    AgentStatus Status,
    bool ServiceRunning,
    bool Paused,
    DateTimeOffset? PauseUntilUtc,
    bool ConsentActive,
    string ConsentVersion,
    DateTimeOffset? ConsentTimestampUtc,
    string ConsentDisclosureHash,
    bool BackendConnected,
    double FocusStrength,
    double OverloadLikelihood,
    string LastAction,
    string AgentVersion,
    string SchemaVersion,
    string PolicyVersion,
    DateTimeOffset? LastEventTimestampUtc,
    string? LastError,
    bool LocalApiAuthReady,
    string? LocalApiAuthError,
    bool NotificationSuppressionActive,
    bool NotificationSuppressionReady,
    string? NotificationSuppressionLastError,
    string? NotificationSuppressionError,
    DateTimeOffset? NotificationSuppressionBackoffUntilUtc,
    DateTimeOffset? NotificationBackoffUntilUtc,
    bool NotificationsSuppressed,
    bool BrightnessActionReady,
    bool BrightnessActionActive,
    bool BrightnessAdjusted,
    bool BrightnessSupported,
    int BrightnessCurrentLevel,
    int BrightnessTargetLevel,
    string? BrightnessLastError,
    string? BrightnessError,
    DateTimeOffset? BrightnessBackoffUntilUtc,
    bool DbMigrationFailed,
    bool BaselineReady,
    bool ColdStart,
    bool OverloadActive,
    bool DeepWorkActive,
    int DeepWorkStreakSeconds,
    string VisualMode,
    string? LatestContributorsJson,
    string? LatestWhyJson,
    DateTimeOffset LastUpdatedUtc)
{
    // Step 18 pinned snake_case aliases for operational truth consumers.
    public bool overload_active => OverloadActive;
    public bool deep_work_active => DeepWorkActive;
    public bool notifications_suppressed => NotificationsSuppressed;
    public bool brightness_adjusted => BrightnessAdjusted;
    public bool brightness_supported => BrightnessSupported;
    public bool db_migration_failed => DbMigrationFailed;
}

public sealed record LocalApiCurrentSnapshotResponse(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double SmoothedTypingBurstIntensity,
    double SmoothedScrollVelocityIndex,
    double SmoothedFocusSwitchRate,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double DominantStateValue,
    string DominantStateName,
    double Confidence,
    string LastAction,
    bool ActivationWindowTriggered,
    bool CooldownTriggered,
    bool ActivationWindowOpen,
    IReadOnlyList<string> Contributors,
    double SustainedAttentionDurationSec = 0,
    double IdleFragmentationIndex = 0,
    double FocusChoppinessIndex = 0,
    bool OverloadActive = false,
    string? StateFlagsJson = null,
    string? ContributorsJson = null,
    bool? BaselineReady = null,
    double? BaselineReadyPct = null,
    double? AvgQuality2m = null,
    bool? ColdStart = null,
    double? CurrentQualityScore = null,
    bool? PartialData = null,
    IReadOnlyList<string>? MissingMajorChannels = null,
    LocalApiCooldownsRemainingResponse? CooldownsRemaining = null);

public sealed record LocalApiCooldownsRemainingResponse(
    int NotificationEnableSeconds,
    int NotificationMinOnSeconds,
    int BrightnessSeconds,
    int BreakSuggestionSeconds,
    bool AnyActive);

public sealed record LocalApiInputsSummaryResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int WindowSeconds,
    int SampleCount,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double IdleFragmentationIndex,
    double NotificationLatencyIndex);

public sealed record LocalApiEventResponse(
    long Id,
    string Type,
    DateTimeOffset TsUtc,
    string IdempotencyKey,
    string WhyJson,
    string? StateFlagsJson,
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
    string InterventionId,
    string PolicyId,
    string Domain,
    DateTimeOffset? PlannedEndTimestampUtc,
    DateTimeOffset? ActualEndTimestampUtc,
    string PreviousSystemStateJson,
    string WhatChanged,
    string HowToUndo,
    bool ReversalApplied,
    DateTimeOffset? CooldownEndTimestampUtc,
    string LifecycleState);

public sealed record LocalApiTimelineResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int BucketSeconds,
    IReadOnlyList<LocalApiCurrentSnapshotResponse> Snapshots,
    IReadOnlyList<LocalApiEventResponse> Interventions);

public sealed record LocalApiSettingsResponse(
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

public sealed record LocalApiUpdateSettingsRequest(
    string? RegulationIntensity,
    string? PrivacyLevel,
    bool? NotificationReductionEnabled,
    bool? FocusModeEnabled,
    bool? AnimationReductionEnabled,
    bool? TemporaryAppRestrictionEnabled);

public sealed record LocalApiRetentionResponse(
    int RetentionDays,
    long DatabaseBytes,
    long LogBytes,
    long ConsentBytes,
    long TotalBytes,
    DateTimeOffset UpdatedAtUtc);

public sealed record LocalApiRecalibrationResponse(
    bool Active,
    DateTimeOffset StartedUtc,
    DateTimeOffset UntilUtc);

public sealed record LocalApiPauseRequest(
    string? Reason,
    DateTimeOffset? UntilUtc);

public sealed record LocalApiTestInterventionRequest(
    string Action);

public sealed record LocalApiAckResponse(
    bool Success,
    string Message,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiExportResponse(
    bool Success,
    string BundlePath,
    DateTimeOffset TimestampUtc);
