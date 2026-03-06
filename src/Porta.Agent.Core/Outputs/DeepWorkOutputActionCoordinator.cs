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
    long LastTransitionSourceBucketSeq)
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
            LastTransitionSourceBucketSeq: 0);
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
    bool StateChanged);

public readonly record struct DeepWorkTransitionExecution(
    DeepWorkActionOutcome Outcome,
    NotificationSuppressionRuntimeState RuntimeState);

public interface IDeepWorkOutputActionCoordinator
{
    DeepWorkTransitionExecution HandleDeepWorkTransition(
        DeepWorkTransitionContext context,
        NotificationSuppressionRuntimeState runtimeState);
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
        NotificationSuppressionRuntimeState normalizedState = NormalizeRuntimeState(runtimeState);
        bool entering = !context.PreviousDeepWorkActive && context.CurrentDeepWorkActive;
        bool exiting = context.PreviousDeepWorkActive && !context.CurrentDeepWorkActive;
        if (!entering && !exiting)
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
                    StateChanged: false),
                RuntimeState: normalizedState);
        }

        if (entering)
        {
            NotificationControllerSnapshotResult snapshotResult = _notificationController.GetSnapshot();
            if (!snapshotResult.Succeeded)
            {
                return BuildFailureExecution(
                    normalizedState,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: false,
                    restoreAttempted: false,
                    errorCode: string.IsNullOrWhiteSpace(snapshotResult.ErrorCode)
                        ? "snapshot_capture_failed"
                        : snapshotResult.ErrorCode,
                    errorDetail: snapshotResult.ErrorDetail);
            }

            if (!snapshotResult.Snapshot.IsValid)
            {
                return BuildFailureExecution(
                    normalizedState,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: false,
                    restoreAttempted: false,
                    errorCode: "snapshot_invalid",
                    errorDetail: "Notification snapshot is invalid and cannot be used for exact restore.");
            }

            NotificationControllerActionResult applyResult = _notificationController.ApplySuppression(snapshotResult.Snapshot);
            if (!applyResult.Succeeded)
            {
                return BuildFailureExecution(
                    normalizedState,
                    context,
                    entering: true,
                    exiting: false,
                    suppressionAttempted: true,
                    restoreAttempted: false,
                    errorCode: string.IsNullOrWhiteSpace(applyResult.ErrorCode)
                        ? "suppression_apply_failed"
                        : applyResult.ErrorCode,
                    errorDetail: applyResult.ErrorDetail);
            }

            NotificationSuppressionRuntimeState updatedState = normalizedState with
            {
                SchemaVer = 1,
                Active = true,
                Snapshot = snapshotResult.Snapshot,
                SnapshotCapturedAtUtc = snapshotResult.Snapshot.CapturedAtUtc.ToString("O"),
                SuppressedAtUtc = context.BucketEndUtc.ToString("O"),
                LastErrorCode = string.Empty,
                BackoffUntilUtcMs = 0,
                LastTransitionSourceBucketSeq = context.BucketSeq
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
                    StateChanged: applyResult.StateChanged),
                RuntimeState: updatedState);
        }

        NotificationControllerSnapshot? snapshot = normalizedState.Snapshot;
        if (!snapshot.HasValue || !snapshot.Value.IsValid)
        {
            return BuildFailureExecution(
                normalizedState,
                context,
                entering: false,
                exiting: true,
                suppressionAttempted: false,
                restoreAttempted: false,
                errorCode: "snapshot_missing_or_invalid",
                errorDetail: "Cannot restore notifications exactly because persisted snapshot is missing or invalid.");
        }

        NotificationControllerActionResult restoreResult = _notificationController.Restore(snapshot.Value);
        if (!restoreResult.Succeeded)
        {
            return BuildFailureExecution(
                normalizedState,
                context,
                entering: false,
                exiting: true,
                suppressionAttempted: false,
                restoreAttempted: true,
                errorCode: string.IsNullOrWhiteSpace(restoreResult.ErrorCode)
                    ? "suppression_restore_failed"
                    : restoreResult.ErrorCode,
                errorDetail: restoreResult.ErrorDetail);
        }

        NotificationSuppressionRuntimeState restoredState = normalizedState with
        {
            SchemaVer = 1,
            Active = false,
            RestoredAtUtc = context.BucketEndUtc.ToString("O"),
            LastErrorCode = string.Empty,
            BackoffUntilUtcMs = 0,
            LastTransitionSourceBucketSeq = context.BucketSeq
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
                StateChanged: restoreResult.StateChanged),
            RuntimeState: restoredState);
    }

    private static NotificationSuppressionRuntimeState NormalizeRuntimeState(NotificationSuppressionRuntimeState runtimeState)
    {
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
            LastTransitionSourceBucketSeq: Math.Max(0, runtimeState.LastTransitionSourceBucketSeq));
    }

    private static DeepWorkTransitionExecution BuildFailureExecution(
        NotificationSuppressionRuntimeState runtimeState,
        DeepWorkTransitionContext context,
        bool entering,
        bool exiting,
        bool suppressionAttempted,
        bool restoreAttempted,
        string errorCode,
        string errorDetail)
    {
        long nowUtcMs = context.BucketEndUtc.ToUnixTimeMilliseconds();
        long backoffUntilUtcMs = checked(nowUtcMs + (PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds * 1000L));
        NotificationSuppressionRuntimeState failedState = runtimeState with
        {
            SchemaVer = 1,
            LastErrorCode = string.IsNullOrWhiteSpace(errorCode) ? "unknown_notification_error" : errorCode,
            BackoffUntilUtcMs = backoffUntilUtcMs,
            LastTransitionSourceBucketSeq = context.BucketSeq
        };
        return new DeepWorkTransitionExecution(
            Outcome: new DeepWorkActionOutcome(
                EnteringDeepWork: entering,
                ExitingDeepWork: exiting,
                SuppressionAttempted: suppressionAttempted,
                RestoreAttempted: restoreAttempted,
                Succeeded: false,
                ErrorCode: failedState.LastErrorCode,
                ErrorDetail: errorDetail ?? string.Empty,
                StateChanged: false),
            RuntimeState: failedState);
    }
}
