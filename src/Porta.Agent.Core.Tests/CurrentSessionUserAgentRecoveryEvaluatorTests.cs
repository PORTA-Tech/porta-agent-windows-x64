using Porta.Agent.Common.UserAgent;

namespace Porta.Agent.Core.Tests;

public sealed class CurrentSessionUserAgentRecoveryEvaluatorTests
{
    [Fact]
    public void Evaluate_Launches_WhenNoSessionProcessExists()
    {
        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 3,
            processes: [],
            sessionContext: null,
            nowUtc: DateTimeOffset.Parse("2026-03-20T08:00:00Z"));

        Assert.Equal(CurrentSessionUserAgentAction.LaunchNew, decision.Action);
        Assert.Equal("missing_process", decision.Reason);
    }

    [Fact]
    public void Evaluate_KeepsExisting_WhenHeartbeatMatchesFreshSessionProcess()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-20T08:00:00Z");

        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 2,
            processes:
            [
                new RunningUserAgentProcessSnapshot(4012, 2, nowUtc.AddMinutes(-5))
            ],
            sessionContext: new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 4012,
                SessionId: 2,
                UpdatedAtUtc: nowUtc.AddSeconds(-4),
                AgentInstanceId: "useragent-4012"),
            nowUtc: nowUtc);

        Assert.Equal(CurrentSessionUserAgentAction.KeepExisting, decision.Action);
        Assert.Equal("healthy", decision.Reason);
    }

    [Fact]
    public void Evaluate_Waits_WhenProcessJustStartedAndHeartbeatHasNotLandedYet()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-20T08:00:00Z");

        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 5,
            processes:
            [
                new RunningUserAgentProcessSnapshot(9021, 5, nowUtc.AddSeconds(-3))
            ],
            sessionContext: null,
            nowUtc: nowUtc);

        Assert.Equal(CurrentSessionUserAgentAction.WaitForHeartbeat, decision.Action);
        Assert.Equal("awaiting_heartbeat", decision.Reason);
    }

    [Fact]
    public void Evaluate_Restarts_WhenHeartbeatIsStale()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-20T08:00:00Z");

        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 4,
            processes:
            [
                new RunningUserAgentProcessSnapshot(6111, 4, nowUtc.AddHours(-12))
            ],
            sessionContext: new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 6111,
                SessionId: 4,
                UpdatedAtUtc: nowUtc.AddMinutes(-40),
                AgentInstanceId: "useragent-6111"),
            nowUtc: nowUtc);

        Assert.Equal(CurrentSessionUserAgentAction.RestartExisting, decision.Action);
        Assert.Equal("stale_heartbeat", decision.Reason);
    }

    [Fact]
    public void Evaluate_Restarts_WhenHeartbeatBelongsToDifferentSessionProcess()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-20T08:00:00Z");

        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 7,
            processes:
            [
                new RunningUserAgentProcessSnapshot(7111, 7, nowUtc.AddMinutes(-20))
            ],
            sessionContext: new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 7001,
                SessionId: 7,
                UpdatedAtUtc: nowUtc.AddSeconds(-5),
                AgentInstanceId: "useragent-7001"),
            nowUtc: nowUtc);

        Assert.Equal(CurrentSessionUserAgentAction.RestartExisting, decision.Action);
        Assert.Equal("process_mismatch", decision.Reason);
    }

    [Fact]
    public void Evaluate_LaunchesNewProcess_WhenInteractiveSessionHasSwitched()
    {
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-20T08:00:00Z");

        CurrentSessionUserAgentDecision decision = CurrentSessionUserAgentRecoveryEvaluator.Evaluate(
            currentSessionId: 9,
            processes:
            [
                new RunningUserAgentProcessSnapshot(8111, 3, nowUtc.AddMinutes(-15))
            ],
            sessionContext: new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 8111,
                SessionId: 3,
                UpdatedAtUtc: nowUtc.AddSeconds(-4),
                AgentInstanceId: "useragent-8111"),
            nowUtc: nowUtc);

        Assert.Equal(CurrentSessionUserAgentAction.LaunchNew, decision.Action);
        Assert.Equal("missing_process", decision.Reason);
    }
}
