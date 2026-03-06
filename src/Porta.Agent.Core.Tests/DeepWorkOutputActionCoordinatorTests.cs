using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

public sealed class DeepWorkOutputActionCoordinatorTests
{
    [Fact]
    public void HandleDeepWorkTransition_NoStateChange_DoesNotTouchController()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);

        DeepWorkActionOutcome outcome = coordinator.HandleDeepWorkTransition(new DeepWorkTransitionContext(
            BucketSeq: 1,
            BucketEndUtc: DateTimeOffset.UtcNow,
            PreviousDeepWorkActive: false,
            CurrentDeepWorkActive: false,
            BaselineReady: true,
            LowQualityPaused: false,
            Confidence: 0.8d));

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.SuppressionAttempted);
        Assert.False(outcome.RestoreAttempted);
        Assert.Equal(0, fake.GetSnapshotCalls);
        Assert.Equal(0, fake.ApplyCalls);
        Assert.Equal(0, fake.RestoreCalls);
    }

    [Fact]
    public void HandleDeepWorkTransition_Entering_UsesNotificationController()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);

        DeepWorkActionOutcome outcome = coordinator.HandleDeepWorkTransition(new DeepWorkTransitionContext(
            BucketSeq: 2,
            BucketEndUtc: DateTimeOffset.UtcNow,
            PreviousDeepWorkActive: false,
            CurrentDeepWorkActive: true,
            BaselineReady: true,
            LowQualityPaused: false,
            Confidence: 0.9d));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.EnteringDeepWork);
        Assert.True(outcome.SuppressionAttempted);
        Assert.Equal(1, fake.GetSnapshotCalls);
        Assert.Equal(1, fake.ApplyCalls);
        Assert.Equal(0, fake.RestoreCalls);
    }

    [Fact]
    public void HandleDeepWorkTransition_Exiting_RestoresPreviousSnapshot()
    {
        var fake = new FakeNotificationController();
        var coordinator = new DeepWorkOutputActionCoordinator(fake);

        _ = coordinator.HandleDeepWorkTransition(new DeepWorkTransitionContext(
            BucketSeq: 2,
            BucketEndUtc: DateTimeOffset.UtcNow,
            PreviousDeepWorkActive: false,
            CurrentDeepWorkActive: true,
            BaselineReady: true,
            LowQualityPaused: false,
            Confidence: 0.9d));

        DeepWorkActionOutcome outcome = coordinator.HandleDeepWorkTransition(new DeepWorkTransitionContext(
            BucketSeq: 3,
            BucketEndUtc: DateTimeOffset.UtcNow,
            PreviousDeepWorkActive: true,
            CurrentDeepWorkActive: false,
            BaselineReady: true,
            LowQualityPaused: false,
            Confidence: 0.9d));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.ExitingDeepWork);
        Assert.True(outcome.RestoreAttempted);
        Assert.Equal(1, fake.RestoreCalls);
    }

    private sealed class FakeNotificationController : INotificationController
    {
        public int GetSnapshotCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public NotificationControllerSnapshot GetSnapshot()
        {
            GetSnapshotCalls++;
            return new NotificationControllerSnapshot(
                SuppressionEnabled: false,
                Provider: "fake",
                RawStateJson: "{}");
        }

        public void ApplySuppression(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            ApplyCalls++;
        }

        public void Restore(NotificationControllerSnapshot snapshot)
        {
            _ = snapshot;
            RestoreCalls++;
        }
    }
}
