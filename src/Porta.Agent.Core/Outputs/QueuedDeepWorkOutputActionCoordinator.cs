namespace Porta.Agent.Core.Outputs;

public sealed class QueuedDeepWorkOutputActionCoordinator : IDeepWorkOutputActionCoordinator
{
    public DeepWorkTransitionExecution HandleDeepWorkTransition(
        DeepWorkTransitionContext context,
        NotificationSuppressionRuntimeState runtimeState,
        RuntimeNotificationSuppressionSettings? policy = null)
    {
        _ = policy;
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
                return NoOp(state with { ActionState = "enable_cooldown_active" }, "enable_cooldown_active");
            }

            if (nowUtcMs < state.BackoffUntilUtcMs)
            {
                return NoOp(state with { ActionState = "backoff_wait" }, "backoff_active");
            }

            NotificationSuppressionRuntimeState queuedState = state with
            {
                SchemaVer = 1,
                RestorePending = false,
                LastTransitionSourceBucketSeq = context.BucketSeq,
                ActionState = "apply_pending"
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
                    StateChanged: false,
                    OutcomeReason: "queued"),
                RuntimeState: queuedState);
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
                LastTransitionSourceBucketSeq = context.BucketSeq,
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
                    LastTransitionSourceBucketSeq = context.BucketSeq,
                    ActionState = actionState
                },
                "backoff_active");
        }

        NotificationSuppressionRuntimeState restoreQueuedState = state with
        {
            RestorePending = true,
            LastTransitionSourceBucketSeq = context.BucketSeq,
            ActionState = "restore_pending"
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
                StateChanged: false,
                OutcomeReason: "queued"),
            RuntimeState: restoreQueuedState);
    }

    public NotificationSuppressionStartupReconciliation ReconcileOnStartup(
        NotificationSuppressionRuntimeState runtimeState,
        DateTimeOffset nowUtc,
        RuntimeNotificationSuppressionSettings? policy = null)
    {
        _ = nowUtc;
        _ = policy;
        NotificationSuppressionRuntimeState state = NormalizeRuntimeState(runtimeState);
        string actionState = state.Active
            ? (state.RestorePending ? "restore_pending" : "active")
            : "inactive";
        return new NotificationSuppressionStartupReconciliation(
            RuntimeState: state with
            {
                ActionState = actionState
            },
            ShouldLogDiagnosticEvent: false,
            DiagnosticEventAction: string.Empty,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            OutcomeReason: "startup_deferred_to_user_session");
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
}
