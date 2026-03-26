using Porta.UserAgent;

namespace Porta.UserAgent.Tests;

public sealed class CaptureSelfTestCoordinatorTests
{
    [Fact]
    public void Evaluate_RequestsHookReset_WhenNoRawInputArrivesAfterObservedUserActivity()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var coordinator = new CaptureSelfTestCoordinator();
        coordinator.Arm("startup", nowUtc);
        coordinator.RecordHeartbeatPublished(nowUtc);

        coordinator.Evaluate(nowUtc, heartbeatFresh: true, hooksAvailable: true, idleSeconds: 1d);
        CaptureSelfTestDecision decision = coordinator.Evaluate(
            nowUtc.AddSeconds(5),
            heartbeatFresh: true,
            hooksAvailable: true,
            idleSeconds: 5d);

        Assert.True(decision.RequestHookReset);
        Assert.Equal(CaptureSelfTestStates.Recovering, decision.State);
        Assert.Equal("no_live_input_after_user_activity", decision.FailureCode);
    }

    [Fact]
    public void Evaluate_RequestsImmediateFlush_WhenRawInputArrivesButPublishHasNotBeenConfirmed()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var coordinator = new CaptureSelfTestCoordinator();
        coordinator.Arm("startup", nowUtc);
        coordinator.RecordHeartbeatPublished(nowUtc);

        coordinator.Evaluate(nowUtc, heartbeatFresh: true, hooksAvailable: true, idleSeconds: 1d);
        coordinator.RecordRawInput(nowUtc.AddSeconds(1));

        CaptureSelfTestDecision decision = coordinator.Evaluate(
            nowUtc.AddSeconds(2.5),
            heartbeatFresh: true,
            hooksAvailable: true,
            idleSeconds: 2.5d);

        Assert.True(decision.RequestImmediateFlush);
        Assert.False(decision.RequestHookReset);
        Assert.Equal(CaptureSelfTestStates.Pending, decision.State);
    }

    [Fact]
    public void Evaluate_NotifiesFailure_AfterExhaustingRecoveryAttempts()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var coordinator = new CaptureSelfTestCoordinator();
        coordinator.Arm("startup", nowUtc);
        coordinator.RecordHeartbeatPublished(nowUtc);

        coordinator.Evaluate(nowUtc, heartbeatFresh: true, hooksAvailable: true, idleSeconds: 1d);
        CaptureSelfTestDecision attemptOne = coordinator.Evaluate(nowUtc.AddSeconds(5), true, true, 5d);

        coordinator.RecordHeartbeatPublished(nowUtc.AddSeconds(6));
        coordinator.Evaluate(nowUtc.AddSeconds(6), true, true, 1d);
        CaptureSelfTestDecision attemptTwo = coordinator.Evaluate(nowUtc.AddSeconds(11), true, true, 5d);

        coordinator.RecordHeartbeatPublished(nowUtc.AddSeconds(12));
        coordinator.Evaluate(nowUtc.AddSeconds(12), true, true, 1d);
        CaptureSelfTestDecision failed = coordinator.Evaluate(nowUtc.AddSeconds(17), true, true, 5d);

        Assert.True(attemptOne.RequestHookReset);
        Assert.True(attemptTwo.RequestHookReset);
        Assert.False(failed.RequestHookReset);
        Assert.True(failed.NotifyUserFailure);
        Assert.Equal(CaptureSelfTestStates.Failed, failed.State);
        Assert.Equal("no_live_input_after_user_activity", failed.FailureCode);
    }

    [Fact]
    public void Evaluate_ReturnsHealthy_WhenPublishConfirmationArrivesAfterObservedInput()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var coordinator = new CaptureSelfTestCoordinator();
        coordinator.Arm("startup", nowUtc);
        coordinator.RecordHeartbeatPublished(nowUtc);

        coordinator.Evaluate(nowUtc, heartbeatFresh: true, hooksAvailable: true, idleSeconds: 1d);
        coordinator.RecordRawInput(nowUtc.AddSeconds(1));
        coordinator.RecordPublishAccepted(nowUtc.AddSeconds(2));

        CaptureSelfTestDecision decision = coordinator.Evaluate(
            nowUtc.AddSeconds(2),
            heartbeatFresh: true,
            hooksAvailable: true,
            idleSeconds: 2d);
        CaptureSelfTestHeartbeatProjection projection = coordinator.GetHeartbeatProjection();

        Assert.Equal(CaptureSelfTestStates.Healthy, decision.State);
        Assert.Equal("capture_self_test_passed", decision.LogMessage);
        Assert.Null(projection.State);
        Assert.Null(projection.ErrorCode);
    }
}
