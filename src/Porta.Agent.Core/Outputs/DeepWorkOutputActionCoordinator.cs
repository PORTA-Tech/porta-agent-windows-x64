namespace Porta.Agent.Core.Outputs;

public readonly record struct DeepWorkTransitionContext(
    long BucketSeq,
    DateTimeOffset BucketEndUtc,
    bool PreviousDeepWorkActive,
    bool CurrentDeepWorkActive,
    bool BaselineReady,
    bool LowQualityPaused,
    double Confidence);

public readonly record struct NotificationSuppressionRuntimeState(
    int SchemaVer,
    bool Active,
    NotificationControllerSnapshot? Snapshot,
    string SnapshotCapturedAtUtc,
    string SuppressedAtUtc,
    string RestoredAtUtc,
    long EnableCooldownUntilUtcMs,
    long MinOnUntilUtcMs,
    string LastErrorCode,
    long BackoffUntilUtcMs,
    long LastTransitionSourceBucketSeq,
    bool RestorePending,
    string ActionState)
{
    public static NotificationSuppressionRuntimeState CreateDefault()
    {
        return new NotificationSuppressionRuntimeState(
            SchemaVer: 1,
            Active: false,
            Snapshot: null,
            SnapshotCapturedAtUtc: string.Empty,
            SuppressedAtUtc: string.Empty,
            RestoredAtUtc: string.Empty,
            EnableCooldownUntilUtcMs: 0,
            MinOnUntilUtcMs: 0,
            LastErrorCode: string.Empty,
            BackoffUntilUtcMs: 0,
            LastTransitionSourceBucketSeq: 0,
            RestorePending: false,
            ActionState: "inactive");
    }
}

public readonly record struct DeepWorkActionOutcome(
    bool EnteringDeepWork,
    bool ExitingDeepWork,
    bool SuppressionAttempted,
    bool RestoreAttempted,
    bool Succeeded,
    string ErrorCode,
    string ErrorDetail,
    bool StateChanged,
    string OutcomeReason);

public readonly record struct DeepWorkTransitionExecution(
    DeepWorkActionOutcome Outcome,
    NotificationSuppressionRuntimeState RuntimeState);

public readonly record struct NotificationSuppressionStartupReconciliation(
    NotificationSuppressionRuntimeState RuntimeState,
    bool ShouldLogDiagnosticEvent,
    string DiagnosticEventAction,
    string ErrorCode,
    string ErrorDetail,
    string OutcomeReason);

public interface IDeepWorkOutputActionCoordinator
{
    DeepWorkTransitionExecution HandleDeepWorkTransition(
        DeepWorkTransitionContext context,
        NotificationSuppressionRuntimeState runtimeState);

    NotificationSuppressionStartupReconciliation ReconcileOnStartup(
        NotificationSuppressionRuntimeState runtimeState,
        DateTimeOffset nowUtc);
}

public sealed class DeepWorkOutputActionCoordinator : IDeepWorkOutputActionCoordinator
{
    private readonly INotificationController _notificationController;

    public DeepWorkOutputActionCoordinator(INotificationController notificationController)
    {
        _notificationController = notificationController ?? throw new ArgumentNullException(nameof(notificationController));
    }

    public DeepWorkTransitionExecution HandleDeepWorkTransition(
        DeepWorkTransitionContext context,
        NotificationSuppressionRuntimeState runtimeState)
    {
        NotificationSuppressionRuntimeState state = NormalizeRuntimeState(runtimeState);
        long nowUtcMs = context.BucketEndUtc.ToUnixTimeMilliseconds();
        bool enteringTransition = !context.PreviousDeepWorkActive && context.CurrentDeepWorkActive;

        if (enteringTransition)
        {
            if (state.Active)
            {
                return NoOp(state, "already_active");
            }

            if (nowUtcMs < state.EnableCooldownUntilUtcMs)
            {
                return NoOp(state, "enable_cooldown_active");
            }

            if (nowUtcMs < state.BackoffUntilUtcMs)
            {
                return NoOp(state with { ActionState = "backoff_wait" }, "backoff_active");
            }

            NotificationControllerSnapshotResult snapshotResult = _notificationController.GetSnapshot();
            if (!snapshotResult.Succeeded)
            {
                return BuildFailureExecution(
                    state,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: false,
                    restoreAttempted: false,
                    errorCode: string.IsNullOrWhiteSpace(snapshotResult.ErrorCode)
                        ? "snapshot_capture_failed"
                        : snapshotResult.ErrorCode,
                    errorDetail: snapshotResult.ErrorDetail,
                    actionState: "inactive");
            }

            if (!snapshotResult.Snapshot.IsValid)
            {
                return BuildFailureExecution(
                    state,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: false,
                    restoreAttempted: false,
                    errorCode: "snapshot_invalid",
                    errorDetail: "Notification snapshot is invalid and cannot be used for exact restore.",
                    actionState: "inactive");
            }

            NotificationControllerActionResult applyResult = _notificationController.ApplySuppression(snapshotResult.Snapshot);
            if (!applyResult.Succeeded)
            {
                return BuildFailureExecution(
                    state,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: true,
                    restoreAttempted: false,
                    errorCode: string.IsNullOrWhiteSpace(applyResult.ErrorCode)
                        ? "suppression_apply_failed"
                        : applyResult.ErrorCode,
                    errorDetail: applyResult.ErrorDetail,
                    actionState: "inactive");
            }

            long minOnUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.NotificationSuppression.MinOnSeconds * 1000L));
            long enableCooldownUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.NotificationSuppression.EnableCooldownSeconds * 1000L));
            NotificationSuppressionRuntimeState updatedState = state with
            {
                SchemaVer = 1,
                Active = true,
                Snapshot = snapshotResult.Snapshot,
                SnapshotCapturedAtUtc = snapshotResult.Snapshot.CapturedAtUtc.ToString("O"),
                SuppressedAtUtc = context.BucketEndUtc.ToString("O"),
                RestoredAtUtc = string.Empty,
                EnableCooldownUntilUtcMs = enableCooldownUntilUtcMs,
                MinOnUntilUtcMs = minOnUntilUtcMs,
                LastErrorCode = string.Empty,
                BackoffUntilUtcMs = 0,
                LastTransitionSourceBucketSeq = context.BucketSeq,
                RestorePending = false,
                ActionState = "active"
            };
            return new DeepWorkTransitionExecution(
                Outcome: new DeepWorkActionOutcome(
                    EnteringDeepWork: true,
                    ExitingDeepWork: false,
                    SuppressionAttempted: true,
                    RestoreAttempted: false,
                    Succeeded: true,
                    ErrorCode: string.Empty,
                    ErrorDetail: string.Empty,
                    StateChanged: applyResult.StateChanged,
                    OutcomeReason: "suppressed"),
                RuntimeState: updatedState);
        }

        bool shouldAttemptRestore = state.Active && !context.CurrentDeepWorkActive;
        if (!shouldAttemptRestore)
        {
            return NoOp(state, "no_transition");
        }

        if (nowUtcMs < state.MinOnUntilUtcMs)
        {
            NotificationSuppressionRuntimeState pendingState = state with
            {
                RestorePending = true,
                ActionState = "restore_pending"
            };
            return NoOp(pendingState, "min_on_not_elapsed");
        }

        if (nowUtcMs < state.BackoffUntilUtcMs)
        {
            string actionState = string.IsNullOrWhiteSpace(state.LastErrorCode)
                ? "restore_pending"
                : "active_with_restore_error";
            return NoOp(
                state with
                {
                    RestorePending = true,
                    ActionState = actionState
                },
                "backoff_active");
        }

        NotificationControllerSnapshot? snapshot = state.Snapshot;
        if (!snapshot.HasValue || !snapshot.Value.IsValid)
        {
            return BuildFailureExecution(
                state with
                {
                    RestorePending = true,
                    ActionState = "active_with_restore_error"
                },
                context,
                entering: false,
                exiting: true,
                suppressionAttempted: false,
                restoreAttempted: false,
                errorCode: "snapshot_missing_or_invalid",
                errorDetail: "Cannot restore notifications exactly because persisted snapshot is missing or invalid.",
                actionState: "active_with_restore_error");
        }

        NotificationControllerActionResult restoreResult = _notificationController.Restore(snapshot.Value);
        if (!restoreResult.Succeeded)
        {
            return BuildFailureExecution(
                state with
                {
                    RestorePending = true,
                    ActionState = "active_with_restore_error"
                },
                context,
                entering: false,
                exiting: true,
                suppressionAttempted: false,
                restoreAttempted: true,
                errorCode: string.IsNullOrWhiteSpace(restoreResult.ErrorCode)
                    ? "suppression_restore_failed"
                    : restoreResult.ErrorCode,
                errorDetail: restoreResult.ErrorDetail,
                actionState: "active_with_restore_error");
        }

        NotificationSuppressionRuntimeState restoredState = state with
        {
            SchemaVer = 1,
            Active = false,
            Snapshot = null,
            SnapshotCapturedAtUtc = state.SnapshotCapturedAtUtc,
            RestoredAtUtc = context.BucketEndUtc.ToString("O"),
            MinOnUntilUtcMs = 0,
            LastErrorCode = string.Empty,
            BackoffUntilUtcMs = 0,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            RestorePending = false,
            ActionState = "inactive"
        };
        return new DeepWorkTransitionExecution(
            Outcome: new DeepWorkActionOutcome(
                EnteringDeepWork: false,
                ExitingDeepWork: true,
                SuppressionAttempted: false,
                RestoreAttempted: true,
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: restoreResult.StateChanged,
                OutcomeReason: "restored"),
            RuntimeState: restoredState);
    }

    private static DeepWorkTransitionExecution NoOp(NotificationSuppressionRuntimeState state, string reason)
    {
        return new DeepWorkTransitionExecution(
            Outcome: new DeepWorkActionOutcome(
                EnteringDeepWork: false,
                ExitingDeepWork: false,
                SuppressionAttempted: false,
                RestoreAttempted: false,
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                StateChanged: false,
                OutcomeReason: reason),
            RuntimeState: state);
    }

    private static NotificationSuppressionRuntimeState NormalizeRuntimeState(NotificationSuppressionRuntimeState runtimeState)
    {
        string actionState = string.IsNullOrWhiteSpace(runtimeState.ActionState)
            ? (runtimeState.Active ? "active" : "inactive")
            : runtimeState.ActionState;
        return new NotificationSuppressionRuntimeState(
            SchemaVer: runtimeState.SchemaVer <= 0 ? 1 : runtimeState.SchemaVer,
            Active: runtimeState.Active,
            Snapshot: runtimeState.Snapshot,
            SnapshotCapturedAtUtc: runtimeState.SnapshotCapturedAtUtc ?? string.Empty,
            SuppressedAtUtc: runtimeState.SuppressedAtUtc ?? string.Empty,
            RestoredAtUtc: runtimeState.RestoredAtUtc ?? string.Empty,
            EnableCooldownUntilUtcMs: Math.Max(0, runtimeState.EnableCooldownUntilUtcMs),
            MinOnUntilUtcMs: Math.Max(0, runtimeState.MinOnUntilUtcMs),
            LastErrorCode: runtimeState.LastErrorCode ?? string.Empty,
            BackoffUntilUtcMs: Math.Max(0, runtimeState.BackoffUntilUtcMs),
            LastTransitionSourceBucketSeq: Math.Max(0, runtimeState.LastTransitionSourceBucketSeq),
            RestorePending: runtimeState.RestorePending,
            ActionState: actionState);
    }

    private static DeepWorkTransitionExecution BuildFailureExecution(
        NotificationSuppressionRuntimeState runtimeState,
        DeepWorkTransitionContext context,
        bool entering,
        bool exiting,
        bool suppressionAttempted,
        bool restoreAttempted,
        string errorCode,
        string errorDetail,
        string actionState)
    {
        long nowUtcMs = context.BucketEndUtc.ToUnixTimeMilliseconds();
        long backoffUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds * 1000L));
        string normalizedErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "unknown_notification_error" : errorCode;
        NotificationSuppressionRuntimeState failedState = runtimeState with
        {
            SchemaVer = 1,
            Active = runtimeState.Active,
            LastErrorCode = normalizedErrorCode,
            BackoffUntilUtcMs = backoffUntilUtcMs,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            ActionState = string.IsNullOrWhiteSpace(actionState) ? runtimeState.ActionState : actionState
        };
        return new DeepWorkTransitionExecution(
            Outcome: new DeepWorkActionOutcome(
                EnteringDeepWork: entering,
                ExitingDeepWork: exiting,
                SuppressionAttempted: suppressionAttempted,
                RestoreAttempted: restoreAttempted,
                Succeeded: false,
                ErrorCode: normalizedErrorCode,
                ErrorDetail: errorDetail ?? string.Empty,
                StateChanged: false,
                OutcomeReason: "failure"),
            RuntimeState: failedState);
    }

    public NotificationSuppressionStartupReconciliation ReconcileOnStartup(
        NotificationSuppressionRuntimeState runtimeState,
        DateTimeOffset nowUtc)
    {
        NotificationSuppressionRuntimeState state = NormalizeRuntimeState(runtimeState);
        if (!state.Active)
        {
            return new NotificationSuppressionStartupReconciliation(
                RuntimeState: state with
                {
                    RestorePending = false,
                    ActionState = "inactive"
                },
                ShouldLogDiagnosticEvent: false,
                DiagnosticEventAction: string.Empty,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                OutcomeReason: "startup_inactive");
        }

        if (!state.Snapshot.HasValue || !state.Snapshot.Value.IsValid)
        {
            NotificationSuppressionRuntimeState corrupted = state with
            {
                LastErrorCode = "startup_snapshot_missing_or_invalid",
                ActionState = "active_with_restore_error",
                RestorePending = true,
                BackoffUntilUtcMs = Math.Max(
                    state.BackoffUntilUtcMs,
                    checked(nowUtc.ToUnixTimeMilliseconds() + (PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds * 1000L)))
            };
            return new NotificationSuppressionStartupReconciliation(
                RuntimeState: corrupted,
                ShouldLogDiagnosticEvent: true,
                DiagnosticEventAction: "notification_suppress_state_corrupt",
                ErrorCode: corrupted.LastErrorCode,
                ErrorDetail: "runtime_state.active=true but snapshot is missing/corrupt; restore cannot be attempted safely.",
                OutcomeReason: "startup_corrupt_state");
        }

        NotificationControllerSnapshotResult currentSnapshotResult = _notificationController.GetSnapshot();
        if (!currentSnapshotResult.Succeeded)
        {
            NotificationSuppressionRuntimeState degraded = state with
            {
                LastErrorCode = string.IsNullOrWhiteSpace(currentSnapshotResult.ErrorCode)
                    ? "startup_reconcile_snapshot_failed"
                    : currentSnapshotResult.ErrorCode,
                ActionState = "startup_reconcile_failed",
                RestorePending = state.RestorePending,
                BackoffUntilUtcMs = Math.Max(
                    state.BackoffUntilUtcMs,
                    checked(nowUtc.ToUnixTimeMilliseconds() + (PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds * 1000L)))
            };
            return new NotificationSuppressionStartupReconciliation(
                RuntimeState: degraded,
                ShouldLogDiagnosticEvent: true,
                DiagnosticEventAction: "notification_suppress_state_reconcile_failed",
                ErrorCode: degraded.LastErrorCode,
                ErrorDetail: currentSnapshotResult.ErrorDetail,
                OutcomeReason: "startup_snapshot_query_failed");
        }

        bool osSuppressed = IsSuppressed(currentSnapshotResult.Snapshot);
        if (osSuppressed)
        {
            NotificationSuppressionRuntimeState confirmed = state with
            {
                LastErrorCode = string.Empty,
                BackoffUntilUtcMs = 0,
                ActionState = "active"
            };
            return new NotificationSuppressionStartupReconciliation(
                RuntimeState: confirmed,
                ShouldLogDiagnosticEvent: false,
                DiagnosticEventAction: string.Empty,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                OutcomeReason: "startup_active_confirmed");
        }

        NotificationSuppressionRuntimeState discrepancy = state with
        {
            Active = false,
            RestorePending = false,
            ActionState = "startup_discrepancy_cleared",
            LastErrorCode = "startup_discrepancy_os_not_suppressed",
            BackoffUntilUtcMs = Math.Max(
                state.BackoffUntilUtcMs,
                checked(nowUtc.ToUnixTimeMilliseconds() + (PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds * 1000L))),
            RestoredAtUtc = nowUtc.ToString("O")
        };
        return new NotificationSuppressionStartupReconciliation(
            RuntimeState: discrepancy,
            ShouldLogDiagnosticEvent: true,
            DiagnosticEventAction: "notification_suppress_state_discrepancy",
            ErrorCode: discrepancy.LastErrorCode,
            ErrorDetail: "runtime_state indicated suppressed, but OS state is currently not suppressed.",
            OutcomeReason: "startup_discrepancy_cleared");
    }

    private static bool IsSuppressed(NotificationControllerSnapshot snapshot)
    {
        if (!snapshot.IsValid)
        {
            return false;
        }

        if (snapshot.QuietHoursEnabled)
        {
            return true;
        }

        if (!snapshot.ToastBannersEnabled)
        {
            return true;
        }

        string mode = (snapshot.FocusAssistMode ?? string.Empty).Trim().ToLowerInvariant();
        return mode switch
        {
            "priority_only" => true,
            "alarms_only" => true,
            "on" => true,
            _ => false
        };
    }
}
