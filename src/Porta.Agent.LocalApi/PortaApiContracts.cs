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
    string? RuntimeStateRefreshError = null,
    // Canonical expressive pressure signal, separate from the conservative
    // OverloadLikelihood used as the stabilization gate input.
    // Always show with PressureConfidence labeled separately.
    double PressureLevel = 0d,
    double PressureConfidence = 0d)
{
    // Step 18 pinned snake_case aliases for operational truth consumers.
    public bool overload_active => OverloadActive;
    public double pressure_level => PressureLevel > 0d ? PressureLevel : OverloadLikelihood;
    public double pressure_confidence => PressureConfidence;
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
    string? BreakPendingKind = null,
    string? BreakPendingReasonCode = null,
    double? BreakPendingEffectiveFocusSeconds = null,
    int? BreakPendingEffectiveFocusWindowSeconds = null,
    bool? BreakDeliveryBoundaryReached = null,
    string? BreakDeliveryBoundaryKind = null,
    double? BreakShortBreakEffectiveFocusSeconds = null,
    double? BreakLongBreakEffectiveFocusSeconds = null,
    string RuntimeStateFreshness = "unknown",
    DateTimeOffset? RuntimeStateLastSuccessfulRefreshUtc = null,
    DateTimeOffset? RuntimeStateLastAttemptUtc = null,
    string? RuntimeStateRefreshError = null,
    bool AnalyzedStateAvailable = false,
    string ModelSource = "brain_fallback",
    double FocusEpisodeQuality = 0d,
    double FocusEpisodeSeconds = 0d,
    string? FocusEpisodeState = null,
    string AttentionSegmentKind = LocalAttentionSegmentAnalytics.FocusSegmentKind,
    double AttentionSegmentSeconds = 0d,
    bool DistractionEpisodeActive = false,
    double DistractionEpisodeSeconds = 0d,
    string AttentionSegmentReasonCode = LocalAttentionSegmentAnalytics.SteadyFocusReasonCode,
    string AttentionSegmentReasonText = "Steady focus episode.",
    string FocusEpisodeReasonCode = LocalAttentionSegmentAnalytics.FocusEpisodeActiveReasonCode,
    string FocusEpisodeReasonText = "Focus episode active.",
    double? PressureLevel = null,
    double? PressureConfidence = null)
{
    // Step 18D pinned snake_case aliases for snapshot consumers.
    public double overload_likelihood => OverloadLikelihood;
    public double pressure_level => PressureLevel ?? OverloadLikelihood;
    public double pressure_confidence => PressureConfidence ?? (ModelConfidence ?? Confidence);
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
    public double focus_episode_quality => FocusEpisodeQuality;
    public double focus_episode_seconds => FocusEpisodeSeconds;
    public string? focus_episode_state => FocusEpisodeState ?? ContinuityState;
    public string attention_segment_kind => LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(AttentionSegmentKind);
    public double attention_segment_seconds => AttentionSegmentSeconds;
    public bool distraction_episode_active => DistractionEpisodeActive;
    public double distraction_episode_seconds => DistractionEpisodeSeconds;
    public string attention_segment_reason_code => LocalAttentionSegmentAnalytics.NormalizeReasonCode(AttentionSegmentReasonCode);
    public string attention_segment_reason_text => AttentionSegmentReasonText;
    public string focus_episode_reason_code => LocalAttentionSegmentAnalytics.NormalizeReasonCode(FocusEpisodeReasonCode);
    public string focus_episode_reason_text => FocusEpisodeReasonText;
    public double? continuity_confidence => ContinuityConfidence;
    public double behavior_continuity_score => TaskContinuityScore;
    public double? signal_quality_score => SignalQualityScore;
    public double model_confidence => ModelConfidence ?? Confidence;
    public string? break_pending_action => BreakPendingAction;
    public string? break_pending_kind => BreakPendingKind;
    public string? break_pending_reason_code => BreakPendingReasonCode;
    public double? break_pending_effective_focus_s => BreakPendingEffectiveFocusSeconds;
    public int? break_pending_effective_focus_window_s => BreakPendingEffectiveFocusWindowSeconds;
    public bool? break_delivery_boundary_reached => BreakDeliveryBoundaryReached;
    public string? break_delivery_boundary_kind => BreakDeliveryBoundaryKind;
    public double? break_short_break_effective_focus_s => BreakShortBreakEffectiveFocusSeconds;
    public double? break_long_break_effective_focus_s => BreakLongBreakEffectiveFocusSeconds;
    // Legacy compatibility aliases:
    public double? break_microbreak_effective_focus_s => BreakShortBreakEffectiveFocusSeconds;
    public double? break_legacy_short_break_effective_focus_s => BreakLongBreakEffectiveFocusSeconds;
    public string runtime_state_freshness => RuntimeStateFreshness;
    public string? runtime_state_last_successful_refresh_utc => ToUtcMsStringOrNull(RuntimeStateLastSuccessfulRefreshUtc);
    public string? runtime_state_last_attempt_utc => ToUtcMsStringOrNull(RuntimeStateLastAttemptUtc);
    public string? runtime_state_refresh_error => RuntimeStateRefreshError;
    public bool analysis_ready => AnalyzedStateAvailable;
    public string model_source => ModelSource;
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
        pressure_level: pressure_level,
        pressure_confidence: pressure_confidence,
        confidence: Confidence,
        model_confidence: ModelConfidence ?? Confidence,
        signal_quality_score: SignalQualityScore,
        behavior_continuity_score: TaskContinuityScore,
        focus_episode_quality: FocusEpisodeQuality,
        focus_episode_seconds: FocusEpisodeSeconds,
        focus_episode_state: FocusEpisodeState ?? ContinuityState,
        attention_segment_kind: attention_segment_kind,
        attention_segment_seconds: attention_segment_seconds,
        distraction_episode_active: distraction_episode_active,
        distraction_episode_seconds: distraction_episode_seconds,
        attention_segment_reason_code: attention_segment_reason_code,
        attention_segment_reason_text: attention_segment_reason_text,
        focus_episode_reason_code: focus_episode_reason_code,
        focus_episode_reason_text: focus_episode_reason_text,
        sustained_attention_duration_sec: sustained_attention_duration_sec,
        break_pending_action: BreakPendingAction,
        break_pending_kind: BreakPendingKind,
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
    // Step 2 migration map:
    // BreakSuggestionSecRemaining is a legacy shared break-suggestion cooldown field.
    // The canonical product model will eventually split short-break and long-break
    // overlay timing instead of routing both through one compatibility alias.
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

public sealed record LocalApiHealthResponse(
    [property: JsonPropertyName("alive")] bool Alive,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("process_id")] int ProcessId,
    [property: JsonPropertyName("agent_version")] string AgentVersion);

public sealed record LocalApiLiveSessionStateRequest(
    int SchemaVer,
    string AgentInstanceId,
    string SessionId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset SessionStartedAtUtc,
    DateTimeOffset? LastInputEventUtc,
    bool KeyboardActive,
    bool MouseActive,
    double IdleSeconds,
    string? FocusAppHash,
    DateTimeOffset? FocusStartedAtUtc = null,
    long? HeartbeatSequence = null,
    DateTimeOffset? HeartbeatUpdatedAtUtc = null,
    string? HeartbeatCaptureContext = null);

public sealed record LocalApiLiveSessionStateResponse(
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? SessionStartedAtUtc,
    DateTimeOffset? LastInputEventUtc,
    string? AgentInstanceId,
    string? SessionId,
    bool KeyboardActive,
    bool MouseActive,
    bool UserActive,
    bool Idle,
    double IdleSeconds,
    string? FocusAppHash,
    [property: JsonPropertyName("freshness_raw")] string Freshness,
    string? DegradedReason,
    DateTimeOffset? FocusStartedAtUtc = null)
{
    public string? updated_at_utc => ToUtcMsStringOrNull(UpdatedAtUtc);
    public string? session_started_at_utc => ToUtcMsStringOrNull(SessionStartedAtUtc);
    public string? last_input_event_utc => ToUtcMsStringOrNull(LastInputEventUtc);
    public string? agent_instance_id => AgentInstanceId;
    public string? session_id => SessionId;
    public bool keyboard_active => KeyboardActive;
    public bool mouse_active => MouseActive;
    public bool user_active => UserActive;
    [JsonIgnore]
    public bool idle => Idle;
    public double idle_seconds => IdleSeconds;
    public string? focus_app_hash => FocusAppHash;
    public string? focus_started_at_utc => ToUtcMsStringOrNull(FocusStartedAtUtc);
    public string freshness_state => string.Equals(Freshness, "fresh", StringComparison.OrdinalIgnoreCase) ? "fresh" : "stale";
    public string? degraded_reason => DegradedReason;
    public double? session_elapsed_seconds => ComputeAgeSecondsFrom(SessionStartedAtUtc);
    public double? last_input_age_seconds => ComputeAgeSecondsFrom(LastInputEventUtc);
    // Deprecated: live-first timer bound to FocusStartedAtUtc (foreground-app session). Use FocusEpisodeSeconds from the snapshot instead.
    public double? focus_elapsed_seconds => ComputeAgeSecondsFrom(FocusStartedAtUtc);
    public LocalApiFreshnessView freshness => new(
        generated_at_utc: updated_at_utc,
        captured_at_utc: null,
        last_successful_db_refresh_utc: null,
        last_attempt_utc: updated_at_utc,
        freshness_state: freshness_state,
        degraded_reason: degraded_reason);

    private static double? ComputeAgeSecondsFrom(DateTimeOffset? utc)
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
    double pressure_level,
    double pressure_confidence,
    double confidence,
    double model_confidence,
    double? signal_quality_score,
    double behavior_continuity_score,
    double focus_episode_quality,
    double focus_episode_seconds,
    string? focus_episode_state,
    string attention_segment_kind,
    double attention_segment_seconds,
    bool distraction_episode_active,
    double distraction_episode_seconds,
    string attention_segment_reason_code,
    string attention_segment_reason_text,
    string focus_episode_reason_code,
    string focus_episode_reason_text,
    double sustained_attention_duration_sec,
    string? break_pending_action,
    string? break_pending_kind,
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

public sealed record LocalApiSetDeviceCredentialRequest(
    [property: JsonPropertyName("device_id")] string? DeviceId,
    [property: JsonPropertyName("device_secret")] string? DeviceSecret);

public sealed record LocalApiDeviceCredentialStatusResponse(
    [property: JsonPropertyName("present")] bool Present,
    [property: JsonPropertyName("device_id")] string? DeviceId,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc);

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
    [property: JsonPropertyName("what_changed")] string WhatChanged,
    [property: JsonPropertyName("interaction_kind")] string? InteractionKind = null,
    [property: JsonPropertyName("delivery_surface")] string? DeliverySurface = null,
    [property: JsonPropertyName("tip_key")] string? TipKey = null,
    [property: JsonPropertyName("break_kind")] string? BreakKind = null,
    [property: JsonPropertyName("countdown_seconds")] int? CountdownSeconds = null);

public sealed record LocalApiInterventionAckRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("intervention_id")] string? InterventionId,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("agent_instance_id")] string? AgentInstanceId,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("applied_at_utc")] DateTimeOffset? AppliedAtUtc,
    [property: JsonPropertyName("interaction_kind")] string? InteractionKind = null,
    [property: JsonPropertyName("delivery_surface")] string? DeliverySurface = null,
    [property: JsonPropertyName("break_kind")] string? BreakKind = null);

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
    [property: JsonPropertyName("notification_toasts_enabled")] bool? NotificationToastsEnabled = null,
    [property: JsonPropertyName("heartbeat_sequence")] long? HeartbeatSequence = null,
    [property: JsonPropertyName("heartbeat_updated_at_utc")] DateTimeOffset? HeartbeatUpdatedAtUtc = null,
    [property: JsonPropertyName("heartbeat_capture_context")] string? HeartbeatCaptureContext = null,
    // Category bridge (v1.2.112): metadata for the SAME hash carried in focus_app_hash,
    // resolved from the UserAgent's populated registries. The service's AppCategoryClassifier
    // registries are per-process and were permanently empty — every category-aware rule in the
    // brain (2f off-task gate, 2g suppression, 2b meeting shield) ran dark. Optional + additive:
    // an old client omitting these keeps exactly the old behavior (Unknown / not meeting).
    [property: JsonPropertyName("focus_app_category")] int FocusAppCategory = 0,
    [property: JsonPropertyName("focus_app_is_meeting")] bool FocusAppIsMeeting = false,
    [property: JsonPropertyName("focus_app_label")] string? FocusAppLabel = null);

public sealed record LocalApiAckResponse(
    bool Success,
    string Message,
    DateTimeOffset TimestampUtc);

// Phase 1b — Step 3b: sampled ground-truth prompts.
public sealed record LocalApiLabelPromptDueResponse(
    bool ShouldPrompt,
    string LabelTarget,
    string PromptKind,
    string ReasonCode,
    DateTimeOffset TimestampUtc);

// Phase 1c — signup nudge: whether an unlinked device is due to be nudged to sign in.
public sealed record LocalApiSignupNudgeDueResponse(
    [property: JsonPropertyName("should_nudge")] bool ShouldNudge,
    [property: JsonPropertyName("reason_code")] string ReasonCode,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc);

public sealed record LocalApiFocusLabelRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("label_target")] string? LabelTarget,
    [property: JsonPropertyName("prompt_kind")] string? PromptKind,
    [property: JsonPropertyName("answer")] string? Answer,
    [property: JsonPropertyName("response_latency_ms")] int ResponseLatencyMs);

// Phase 1b — Step 4: local readout of accumulated ground-truth labels (counts only, no content).
public sealed record LocalApiLabelDiagnosticsResponse(
    long TotalLabels,
    long LabelsToday,
    long LabelsThisWeek,
    IReadOnlyList<LocalApiLabelTargetCount> ByTarget,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiLabelTargetCount(
    string Target,
    long Count,
    long NetValue);

// Phase 2c — reminders (a plain, time-based alarm attached to the widget).
public sealed record LocalApiReminder(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("due_at_utc")] DateTimeOffset DueAtUtc,
    [property: JsonPropertyName("state")] string State,        // pending | fired
    [property: JsonPropertyName("snooze_count")] int SnoozeCount);

public sealed record LocalApiReminderCreateRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("due_at_utc")] DateTimeOffset DueAtUtc);

public sealed record LocalApiReminderAckRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("action")] string? Action);    // done | dismiss | snooze

public sealed record LocalApiReminderCreateResponse(
    bool Success,
    string? Reason,                                             // cap_reached when full
    LocalApiReminder? Reminder,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiNextReminderResponse(
    LocalApiReminder? Next,
    DateTimeOffset TimestampUtc);

// Workstream 4 — timed tasks (plan vs. reality).
public sealed record LocalApiTask(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("planned_seconds")] int PlannedSeconds,
    [property: JsonPropertyName("state")] string State,          // pending | active | done | abandoned
    [property: JsonPropertyName("active_seconds")] double ActiveSeconds,
    [property: JsonPropertyName("focused_seconds")] double FocusedSeconds,
    [property: JsonPropertyName("offtask_seconds")] double OfftaskSeconds,
    [property: JsonPropertyName("extended_seconds")] int ExtendedSeconds,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset? StartedAtUtc,
    [property: JsonPropertyName("ended_at_utc")] DateTimeOffset? EndedAtUtc,
    // Complaint 3 (2026-08-11) — the goal-day this task is next due back, set by the close-out
    // card's "Tomorrow". Empty = not deferred. Defaulted so older payloads deserialize unchanged.
    [property: JsonPropertyName("deferred_until_day")] string DeferredUntilDay = "",
    // Step 9a — the project this task belongs to. 0 = untagged, and the name is empty with it.
    // The name rides along so the widget, repainting every 4s, never needs a second call.
    // Defaulted so older payloads deserialize unchanged.
    [property: JsonPropertyName("project_id")] long ProjectId = 0,
    [property: JsonPropertyName("project_name")] string ProjectName = "");

public sealed record LocalApiTaskCreateRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("planned_seconds")] int PlannedSeconds,
    // Step 9a — 0 = untagged, which is the default and a permanent valid state.
    [property: JsonPropertyName("project_id")] long ProjectId = 0);

// Step 9a — projects. On a card a project is ONLY a name; anything richer lives in Control Center.
public sealed record LocalApiProject(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    // Step 10 phase A — the client-facing name, the per-project label default, and the user-side
    // rate. This travels on localhost only; none of it is on any payload that leaves the machine,
    // and the rate must never be.
    [property: JsonPropertyName("client_name")] string ClientName = "",
    [property: JsonPropertyName("use_client_labels")] bool UseClientLabels = false,
    [property: JsonPropertyName("rate_minor")] long RateMinor = 0,
    [property: JsonPropertyName("rate_currency")] string RateCurrency = "");

// Step 10 phase A — naming one line for a client. The raw title is never sent here and never
// changes; this sets the label beside it.
public sealed record LocalApiTaskClientLabelRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("task_id")] long TaskId,
    [property: JsonPropertyName("client_label")] string? ClientLabel);

// Phase 1.1 + 1.3 (2026-08-17) — the reporting fields on a task. Every field is nullable and a
// null leaves that column alone, so the same request sets a result line on its own, a status on
// its own, or all of them together from the report draft.
public sealed record LocalApiTaskReportFieldsRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("task_id")] long TaskId,
    // planned | in_progress | done | blocked. Anything else is refused rather than stored.
    [property: JsonPropertyName("status")] string? Status,
    // What came of it, in one line, written by the user. The report body is built from these.
    [property: JsonPropertyName("result_line")] string? ResultLine,
    [property: JsonPropertyName("blocked_reason")] string? BlockedReason,
    [property: JsonPropertyName("expected_finish_day")] string? ExpectedFinishDay);

// Step 10 phase A — the project's client-facing settings. Each field is applied only when present,
// so setting a rate cannot accidentally clear a client name.
public sealed record LocalApiProjectClientRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("client_name")] string? ClientName,
    [property: JsonPropertyName("use_client_labels")] bool? UseClientLabels,
    [property: JsonPropertyName("rate_minor")] long? RateMinor,
    [property: JsonPropertyName("rate_currency")] string? RateCurrency);

// Step 10 phase B — the report as it stands. DERIVED on every request and never stored: the stored
// thing is the snapshot taken when it is sent.
public sealed record LocalApiReportLine(
    [property: JsonPropertyName("task_id")] long TaskId,
    // The raw title, for the user's own screen so they can tell the lines apart. Never leaves.
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("client_label")] string ClientLabel,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("hours")] double Hours,
    // User-side diagnostic: measured time on this task with no identifiable application, so it is
    // NOT in the hours above. Never rendered into anything a client sees.
    //
    // Read unattributed_measurable FIRST: when it is false this is 0 meaning "not known", never
    // "measured as zero". The gap is a property of the task's whole life, not of this range.
    [property: JsonPropertyName("unattributed_hours")] double UnattributedHours,
    [property: JsonPropertyName("unattributed_measurable")] bool UnattributedMeasurable,
    // Phase 2b — the fields the draft screen has to SHOW before send (condition 2) and the payload
    // has to carry. Local DTO: this crosses localhost only.
    [property: JsonPropertyName("status")] string Status = "planned",
    [property: JsonPropertyName("dates")] string Dates = "",
    [property: JsonPropertyName("result_line")] string ResultLine = "",
    [property: JsonPropertyName("blocked_reason")] string BlockedReason = "",
    [property: JsonPropertyName("expected_finish_day")] string ExpectedFinishDay = "",
    [property: JsonPropertyName("colour_slot")] int ColourSlot = 0);

// Step 10 phase C/D — the address book. LOCAL ONLY: these travel over localhost and never leave the
// machine. Only the one address on the report being sent is ever uploaded.
public sealed record LocalApiReportRecipient(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("last_used_utc")] string LastUsedUtc);

public sealed record LocalApiReportRecipientsResponse(
    [property: JsonPropertyName("recipients")] IReadOnlyList<LocalApiReportRecipient> Recipients);

public sealed record LocalApiReportRecipientRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("email")] string? Email);

// The confirm step asks for this before it can decide what to show: who the user appears to be, and
// whether this client has been sent to before (which is what makes the preview mandatory or not).
public sealed record LocalApiSendContext(
    [property: JsonPropertyName("from_name")] string FromName,
    [property: JsonPropertyName("from_address")] string FromAddress,
    [property: JsonPropertyName("reply_to")] string ReplyTo,
    [property: JsonPropertyName("identity_resolved")] bool IdentityResolved,
    [property: JsonPropertyName("preview_required")] bool PreviewRequired);

public sealed record LocalApiReportSendRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("project_id")] long ProjectId,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("recipient_email")] string? RecipientEmail,
    [property: JsonPropertyName("recipient_name")] string? RecipientName,
    // Overrides typed on the confirm screen. Empty means "use the account profile as it stands".
    [property: JsonPropertyName("from_name")] string? FromName,
    [property: JsonPropertyName("reply_to")] string? ReplyTo,
    [property: JsonPropertyName("excluded_task_ids")] IReadOnlyList<long>? ExcludedTaskIds,
    // Proof the user looked at the rendered email. Mandatory for a first send to a new client.
    [property: JsonPropertyName("previewed")] bool Previewed,
    // Phase 2b — the summary the USER approved. Ari drafts it, the user edits or replaces it, and
    // whatever is here at send time is what goes out. Empty is valid and omits the section.
    [property: JsonPropertyName("summary")] string? Summary = null);

// Phase 1.4 / 2b — ask Ari for the summary paragraph. The agent assembles the facts from the draft
// and the control plane calls the model; nothing here is stored.
public sealed record LocalApiReportSummaryRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("project_id")] long ProjectId,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("excluded_task_ids")] IReadOnlyList<long>? ExcludedTaskIds);

public sealed record LocalApiReportSummaryResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("reason")] string? Reason);

public sealed record LocalApiReportSendResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("report_id")] string ReportId,
    // Refusal codes from ReportSendPreflight, so the UI supplies the sentences.
    [property: JsonPropertyName("refusals")] IReadOnlyList<string> Refusals,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("sent_at_utc")] string? SentAtUtc,
    // Phase 2b — the permanent link, so the app can offer copy-link rather than inventing the URL.
    [property: JsonPropertyName("report_url")] string? ReportUrl = null);

public sealed record LocalApiSentReportLine(
    [property: JsonPropertyName("line_id")] string LineId,
    [property: JsonPropertyName("client_label")] string ClientLabel,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("hours")] double Hours);

public sealed record LocalApiSentReport(
    [property: JsonPropertyName("report_id")] string ReportId,
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("range_start_local")] string RangeStartLocal,
    [property: JsonPropertyName("range_end_local")] string RangeEndLocal,
    [property: JsonPropertyName("recipient_name")] string RecipientName,
    [property: JsonPropertyName("recipient_email")] string RecipientEmail,
    [property: JsonPropertyName("from_name")] string FromName,
    [property: JsonPropertyName("reply_to")] string ReplyTo,
    [property: JsonPropertyName("total_hours")] double TotalHours,
    [property: JsonPropertyName("requested_at_utc")] string RequestedAtUtc,
    [property: JsonPropertyName("sent_at_utc")] string SentAtUtc,
    [property: JsonPropertyName("send_state")] string SendState,
    [property: JsonPropertyName("send_error")] string SendError,
    [property: JsonPropertyName("server_state")] string ServerState,
    [property: JsonPropertyName("lines")] IReadOnlyList<LocalApiSentReportLine> Lines);

public sealed record LocalApiSentReportsResponse(
    [property: JsonPropertyName("reports")] IReadOnlyList<LocalApiSentReport> Reports);

public sealed record LocalApiReportPreviewResponse(
    [property: JsonPropertyName("success")] bool Success,
    // THE HTML ITSELF, not a path to it. It used to be a path, and the agent wrote the file — but
    // the agent is a SERVICE running as LocalSystem, so Path.GetTempPath() there is
    // C:\Windows\SystemTemp, which Control Center (running as the user) cannot read or open. The
    // render succeeded, the file existed, and "Preview what they receive" failed every time with a
    // path the user could not open. Whoever is going to OPEN the file must be the one to WRITE it.
    [property: JsonPropertyName("html")] string? Html,
    [property: JsonPropertyName("reason")] string? Reason,
    // WHICH refusals, not just that there were some. The preflight already computed this list and
    // the preview response threw it away, so every local refusal — an unnamed project, an empty
    // From name, a bad Reply-to, five distinct causes — arrived at the UI as the single word
    // "refused" and printed one generic sentence. The send response has carried its refusals from
    // the start; this is the same field, on the route the user actually hits first.
    [property: JsonPropertyName("refusals")] IReadOnlyList<string>? Refusals = null);

public sealed record LocalApiReportDay(
    [property: JsonPropertyName("date_local")] string DateLocal,
    [property: JsonPropertyName("hours")] double Hours);

public sealed record LocalApiReportDraft(
    [property: JsonPropertyName("project_id")] long ProjectId,
    [property: JsonPropertyName("project_name")] string ProjectName,
    [property: JsonPropertyName("client_name")] string ClientName,
    [property: JsonPropertyName("range_start_local")] string RangeStartLocal,
    [property: JsonPropertyName("range_end_local")] string RangeEndLocal,
    [property: JsonPropertyName("total_hours")] double TotalHours,
    [property: JsonPropertyName("days_worked")] int DaysWorked,
    [property: JsonPropertyName("lines_finished")] int LinesFinished,
    [property: JsonPropertyName("lines")] IReadOnlyList<LocalApiReportLine> Lines,
    [property: JsonPropertyName("days")] IReadOnlyList<LocalApiReportDay> Days);

public sealed record LocalApiReportDraftResponse(
    bool Success,
    string? Reason,
    LocalApiReportDraft? Draft,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiProjectsResponse(
    IReadOnlyList<LocalApiProject> Projects,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiProjectCreateRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("name")] string? Name);

public sealed record LocalApiProjectResponse(
    bool Success,
    string? Reason,                                              // cap_reached_or_invalid / name_taken
    LocalApiProject? Project,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiProjectUpdateRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,           // rename when present
    [property: JsonPropertyName("archived")] bool? Archived);    // archive/un-archive when present

// Step 9a — re-tagging an existing task. project_id 0 clears the tag: untagged must always be
// reachable, because it is a choice and not an error.
public sealed record LocalApiTaskProjectRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("task_id")] long TaskId,
    [property: JsonPropertyName("project_id")] long ProjectId);

public sealed record LocalApiTaskCreateResponse(
    bool Success,
    string? Reason,                                              // cap_reached / invalid_payload_or_schema
    LocalApiTask? Task,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiTaskActRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("action")] string? Action);     // start | done | abandon | extend

public sealed record LocalApiTaskActResponse(
    bool Success,
    LocalApiTask? Task,
    DateTimeOffset TimestampUtc);

public sealed record LocalApiTasksTodayResponse(
    IReadOnlyList<LocalApiTask> Tasks,
    DateTimeOffset TimestampUtc);

// Workstream D — conversational check-in goals.
public sealed record LocalApiDailyGoal(
    [property: JsonPropertyName("local_date")] string LocalDate,
    [property: JsonPropertyName("goal_text")] string GoalText,
    [property: JsonPropertyName("tracking_pref")] string TrackingPref,
    [property: JsonPropertyName("midday_answer")] string MiddayAnswer,        // yes | partly | not_yet | ""
    [property: JsonPropertyName("plan_tomorrow")] string PlanTomorrow,
    [property: JsonPropertyName("created_at_utc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("midday_at_utc")] DateTimeOffset? MiddayAtUtc,
    [property: JsonPropertyName("eod_at_utc")] DateTimeOffset? EndOfDayAtUtc);

public sealed record LocalApiGoalMorningRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("goal_text")] string? GoalText,
    [property: JsonPropertyName("tracking_pref")] string? TrackingPref,       // remind_me | keep_on_track | just_track
    // Step 3 — how many tasks this morning check-in recorded, so the ground-truth label count
    // reflects the commitments made. Absent on an older payload, which the controller reads as 1.
    [property: JsonPropertyName("task_count")] int TaskCount = 1);

public sealed record LocalApiGoalMiddayRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("answer")] string? Answer);                   // yes | partly | not_yet

public sealed record LocalApiGoalEndOfDayRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("plan_tomorrow")] string? PlanTomorrow);

public sealed record LocalApiDailyGoalResponse(
    bool Success,
    LocalApiDailyGoal? Goal,
    DateTimeOffset TimestampUtc,
    // Part 3 — yesterday's plan_tomorrow, present only on goals/today while today has no stated
    // goal, so the morning check-in opens with it instead of a blank ask.
    [property: JsonPropertyName("carried_plan_tomorrow")] string? CarriedPlanTomorrow = null);

public sealed record LocalApiExportResponse(
    bool Success,
    string BundlePath,
    DateTimeOffset TimestampUtc);

// Ari user context — served by GET /v1/session/ari-context
public sealed record LocalApiAriContextResponse(
    string? FirstName,
    int? WeakHour,
    int? AvgStreakMin,
    int? StrongWindowStart,
    int? StrongWindowEnd,
    int DaysOfData = 0)
{
    public string? StrongWindowLabel => StrongWindowStart.HasValue && StrongWindowEnd.HasValue
        ? $"{FormatHour(StrongWindowStart.Value)}–{FormatHour(StrongWindowEnd.Value)}"
        : null;

    private static string FormatHour(int h) =>
        h == 0 ? "12am" : h < 12 ? $"{h}am" : h == 12 ? "12pm" : $"{h - 12}pm";
}

public interface IAriContextProvider
{
    LocalApiAriContextResponse GetContext();
}

// Part 5 — live check-in copy. The user agent asks the local API for one narrative line per
// high-value moment; the service forwards to the control plane (where the LLM + key live) and
// fails soft to null so the card can render its honest template instead. Kind is one of:
// morning | midday | eod | reply_morning | reply_midday | reply_eod.
public sealed record LocalApiNarrativeRequest(
    [property: JsonPropertyName("schema_ver")] int SchemaVer,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("user_text")] string? UserText = null);

public sealed record LocalApiNarrativeResponse(
    bool Success,
    [property: JsonPropertyName("line")] string? Line,
    DateTimeOffset TimestampUtc,
    // Why there is no line. The whole path is fail-soft by design — four layers each turn a
    // failure into a bare null — which made a missing line indistinguishable from a model that
    // declined. The agent service has no persistent log (it only writes to Console.Error, which
    // goes nowhere for a Windows service), so the reason has to ride back here to be recorded in
    // user-agent.log. Empty when a line was produced.
    [property: JsonPropertyName("reason")] string Reason = "");

/// <summary>
/// Served by the agent service (which owns the control-plane channel). Null = use the template;
/// implementations must never throw and must respect the caller's cancellation budget.
/// </summary>
/// <summary>
/// Step 10 phase C/D — the seam between the local API and the control plane for client reports.
///
/// The same shape as <see cref="INarrativeGateway"/> and for the same reason: these are the only
/// local routes that do not end at local SQLite. Sending lives on the server because the Resend key
/// must never reach this machine (spec §6a).
///
/// Implementations must never throw. A failure is a reason code.
/// </summary>
public interface IReportGateway
{
    /// <summary>Render what the client would receive. Sends nothing.</summary>
    Task<ReportGatewayPreview> PreviewAsync(string reportJson, CancellationToken token);

    /// <summary>
    /// Send it. <see cref="ReportGatewayOutcome.Unknown"/> means the outcome could not be
    /// established — the request may have been acted on before the connection dropped — and the
    /// caller must NOT present that as a failure, because a failure invites a retry and a retry
    /// could put a second copy in a client's inbox.
    /// </summary>
    Task<ReportGatewayOutcome> SendAsync(string reportJson, CancellationToken token);

    /// <summary>Delete the server's copy. False means it is still there.</summary>
    Task<bool> DeleteAsync(string reportId, CancellationToken token);

    /// <summary>
    /// Phase 1.4 — ask Ari to draft the summary. Null on ANY failure: a draft that cannot be
    /// written must leave the user typing their own, never block the flow.
    /// </summary>
    Task<string?> DraftSummaryAsync(string factsJson, CancellationToken token);

    /// <summary>Who this machine's account appears to be, for the confirm screen.</summary>
    Task<ReportGatewayIdentity> ReadIdentityAsync(CancellationToken token);
}

public sealed record ReportGatewayPreview(bool Success, string? Html, string? Subject, string? Reason);

// ReportUrl is the permanent link, returned so the app can offer copy-link without inventing it.
public sealed record ReportGatewayOutcome(bool Sent, bool Unknown, string? SentAtUtc, string? Reason, string? ReportUrl = null);

public sealed record ReportGatewayIdentity(
    string AccountId,
    string DeviceId,
    string MachineId,
    string FromName,
    string ReplyTo,
    bool Resolved);

public interface INarrativeGateway
{
    Task<string?> GetNarrativeLineAsync(string kind, string? userText, CancellationToken token);

    /// <summary>
    /// Same call, but also reports WHY there is no line. Implementations must never throw; a
    /// failure is a reason code, not an exception. Reason is empty when a line was produced.
    /// </summary>
    Task<NarrativeGatewayResult> GetNarrativeLineWithReasonAsync(
        string kind,
        string? userText,
        CancellationToken token);
}

/// <summary>A narrative line, or the reason there isn't one.</summary>
public readonly record struct NarrativeGatewayResult(string? Line, string Reason)
{
    public static NarrativeGatewayResult Ok(string line) => new(line, string.Empty);

    public static NarrativeGatewayResult Failed(string reason) => new(null, reason);
}
