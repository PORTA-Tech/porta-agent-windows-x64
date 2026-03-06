using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class DeepWorkOutputActionCoordinatorTests
{
    [Fact]
    public void HandleDeepWorkTransition_NoStateChange_DoesNotTouchController()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault();

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: false),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.False(execution.Outcome.SuppressionAttempted);
        Assert.False(execution.Outcome.RestoreAttempted);
        Assert.Equal(0, fake.GetSnapshotCalls);
        Assert.Equal(0, fake.ApplyCalls);
        Assert.Equal(0, fake.RestoreCalls);
        Assert.False(execution.RuntimeState.Active);
    }

    [Fact]
    public void HandleDeepWorkTransition_Entering_UsesSnapshotAndSuppression()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault();

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: true),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.True(execution.Outcome.EnteringDeepWork);
        Assert.True(execution.Outcome.SuppressionAttempted);
        Assert.Equal(1, fake.GetSnapshotCalls);
        Assert.Equal(1, fake.ApplyCalls);
        Assert.Equal(0, fake.RestoreCalls);
        Assert.True(execution.RuntimeState.Active);
        Assert.NotNull(execution.RuntimeState.Snapshot);
        Assert.Equal(string.Empty, execution.RuntimeState.LastErrorCode);
    }

    [Fact]
    public void HandleDeepWorkTransition_Exiting_RestoresFromPersistedSnapshot()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot snapshot = fake.BuildValidSnapshot();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = snapshot,
            SnapshotCapturedAtUtc = snapshot.CapturedAtUtc.ToString("O"),
            SuppressedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O")
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.True(execution.Outcome.ExitingDeepWork);
        Assert.True(execution.Outcome.RestoreAttempted);
        Assert.Equal(0, fake.GetSnapshotCalls);
        Assert.Equal(0, fake.ApplyCalls);
        Assert.Equal(1, fake.RestoreCalls);
        Assert.False(execution.RuntimeState.Active);
        Assert.Equal(string.Empty, execution.RuntimeState.LastErrorCode);
    }

    [Fact]
    public void HandleDeepWorkTransition_ExitingAfterMinOnElapsed_Restores()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot snapshot = fake.BuildValidSnapshot();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = snapshot,
            MinOnUntilUtcMs = nowMs - 1
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.True(execution.Outcome.RestoreAttempted);
        Assert.Equal("restored", execution.Outcome.OutcomeReason);
        Assert.False(execution.RuntimeState.Active);
    }

    [Fact]
    public void HandleDeepWorkTransition_ExitingWithoutSnapshot_ReturnsTypedFailure()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = null
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.False(execution.Outcome.Succeeded);
        Assert.Equal("snapshot_missing_or_invalid", execution.Outcome.ErrorCode);
        Assert.Equal(0, fake.RestoreCalls);
        Assert.True(execution.RuntimeState.BackoffUntilUtcMs > 0);
    }

    [Fact]
    public void HandleDeepWorkTransition_EnteringInvalidSnapshot_ReturnsTypedFailure()
    {
        var fake = new FakeNotificationController
        {
            SnapshotOverride = new NotificationControllerSnapshot(
                SchemaVer: 1,
                ContractVersion: "v1",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                IsValid: false,
                Provider: "fake",
                FocusAssistMode: "off",
                QuietHoursEnabled: false,
                ToastBannersEnabled: true,
                RawStateJson: "{}")
        };
        var coordinator = new DeepWorkOutputActionCoordinator(fake);

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: true),
            NotificationSuppressionRuntimeState.CreateDefault());

        Assert.False(execution.Outcome.Succeeded);
        Assert.Equal("snapshot_invalid", execution.Outcome.ErrorCode);
        Assert.Equal(1, fake.GetSnapshotCalls);
        Assert.Equal(0, fake.ApplyCalls);
    }

    [Fact]
    public void HandleDeepWorkTransition_EnteringWhileEnableCooldownActive_DoesNotAttemptSuppression()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            EnableCooldownUntilUtcMs = nowMs + 60_000
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: true),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("enable_cooldown_active", execution.Outcome.OutcomeReason);
        Assert.Equal(0, fake.GetSnapshotCalls);
        Assert.Equal(0, fake.ApplyCalls);
    }

    [Fact]
    public void HandleDeepWorkTransition_ExitBeforeMinOn_LeavesSuppressionActiveAndPendingRestore()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot snapshot = fake.BuildValidSnapshot();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = snapshot,
            MinOnUntilUtcMs = nowMs + 30_000
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.True(execution.Outcome.Succeeded);
        Assert.Equal("min_on_not_elapsed", execution.Outcome.OutcomeReason);
        Assert.False(execution.Outcome.RestoreAttempted);
        Assert.Equal(0, fake.RestoreCalls);
        Assert.True(execution.RuntimeState.Active);
        Assert.True(execution.RuntimeState.RestorePending);
        Assert.Equal("restore_pending", execution.RuntimeState.ActionState);
    }

    [Fact]
    public void HandleDeepWorkTransition_RestoreFailure_MarksActiveWithRestoreErrorAndBackoff()
    {
        var fake = new FakeNotificationController
        {
            RestoreResult = new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "RESTORE_API_FAILED",
                ErrorDetail: "unit test restore failure",
                StateChanged: false)
        };
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot snapshot = fake.BuildValidSnapshot();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = snapshot,
            MinOnUntilUtcMs = 0
        };

        DeepWorkTransitionExecution execution = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: true, currentActive: false),
            state);

        Assert.False(execution.Outcome.Succeeded);
        Assert.Equal("RESTORE_API_FAILED", execution.Outcome.ErrorCode);
        Assert.Equal(1, fake.RestoreCalls);
        Assert.True(execution.RuntimeState.Active);
        Assert.True(execution.RuntimeState.RestorePending);
        Assert.Equal("active_with_restore_error", execution.RuntimeState.ActionState);
        Assert.True(execution.RuntimeState.BackoffUntilUtcMs > 0);
    }

    [Fact]
    public void HandleDeepWorkTransition_ApplyFailure_SetsBackoffAndSkipsRetryDuringBackoff()
    {
        var fake = new FakeNotificationController
        {
            ApplyResult = new NotificationControllerActionResult(
                Succeeded: false,
                ErrorCode: "APPLY_FAILED",
                ErrorDetail: "unit test apply failure",
                StateChanged: false)
        };
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        DeepWorkTransitionExecution first = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: true, nowUtc: nowUtc),
            NotificationSuppressionRuntimeState.CreateDefault());

        Assert.False(first.Outcome.Succeeded);
        Assert.Equal("APPLY_FAILED", first.Outcome.ErrorCode);
        Assert.True(first.RuntimeState.BackoffUntilUtcMs > nowUtc.ToUnixTimeMilliseconds());
        Assert.Equal(1, fake.GetSnapshotCalls);
        Assert.Equal(1, fake.ApplyCalls);

        DeepWorkTransitionExecution second = coordinator.HandleDeepWorkTransition(
            BuildContext(previousActive: false, currentActive: true, nowUtc: nowUtc.AddSeconds(30)),
            first.RuntimeState);

        Assert.True(second.Outcome.Succeeded);
        Assert.Equal("backoff_active", second.Outcome.OutcomeReason);
        Assert.Equal(1, fake.GetSnapshotCalls);
        Assert.Equal(1, fake.ApplyCalls);
    }

    [Fact]
    public void ReconcileOnStartup_InactiveState_NoActionNeeded()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);

        NotificationSuppressionStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(
            NotificationSuppressionRuntimeState.CreateDefault(),
            DateTimeOffset.UtcNow);

        Assert.False(reconciliation.ShouldLogDiagnosticEvent);
        Assert.Equal("startup_inactive", reconciliation.OutcomeReason);
        Assert.False(reconciliation.RuntimeState.Active);
        Assert.Equal(0, fake.GetSnapshotCalls);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveWithValidSnapshotAndSuppressedOs_KeepsActive()
    {
        NotificationControllerSnapshot osSuppressedSnapshot = new(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            IsValid: true,
            Provider: "fake",
            FocusAssistMode: "priority_only",
            QuietHoursEnabled: true,
            ToastBannersEnabled: false,
            RawStateJson: "{}");
        var fake = new FakeNotificationController
        {
            SnapshotResultOverride = new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: osSuppressedSnapshot)
        };
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot persistedSnapshot = fake.BuildValidSnapshot();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = persistedSnapshot
        };

        NotificationSuppressionStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(
            state,
            DateTimeOffset.UtcNow);

        Assert.False(reconciliation.ShouldLogDiagnosticEvent);
        Assert.Equal("startup_active_confirmed", reconciliation.OutcomeReason);
        Assert.True(reconciliation.RuntimeState.Active);
        Assert.Equal(1, fake.GetSnapshotCalls);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveWithMissingSnapshot_FlagsCorruption()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = null
        };

        NotificationSuppressionStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(
            state,
            DateTimeOffset.UtcNow);

        Assert.True(reconciliation.ShouldLogDiagnosticEvent);
        Assert.Equal("notification_suppress_state_corrupt", reconciliation.DiagnosticEventAction);
        Assert.Equal("startup_snapshot_missing_or_invalid", reconciliation.ErrorCode);
        Assert.True(reconciliation.RuntimeState.Active);
        Assert.True(reconciliation.RuntimeState.RestorePending);
    }

    [Fact]
    public void ReconcileOnStartup_ActiveSnapshotButOsNotSuppressed_ClearsActiveSafely()
    {
        NotificationControllerSnapshot osNotSuppressedSnapshot = new(
            SchemaVer: 1,
            ContractVersion: "v1",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            IsValid: true,
            Provider: "fake",
            FocusAssistMode: "off",
            QuietHoursEnabled: false,
            ToastBannersEnabled: true,
            RawStateJson: "{}");
        var fake = new FakeNotificationController
        {
            SnapshotResultOverride = new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: osNotSuppressedSnapshot)
        };
        var coordinator = new DeepWorkOutputActionCoordinator(fake);
        NotificationControllerSnapshot persistedSnapshot = fake.BuildValidSnapshot();
        NotificationSuppressionRuntimeState state = NotificationSuppressionRuntimeState.CreateDefault() with
        {
            Active = true,
            Snapshot = persistedSnapshot
        };

        NotificationSuppressionStartupReconciliation reconciliation = coordinator.ReconcileOnStartup(
            state,
            DateTimeOffset.UtcNow);

        Assert.True(reconciliation.ShouldLogDiagnosticEvent);
        Assert.Equal("notification_suppress_state_discrepancy", reconciliation.DiagnosticEventAction);
        Assert.Equal("startup_discrepancy_os_not_suppressed", reconciliation.ErrorCode);
        Assert.False(reconciliation.RuntimeState.Active);
        Assert.Equal("startup_discrepancy_cleared", reconciliation.RuntimeState.ActionState);
    }

    private static DeepWorkTransitionContext BuildContext(bool previousActive, bool currentActive, DateTimeOffset? nowUtc = null)
    {
        return new DeepWorkTransitionContext(
            BucketSeq: 99,
            BucketEndUtc: nowUtc ?? DateTimeOffset.UtcNow,
            PreviousDeepWorkActive: previousActive,
            CurrentDeepWorkActive: currentActive,
            BaselineReady: true,
            LowQualityPaused: false,
            Confidence: 0.82d);
    }

    private sealed class FakeNotificationController : INotificationController
    {
        public int GetSnapshotCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public NotificationControllerSnapshot? SnapshotOverride { get; init; }
        public NotificationControllerSnapshotResult? SnapshotResultOverride { get; init; }
        public NotificationControllerActionResult ApplyResult { get; init; } = new(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: true);
        public NotificationControllerActionResult RestoreResult { get; init; } = new(
            Succeeded: true,
            ErrorCode: string.Empty,
            ErrorDetail: string.Empty,
            StateChanged: true);

        public NotificationControllerSnapshot BuildValidSnapshot()
        {
            return new NotificationControllerSnapshot(
                SchemaVer: 1,
                ContractVersion: "v1",
                CapturedAtUtc: DateTimeOffset.UtcNow,
                IsValid: true,
                Provider: "fake",
                FocusAssistMode: "off",
                QuietHoursEnabled: false,
                ToastBannersEnabled: true,
                RawStateJson: "{\"focusAssist\":\"off\"}");
        }

        public NotificationControllerSnapshotResult GetSnapshot()
        {
            GetSnapshotCalls++;
            if (SnapshotResultOverride.HasValue)
            {
                return SnapshotResultOverride.Value;
            }

            NotificationControllerSnapshot snapshot = SnapshotOverride ?? BuildValidSnapshot();
            return new NotificationControllerSnapshotResult(
                Succeeded: true,
                ErrorCode: string.Empty,
                ErrorDetail: string.Empty,
                Snapshot: snapshot);
        }

        public NotificationControllerActionResult ApplySuppression(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            ApplyCalls++;
            return ApplyResult;
        }

        public NotificationControllerActionResult Restore(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            RestoreCalls++;
            return RestoreResult;
        }
    }
}
