using System.Text.Json.Serialization;

namespace Porta.Agent.ValidationRunner.Fleet;

internal sealed record FleetValidationOptions(
    string ControlPlaneBaseUrl,
    string OperatorId,
    string OperatorRole,
    string MachineId,
    string UserAgentPath,
    bool AllowProcessControl,
    string? VerifyPersistenceCheckpointPath,
    string? IdentityUserId,
    string? IdentityPassword,
    string? IdentityOrgId,
    bool AllowIdentityRelink,
    bool SkipIdentityRolloutGates);

internal sealed record MachineSummaryRow(
    string? DeviceId,
    string? AccountId,
    string? OrgId,
    string MachineId,
    string MachineName,
    string UserName,
    string? DisplayName,
    string? PrimaryEmail,
    string? OrgName,
    string? Role,
    string? DeviceStatus,
    string? DeviceRevokedAt,
    string ReportedAtUtc,
    bool AgentRunning,
    bool UserSessionConnected,
    bool ActivityDetected,
    string? LastActivityUtc,
    string? UserAgentHeartbeatUtc,
    double? UserAgentHeartbeatAgeSeconds,
    double? LastAcceptedInputAgeSeconds,
    string? UserAgentStartupSource,
    string? UserAgentStartupState,
    string? UserAgentLastLaunchReason,
    int? UserAgentHeartbeatWriteFailureCount,
    string CaptureContext,
    bool KeyboardSignalActive,
    bool MouseSignalActive,
    string? OwnershipWarning,
    string? CaptureFailureReason,
    string? LastError,
    bool BackendConnected,
    bool LocalApiAuthReady,
    bool DbMigrationFailed,
    double TaskContinuityScore,
    double TaskContinuitySeconds,
    string ContinuityState,
    bool DeepWorkActive,
    double OverloadLikelihood,
    bool OverloadActive,
    bool BreakNeeded,
    string? BreakPendingAction,
    string? BreakPendingReasonCode,
    string CalibrationStage,
    string TrustTier,
    double ModelConfidence,
    double? SignalQualityScore,
    bool PartialData,
    IReadOnlyList<string> MissingMajorChannels);

internal sealed record MachineActionRow(
    string MachineId,
    string UserName,
    string InterventionId,
    string Action,
    string ActionCategory,
    string DecisionState,
    string ExecutionState,
    string VerifiedState,
    string TimestampUtc,
    string? AppliedAtUtc,
    string? ErrorCode,
    string PolicyId,
    string? WhatChanged,
    string? HowToUndo);

internal sealed record FleetSystemHealthResponse(
    [property: JsonPropertyName("fleet_reporting")] FleetReportingHealth? FleetReporting);

internal sealed record FleetReportingHealth(
    [property: JsonPropertyName("persistence_mode")] string? PersistenceMode,
    [property: JsonPropertyName("database_configured")] bool DatabaseConfigured);

internal sealed record FleetValidationCheckpoint(
    string MachineId,
    string ReportedAtUtc,
    string? BreakInterventionId,
    string? NotificationInterventionId,
    string CreatedAtUtc);

internal sealed record FleetValidationResult(
    bool Success,
    string Name,
    string Detail);

internal sealed record ControlPlaneLoginResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("user")] ControlPlaneSessionUser User,
    [property: JsonPropertyName("session_expires_at_utc")] DateTimeOffset SessionExpiresAtUtc,
    [property: JsonPropertyName("reauth_expires_at_utc")] DateTimeOffset ReauthExpiresAtUtc);

internal sealed record ControlPlaneSessionUser(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("display_name")] string DisplayName);

internal sealed record ControlPlaneMeProfile(
    [property: JsonPropertyName("account")] ControlPlaneAccountProfile Account,
    [property: JsonPropertyName("organizations")] IReadOnlyList<ControlPlaneMembershipView> Organizations,
    [property: JsonPropertyName("devices")] IReadOnlyList<ControlPlaneDeviceProfile> Devices);

internal sealed record ControlPlaneAccountProfile(
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("primaryEmail")] string PrimaryEmail);

internal sealed record ControlPlaneMembershipView(
    [property: JsonPropertyName("membership")] ControlPlaneMembership Membership,
    [property: JsonPropertyName("organization")] ControlPlaneOrganizationProfile? Organization);

internal sealed record ControlPlaneMembership(
    [property: JsonPropertyName("membershipId")] string MembershipId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status);

internal sealed record ControlPlaneOrganizationProfile(
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("orgName")] string OrgName,
    [property: JsonPropertyName("planTier")] string PlanTier);

internal sealed record ControlPlaneDeviceProfile(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("machineName")] string MachineName,
    [property: JsonPropertyName("deviceDisplayName")] string DeviceDisplayName,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("agentVersion")] string AgentVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("firstSeenAt")] DateTimeOffset FirstSeenAt,
    [property: JsonPropertyName("lastSeenAt")] DateTimeOffset LastSeenAt,
    [property: JsonPropertyName("currentAccountId")] string? CurrentAccountId,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

internal sealed record DeviceRegistrationRequestRow(
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

internal sealed record DeviceAccountLinkRequestRow(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("accountId")] string AccountId,
    [property: JsonPropertyName("orgId")] string OrgId,
    [property: JsonPropertyName("sessionToken")] string? SessionToken,
    [property: JsonPropertyName("localProofToken")] string? LocalProofToken);

internal sealed record DeviceLinkResultRow(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("linkState")] string LinkState,
    [property: JsonPropertyName("device")] ControlPlaneDeviceProfile Device);

internal sealed record RolloutValidationGatePayload(
    [property: JsonPropertyName("gateId")] string GateId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("checkedAtUtc")] string CheckedAtUtc);

internal sealed record RolloutValidationReportPayload(
    [property: JsonPropertyName("suiteName")] string SuiteName,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("accountId")] string? AccountId,
    [property: JsonPropertyName("orgId")] string? OrgId,
    [property: JsonPropertyName("generatedAtUtc")] string GeneratedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string? ExpiresAtUtc,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("gates")] IReadOnlyList<RolloutValidationGatePayload> Gates);

internal sealed record ControlPlaneRolloutGuardrail(
    [property: JsonPropertyName("guardrail_id")] string GuardrailId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("value_label")] string ValueLabel,
    [property: JsonPropertyName("threshold")] double? Threshold,
    [property: JsonPropertyName("threshold_label")] string ThresholdLabel,
    [property: JsonPropertyName("window")] string Window);

internal sealed record ControlPlaneRolloutDecision(
    [property: JsonPropertyName("promotion_verdict")] string PromotionVerdict,
    [property: JsonPropertyName("trigger_reason_code")] string TriggerReasonCode);

internal sealed record ControlPlaneRolloutEvaluationPayload(
    [property: JsonPropertyName("guardrail_eval")] IReadOnlyList<ControlPlaneRolloutGuardrail> GuardrailEval,
    [property: JsonPropertyName("decision")] ControlPlaneRolloutDecision Decision);
