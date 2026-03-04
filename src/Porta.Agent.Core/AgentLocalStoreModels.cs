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
    string StateFlagsJson = "{\"schema_ver\":1}");

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
    double? CurrentQualityScore = null,
    bool? PartialData = null,
    IReadOnlyList<string>? MissingMajorChannels = null);

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
