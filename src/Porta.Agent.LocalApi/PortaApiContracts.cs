using Porta.Agent.Common.Contracts;

namespace Porta.Agent.LocalApi;

public sealed record LocalApiStatusResponse(
    AgentStatus Status,
    bool ServiceRunning,
    bool Paused,
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
    DateTimeOffset LastUpdatedUtc);

public sealed record LocalApiCurrentSnapshotResponse(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double Confidence,
    string LastAction,
    IReadOnlyList<string> Contributors);

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
    DateTimeOffset TimestampUtc,
    string Action,
    string TriggerState,
    double TriggerValue,
    double Confidence,
    int? DurationSeconds,
    string EscalationLevel,
    bool Reversible,
    string Reason,
    IReadOnlyList<string> Contributors);

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

public sealed record LocalApiAckResponse(
    bool Success,
    string Message,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiExportResponse(
    bool Success,
    string BundlePath,
    DateTimeOffset TimestampUtc);
