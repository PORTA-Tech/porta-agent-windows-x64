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

    private static DeepWorkTransitionContext BuildContext(bool previousActive, bool currentActive)
    {
        return new DeepWorkTransitionContext(
            BucketSeq: 99,
            BucketEndUtc: DateTimeOffset.UtcNow,
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
