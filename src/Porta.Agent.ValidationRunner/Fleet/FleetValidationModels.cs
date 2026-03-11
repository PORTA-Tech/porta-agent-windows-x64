using System.Text.Json.Serialization;

namespace Porta.Agent.ValidationRunner.Fleet;

internal sealed record FleetValidationOptions(
    string ControlPlaneBaseUrl,
    string OperatorId,
    string OperatorRole,
    string MachineId,
    string UserAgentPath,
    bool AllowProcessControl,
    string? VerifyPersistenceCheckpointPath);

internal sealed record MachineSummaryRow(
    string MachineId,
    string MachineName,
    string UserName,
    string ReportedAtUtc,
    bool AgentRunning,
    bool UserSessionConnected,
    bool ActivityDetected,
    string? LastActivityUtc,
    string? UserAgentHeartbeatUtc,
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
