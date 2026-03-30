using System.Text.Json.Serialization;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;

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
    string BackendInstanceId,
    int BackendProcessId,
    string CaptureContext,
    DateTimeOffset? UserAgentHeartbeatUpdatedAtUtc,
    string? UserAgentInstanceId,
    int? UserAgentProcessId,
    string? UserAgentSessionId,
    bool KeyboardSignalActive,
    bool MouseSignalActive,
    DateTimeOffset? LastInputEventUtc,
    string? LastInputAgentInstanceId,
    string? LastInputSessionId,
    string? OwnershipWarning,
    string? CaptureFailureReason,
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
    DateTimeOffset LastUpdatedUtc,
    string CalibrationStage = CalibrationTruth.StageCold,
    bool SystemActionsEnabled = false,
    string TrustTier = CalibrationTruth.TrustLow,
    DateTimeOffset? UserAgentObservedHeartbeatUpdatedAtUtc = null,
    string? UserAgentObservedCaptureContext = null,
    string? UserAgentObservedSessionId = null,
    string? UserAgentStartupSource = null,
    string? UserAgentLaunchReason = null,
    int? UserAgentHeartbeatWriteFailureCount = null,
    string? CaptureSelfTestState = null,
    string? CaptureSelfTestErrorCode = null,
    int? CaptureSelfTestRemediationCount = null,
    string RuntimeStateFreshness = "unknown",
    DateTimeOffset? RuntimeStateLastSuccessfulRefreshUtc = null,
    DateTimeOffset? RuntimeStateLastAttemptUtc = null,
    string? RuntimeStateRefreshError = null)
{
    // Step 18 pinned snake_case aliases for operational truth consumers.
    public bool overload_active => OverloadActive;
    public bool deep_work_active => DeepWorkActive;
    public bool notifications_suppressed => NotificationsSuppressed;
    public bool brightness_adjusted => BrightnessAdjusted;
    public bool brightness_supported => BrightnessSupported;
    public bool db_migration_failed => DbMigrationFailed;
    public string calibration_stage => CalibrationStage;
    public bool system_actions_enabled => SystemActionsEnabled;
    public string trust_tier => TrustTier;
    public string backend_instance_id => BackendInstanceId;
    public int backend_process_id => BackendProcessId;
    public string capture_context => CaptureContext;
    public string? user_agent_heartbeat_utc => ToUtcMsStringOrNull(UserAgentHeartbeatUpdatedAtUtc);
    public string? user_agent_instance_id => UserAgentInstanceId;
    public int? user_agent_process_id => UserAgentProcessId;
    public string? user_agent_session_id => UserAgentSessionId;
    public bool keyboard_signal_active => KeyboardSignalActive;
    public bool mouse_signal_active => MouseSignalActive;
    public string? last_input_event_utc => ToUtcMsStringOrNull(LastInputEventUtc);
    public string? last_input_agent_instance_id => LastInputAgentInstanceId;
    public string? last_input_session_id => LastInputSessionId;
    public string? ownership_warning => OwnershipWarning;
    public string? capture_failure_reason => CaptureFailureReason;
    public string? user_agent_observed_heartbeat_utc => ToUtcMsStringOrNull(UserAgentObservedHeartbeatUpdatedAtUtc);
    public string? user_agent_observed_capture_context => UserAgentObservedCaptureContext;
    public string? user_agent_observed_session_id => UserAgentObservedSessionId;
    public string? user_agent_startup_source => UserAgentStartupSource;
    public string? user_agent_launch_reason => UserAgentLaunchReason;
    public int? user_agent_heartbeat_write_failure_count => UserAgentHeartbeatWriteFailureCount;
    public string? capture_self_test_state => CaptureSelfTestState;
    public string? capture_self_test_error_code => CaptureSelfTestErrorCode;
    public int? capture_self_test_remediation_count => CaptureSelfTestRemediationCount;
    public string runtime_state_freshness => RuntimeStateFreshness;
    public string? runtime_state_last_successful_refresh_utc => ToUtcMsStringOrNull(RuntimeStateLastSuccessfulRefreshUtc);
    public string? runtime_state_last_attempt_utc => ToUtcMsStringOrNull(RuntimeStateLastAttemptUtc);
    public string? runtime_state_refresh_error => RuntimeStateRefreshError;
    public string status_generated_at_utc => ToUtcMsString(LastUpdatedUtc);
    public string? last_successful_db_refresh_utc => runtime_state_last_successful_refresh_utc;
    public string freshness_state => LocalApiFreshnessHelpers.ResolveFreshnessState(RuntimeStateFreshness);
    public string? degraded_reason => LocalApiFreshnessHelpers.ResolveStatusDegradedReason(
        serviceRunning: ServiceRunning,
        localApiAuthReady: LocalApiAuthReady,
        localApiAuthError: LocalApiAuthError,
        dbMigrationFailed: DbMigrationFailed,
        runtimeStateFreshness: RuntimeStateFreshness,
        runtimeStateRefreshError: RuntimeStateRefreshError,
        notificationSuppressionReady: NotificationSuppressionReady,
        brightnessActionReady: BrightnessActionReady,
        lastError: LastError);
    public double? last_accepted_input_age_seconds => ComputeAgeSeconds(LastInputEventUtc);
    public int deep_work_streak_sec => DeepWorkStreakSeconds;
    public string last_updated_utc => ToUtcMsString(LastUpdatedUtc);
    public LocalApiFreshnessView freshness => new(
        generated_at_utc: status_generated_at_utc,
        captured_at_utc: null,
        last_successful_db_refresh_utc: last_successful_db_refresh_utc,
        last_attempt_utc: runtime_state_last_attempt_utc,
        freshness_state: freshness_state,
        degraded_reason: degraded_reason);
    public LocalApiRuntimeStateView runtime_state => new(
        freshness: RuntimeStateFreshness,
        stale: !string.Equals(RuntimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase),
        last_successful_refresh_utc: runtime_state_last_successful_refresh_utc,
        last_attempt_utc: runtime_state_last_attempt_utc,
        error: RuntimeStateRefreshError);

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
        brightness_ready: BrightnessActionReady,
        runtime_state_stale: !string.Equals(RuntimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase),
        runtime_state_refresh_error: RuntimeStateRefreshError,
        runtime_state_last_successful_refresh_utc: runtime_state_last_successful_refresh_utc,
        runtime_state_last_attempt_utc: runtime_state_last_attempt_utc);

    private static int ToRemainingSeconds(DateTimeOffset? untilUtc)
    {
        if (!untilUtc.HasValue)
        {
            return 0;
        }

        double seconds = (untilUtc.Value - DateTimeOffset.UtcNow).TotalSeconds;
        return seconds <= 0d ? 0 : (int)Math.Ceiling(seconds);
    }

    private static double? ComputeAgeSeconds(DateTimeOffset? utc)
    {
        if (!utc.HasValue)
        {
            return null;
        }

        double seconds = (DateTimeOffset.UtcNow - utc.Value.ToUniversalTime()).TotalSeconds;
        return seconds < 0d ? 0d : seconds;
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
    string? CalibrationStage = null,
    bool? SystemActionsEnabled = null,
    string? TrustTier = null,
    double? CurrentQualityScore = null,
    bool? PartialData = null,
    IReadOnlyList<string>? MissingMajorChannels = null,
    LocalApiCooldownsRemainingResponse? CooldownsRemaining = null,
    bool DeepWorkActive = false,
    bool NotificationsSuppressed = false,
    bool BrightnessAdjusted = false,
    double? FComponent = null,
    double? IComponent = null,
    double? VComponent = null,
    double TaskContinuityScore = 0d,
    double TaskContinuitySeconds = 0d,
    string? ContinuityState = null,
    double? ContinuityConfidence = null,
    double? SignalQualityScore = null,
    double? ModelConfidence = null,
    string? BreakPendingAction = null,
    string? BreakPendingReasonCode = null,
    double? BreakPendingEffectiveFocusSeconds = null,
    int? BreakPendingEffectiveFocusWindowSeconds = null,
    bool? BreakDeliveryBoundaryReached = null,
    string? BreakDeliveryBoundaryKind = null,
    double? BreakMicrobreakEffectiveFocusSeconds = null,
    double? BreakShortBreakEffectiveFocusSeconds = null,
    string RuntimeStateFreshness = "unknown",
    DateTimeOffset? RuntimeStateLastSuccessfulRefreshUtc = null,
    DateTimeOffset? RuntimeStateLastAttemptUtc = null,
    string? RuntimeStateRefreshError = null)
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
    public string? calibration_stage => CalibrationStage;
    public bool? system_actions_enabled => SystemActionsEnabled;
    public string? trust_tier => TrustTier;
    public double? F => FComponent;
    public double? I => IComponent;
    public double? V => VComponent;
    public double task_continuity_score => TaskContinuityScore;
    public double task_continuity_seconds => TaskContinuitySeconds;
    public string? continuity_state => ContinuityState;
    public double? continuity_confidence => ContinuityConfidence;
    public double behavior_continuity_score => TaskContinuityScore;
    public double? signal_quality_score => SignalQualityScore;
    public double model_confidence => ModelConfidence ?? Confidence;
    public string? break_pending_action => BreakPendingAction;
    public string? break_pending_reason_code => BreakPendingReasonCode;
    public double? break_pending_effective_focus_s => BreakPendingEffectiveFocusSeconds;
    public int? break_pending_effective_focus_window_s => BreakPendingEffectiveFocusWindowSeconds;
    public bool? break_delivery_boundary_reached => BreakDeliveryBoundaryReached;
    public string? break_delivery_boundary_kind => BreakDeliveryBoundaryKind;
    public double? break_microbreak_effective_focus_s => BreakMicrobreakEffectiveFocusSeconds;
    public double? break_short_break_effective_focus_s => BreakShortBreakEffectiveFocusSeconds;
    public string runtime_state_freshness => RuntimeStateFreshness;
    public string? runtime_state_last_successful_refresh_utc => ToUtcMsStringOrNull(RuntimeStateLastSuccessfulRefreshUtc);
    public string? runtime_state_last_attempt_utc => ToUtcMsStringOrNull(RuntimeStateLastAttemptUtc);
    public string? runtime_state_refresh_error => RuntimeStateRefreshError;
    public string snapshot_captured_at_utc => ToUtcMsString(TimestampUtc);
    public string? last_successful_db_refresh_utc => runtime_state_last_successful_refresh_utc;
    public string freshness_state => LocalApiFreshnessHelpers.ResolveFreshnessState(RuntimeStateFreshness);
    public string? degraded_reason => LocalApiFreshnessHelpers.ResolveSnapshotDegradedReason(
        runtimeStateFreshness: RuntimeStateFreshness,
        runtimeStateRefreshError: RuntimeStateRefreshError);
    public string timestamp_utc => TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public LocalApiFreshnessView freshness => new(
        generated_at_utc: null,
        captured_at_utc: snapshot_captured_at_utc,
        last_successful_db_refresh_utc: last_successful_db_refresh_utc,
        last_attempt_utc: runtime_state_last_attempt_utc,
        freshness_state: freshness_state,
        degraded_reason: degraded_reason);
    public LocalApiRuntimeStateView runtime_state => new(
        freshness: RuntimeStateFreshness,
        stale: !string.Equals(RuntimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase),
        last_successful_refresh_utc: runtime_state_last_successful_refresh_utc,
        last_attempt_utc: runtime_state_last_attempt_utc,
        error: RuntimeStateRefreshError);
    public LocalApiSnapshotCurrentView current => new(
        overload_active: overload_active,
        deep_work_active: deep_work_active,
        notifications_suppressed: notifications_suppressed,
        brightness_adjusted: brightness_adjusted,
        overload_likelihood: overload_likelihood,
        confidence: Confidence,
        model_confidence: ModelConfidence ?? Confidence,
        signal_quality_score: SignalQualityScore,
        behavior_continuity_score: TaskContinuityScore,
        sustained_attention_duration_sec: sustained_attention_duration_sec,
        break_pending_action: BreakPendingAction,
        break_pending_reason_code: BreakPendingReasonCode,
        break_pending_effective_focus_s: BreakPendingEffectiveFocusSeconds,
        break_pending_effective_focus_window_s: BreakPendingEffectiveFocusWindowSeconds,
        break_delivery_boundary_reached: BreakDeliveryBoundaryReached,
        break_delivery_boundary_kind: BreakDeliveryBoundaryKind,
        f: F,
        i: I,
        v: V);

    private static string ToUtcMsString(DateTimeOffset utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string? ToUtcMsStringOrNull(DateTimeOffset? utc) =>
        utc.HasValue ? ToUtcMsString(utc.Value) : null;

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
    bool brightness_ready,
    bool runtime_state_stale = false,
    string? runtime_state_refresh_error = null,
    string? runtime_state_last_successful_refresh_utc = null,
    string? runtime_state_last_attempt_utc = null);

public sealed record LocalApiRuntimeStateView(
    string freshness,
    bool stale,
    string? last_successful_refresh_utc,
    string? last_attempt_utc,
    string? error);

public sealed record LocalApiFreshnessView(
    string? generated_at_utc,
    string? captured_at_utc,
    string? last_successful_db_refresh_utc,
    string? last_attempt_utc,
    string freshness_state,
    string? degraded_reason);

internal static class LocalApiFreshnessHelpers
{
    internal static string ResolveFreshnessState(string runtimeStateFreshness) =>
        string.Equals(runtimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase)
            ? "fresh"
            : "stale";

    internal static string? ResolveSnapshotDegradedReason(
        string runtimeStateFreshness,
        string? runtimeStateRefreshError)
    {
        if (!string.Equals(runtimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(runtimeStateRefreshError)
                ? "snapshot_stale"
                : NormalizeReasonToken(runtimeStateRefreshError);
        }

        return null;
    }

    internal static string? ResolveStatusDegradedReason(
        bool serviceRunning,
        bool localApiAuthReady,
        string? localApiAuthError,
        bool dbMigrationFailed,
        string runtimeStateFreshness,
        string? runtimeStateRefreshError,
        bool notificationSuppressionReady,
        bool brightnessActionReady,
        string? lastError)
    {
        if (!serviceRunning)
        {
            return "service_unavailable";
        }

        if (!localApiAuthReady)
        {
            return string.IsNullOrWhiteSpace(localApiAuthError)
                ? "api_auth_not_ready"
                : NormalizeReasonToken(localApiAuthError);
        }

        if (dbMigrationFailed)
        {
            return "db_migration_failed";
        }

        if (!string.Equals(runtimeStateFreshness, "fresh", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(runtimeStateRefreshError)
                ? "status_stale"
                : NormalizeReasonToken(runtimeStateRefreshError);
        }

        if (!notificationSuppressionReady)
        {
            return "notification_outputs_degraded";
        }

        if (!brightnessActionReady)
        {
            return "brightness_outputs_degraded";
        }

        return string.IsNullOrWhiteSpace(lastError) ? null : "runtime_error";
    }

    private static string NormalizeReasonToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Replace('-', '_').ToLowerInvariant();
    }
}

public sealed record LocalApiSnapshotCurrentView(
    bool overload_active,
    bool deep_work_active,
    bool notifications_suppressed,
    bool brightness_adjusted,
    double overload_likelihood,
    double confidence,
    double model_confidence,
    double? signal_quality_score,
    double behavior_continuity_score,
    double sustained_attention_duration_sec,
    string? break_pending_action,
    string? break_pending_reason_code,
    double? break_pending_effective_focus_s,
    int? break_pending_effective_focus_window_s,
    bool? break_delivery_boundary_reached,
    string? break_delivery_boundary_kind,
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
    string LifecycleState,
    string CommandType = "",
    string ExecutionState = "decision_only",
    DateTimeOffset? DecidedAtUtc = null,
    DateTimeOffset? AppliedAtUtc = null,
    DateTimeOffset? AckedAtUtc = null,
    string ExecutionErrorCode = "",
    string ExecutorAgentInstanceId = "")
{
    // Step 18E pinned proof-trail aliases for events/recent consumers.
    public DateTimeOffset ts_utc => TsUtc;
    public string why_json => WhyJson;
    public string? state_flags_json => StateFlagsJson;
    public string command_type => CommandType;
    public string execution_state => ExecutionState;
    public string? execution_error_code => string.IsNullOrWhiteSpace(ExecutionErrorCode) ? null : ExecutionErrorCode;
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

public sealed record LocalApiIdentityLinkResponse(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("account_id")] string? AccountId,
    [property: JsonPropertyName("org_id")] string? OrgId,
    [property: JsonPropertyName("link_status")] string LinkStatus,
    [property: JsonPropertyName("linked_at_utc")] DateTimeOffset? LinkedAtUtc,
    [property: JsonPropertyName("updated_at_utc")] DateTimeOffset UpdatedAtUtc);

public sealed record LocalApiSetIdentityLinkRequest(
    [property: JsonPropertyName("account_id")] string? AccountId,
    [property: JsonPropertyName("org_id")] string? OrgId);

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

public sealed record LocalApiInterventionPendingResponse(
    long Id,
    [property: JsonPropertyName("intervention_id")] string InterventionId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("command_type")] string CommandType,
    [property: JsonPropertyName("payload_json")] string PayloadJson,
    [property: JsonPropertyName("decided_at_utc")] DateTimeOffset DecidedAtUtc,
    [property: JsonPropertyName("execution_state")] string ExecutionState,
    [property: JsonPropertyName("policy_id")] string PolicyId,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("escalation_level")] string EscalationLevel,
    [property: JsonPropertyName("what_changed")] string WhatChanged);

public sealed record LocalApiInterventionAckRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("intervention_id")] string? InterventionId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agent_instance_id")] string? AgentInstanceId,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("applied_at_utc")] DateTimeOffset? AppliedAtUtc);

public sealed record LocalApiInputSignalsRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("agent_instance_id")] string? AgentInstanceId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("window_start_utc")] DateTimeOffset? WindowStartUtc,
    [property: JsonPropertyName("window_end_utc")] DateTimeOffset? WindowEndUtc,
    [property: JsonPropertyName("keyboard_keydown_count")] int KeyboardKeydownCount,
    [property: JsonPropertyName("keyboard_backspace_count")] int KeyboardBackspaceCount,
    [property: JsonPropertyName("mouse_wheel_delta")] int MouseWheelDelta,
    [property: JsonPropertyName("mouse_move_dx")] double MouseMoveDx,
    [property: JsonPropertyName("mouse_move_dy")] double MouseMoveDy,
    [property: JsonPropertyName("focus_app_hash")] string? FocusAppHash,
    [property: JsonPropertyName("idle_seconds")] double IdleSeconds,
    [property: JsonPropertyName("last_input_event_utc")] DateTimeOffset? LastInputEventUtc,
    [property: JsonPropertyName("keyboard_iki_mean_ms")] double? KeyboardIkiMeanMs = null,
    [property: JsonPropertyName("keyboard_iki_std_ms")] double? KeyboardIkiStdMs = null,
    [property: JsonPropertyName("keyboard_iki_cv")] double? KeyboardIkiCv = null,
    [property: JsonPropertyName("keyboard_burst_count")] int KeyboardBurstCount = 0,
    [property: JsonPropertyName("keyboard_burst_rate")] double? KeyboardBurstRate = null,
    [property: JsonPropertyName("keyboard_correction_spike_count")] int KeyboardCorrectionSpikeCount = 0,
    [property: JsonPropertyName("keyboard_correction_spike_rate")] double? KeyboardCorrectionSpikeRate = null,
    [property: JsonPropertyName("mouse_move_sample_count")] int MouseMoveSampleCount = 0,
    [property: JsonPropertyName("mouse_path_length_px")] double? MousePathLengthPx = null,
    [property: JsonPropertyName("mouse_speed_mean_px_per_sec")] double? MouseSpeedMeanPxPerSec = null,
    [property: JsonPropertyName("mouse_jerk_p95")] double? MouseJerkP95 = null,
    [property: JsonPropertyName("mouse_micro_correction_count")] int MouseMicroCorrectionCount = 0,
    [property: JsonPropertyName("mouse_micro_correction_rate")] double? MouseMicroCorrectionRate = null,
    [property: JsonPropertyName("mouse_wheel_event_count")] int MouseWheelEventCount = 0,
    [property: JsonPropertyName("mouse_scroll_burst_count")] int MouseScrollBurstCount = 0,
    [property: JsonPropertyName("mouse_scroll_burst_rate")] double? MouseScrollBurstRate = null,
    [property: JsonPropertyName("mouse_scroll_speed_mean")] double? MouseScrollSpeedMean = null,
    [property: JsonPropertyName("focus_observation_count")] int FocusObservationCount = 0,
    [property: JsonPropertyName("focus_switch_count")] int FocusSwitchCount = 0,
    [property: JsonPropertyName("focus_active_app_count")] int FocusActiveAppCount = 0,
    [property: JsonPropertyName("focus_app_entropy")] double? FocusAppEntropy = null,
    [property: JsonPropertyName("idle_observation_count")] int IdleObservationCount = 0,
    [property: JsonPropertyName("idle_seconds_within_window")] double? IdleSecondsWithinWindow = null,
    [property: JsonPropertyName("micro_idle_count")] int MicroIdleCount = 0,
    [property: JsonPropertyName("micro_idle_seconds")] double? MicroIdleSeconds = null,
    [property: JsonPropertyName("break_seconds_within_window")] double? BreakSecondsWithinWindow = null,
    [property: JsonPropertyName("short_idle_boundary_observed")] bool ShortIdleBoundaryObserved = false,
    [property: JsonPropertyName("notification_toasts_enabled")] bool? NotificationToastsEnabled = null);

public sealed record LocalApiAckResponse(
    bool Success,
    string Message,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiExportResponse(
    bool Success,
    string BundlePath,
    DateTimeOffset TimestampUtc);
