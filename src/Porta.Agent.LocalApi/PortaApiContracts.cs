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
    public int deep_work_streak_sec => DeepWorkStreakSeconds;
    public string last_updated_utc => ToUtcMsString(LastUpdatedUtc);

    // Step 18H/J: product-facing grouped contract blocks.
    public LocalApiStatusAuthView auth => new(LocalApiAuthReady, LocalApiAuthError);
    public LocalApiStatusOutputsView outputs => new(
        notifications_suppressed: NotificationsSuppressed,
        brightness_adjusted: BrightnessAdjusted,
        brightness_supported: BrightnessSupported,
        notification_error: NotificationSuppressionError,
        brightness_error: BrightnessError,
        notification_backoff_sec_remaining: ToRemainingSeconds(NotificationBackoffUntilUtc),
        brightness_backoff_sec_remaining: ToRemainingSeconds(BrightnessBackoffUntilUtc),
        notification_backoff_utc: ToUtcMsStringOrNull(NotificationBackoffUntilUtc),
        brightness_backoff_utc: ToUtcMsStringOrNull(BrightnessBackoffUntilUtc));
    public LocalApiStatusDegradedView degraded => new(
        degraded: !ServiceRunning || !LocalApiAuthReady || DbMigrationFailed || !NotificationSuppressionReady || !BrightnessActionReady,
        service_running: ServiceRunning,
        db_migration_failed: DbMigrationFailed,
        auth_ready: LocalApiAuthReady,
        notification_ready: NotificationSuppressionReady,
        brightness_ready: BrightnessActionReady);

    private static int ToRemainingSeconds(DateTimeOffset? untilUtc)
    {
        if (!untilUtc.HasValue)
        {
            return 0;
        }

        double seconds = (untilUtc.Value - DateTimeOffset.UtcNow).TotalSeconds;
        return seconds <= 0d ? 0 : (int)Math.Ceiling(seconds);
    }

    private static string ToUtcMsString(DateTimeOffset utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string? ToUtcMsStringOrNull(DateTimeOffset? utc) =>
        utc.HasValue ? ToUtcMsString(utc.Value) : null;
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
    LocalApiCooldownsRemainingResponse? CooldownsRemaining = null,
    bool DeepWorkActive = false,
    bool NotificationsSuppressed = false,
    bool BrightnessAdjusted = false,
    double? FComponent = null,
    double? IComponent = null,
    double? VComponent = null)
{
    // Step 18D pinned snake_case aliases for snapshot consumers.
    public double overload_likelihood => OverloadLikelihood;
    public double sustained_attention_duration_sec => SustainedAttentionDurationSec;
    public LocalApiCooldownsRemainingResponse? cooldowns_remaining => CooldownsRemaining;
    public string? contributors_json => ContributorsJson;
    public string? state_flags_json => StateFlagsJson;
    public bool overload_active => OverloadActive;
    public bool deep_work_active => DeepWorkActive;
    public bool notifications_suppressed => NotificationsSuppressed;
    public bool brightness_adjusted => BrightnessAdjusted;
    public double? F => FComponent;
    public double? I => IComponent;
    public double? V => VComponent;
    public string timestamp_utc => TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public LocalApiSnapshotCurrentView current => new(
        overload_active: overload_active,
        deep_work_active: deep_work_active,
        notifications_suppressed: notifications_suppressed,
        brightness_adjusted: brightness_adjusted,
        overload_likelihood: overload_likelihood,
        confidence: Confidence,
        sustained_attention_duration_sec: sustained_attention_duration_sec,
        f: F,
        i: I,
        v: V);
}

public sealed record LocalApiCooldownsRemainingResponse(
    int BreakSuggestionSecRemaining,
    int DeepWorkNotificationEnableSecRemaining,
    int BrightnessActionSecRemaining,
    int DeepWorkNotificationMinOnSecRemaining,
    bool AnyActive)
{
    // Backward-compatible aliases for existing UI code.
    public int BreakSuggestionSeconds => BreakSuggestionSecRemaining;
    public int NotificationEnableSeconds => DeepWorkNotificationEnableSecRemaining;
    public int BrightnessSeconds => BrightnessActionSecRemaining;
    public int NotificationMinOnSeconds => DeepWorkNotificationMinOnSecRemaining;

    // Step 18D pinned snake_case aliases.
    public int break_suggestion_sec_remaining => BreakSuggestionSecRemaining;
    public int deep_work_notification_enable_sec_remaining => DeepWorkNotificationEnableSecRemaining;
    public int brightness_action_sec_remaining => BrightnessActionSecRemaining;
}

public sealed record LocalApiStatusAuthView(
    bool ready,
    string? error);

public sealed record LocalApiStatusOutputsView(
    bool notifications_suppressed,
    bool brightness_adjusted,
    bool brightness_supported,
    string? notification_error,
    string? brightness_error,
    int notification_backoff_sec_remaining,
    int brightness_backoff_sec_remaining,
    string? notification_backoff_utc,
    string? brightness_backoff_utc);

public sealed record LocalApiStatusDegradedView(
    bool degraded,
    bool service_running,
    bool db_migration_failed,
    bool auth_ready,
    bool notification_ready,
    bool brightness_ready);

public sealed record LocalApiSnapshotCurrentView(
    bool overload_active,
    bool deep_work_active,
    bool notifications_suppressed,
    bool brightness_adjusted,
    double overload_likelihood,
    double confidence,
    double sustained_attention_duration_sec,
    double? f,
    double? i,
    double? v);

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
    string LifecycleState)
{
    // Step 18E pinned proof-trail aliases for events/recent consumers.
    public DateTimeOffset ts_utc => TsUtc;
    public string why_json => WhyJson;
    public string? state_flags_json => StateFlagsJson;
}

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
