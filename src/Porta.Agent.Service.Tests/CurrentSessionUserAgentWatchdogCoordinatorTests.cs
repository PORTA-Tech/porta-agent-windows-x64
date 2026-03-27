using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Porta.Agent.Common.UserAgent;

namespace Porta.Agent.Service.Tests;

public sealed class CurrentSessionUserAgentWatchdogCoordinatorTests
{
    [Fact]
    public void EnsureHealthy_LaunchesWhenInteractiveSessionIsMissingUserAgent()
    {
        var runtime = new FakeRuntime
        {
            InteractiveSessionId = 4,
            HasUserAgentBinary = true,
            RunningProcesses = []
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance);

        coordinator.EnsureHealthy(force: true);

        Assert.Single(runtime.LaunchAttempts);
        Assert.Equal((4, "missing_process"), runtime.LaunchAttempts[0]);
        Assert.Empty(runtime.TerminatedSessions);
    }

    [Fact]
    public void EnsureHealthy_DoesNothingWhenHeartbeatIsHealthy()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var runtime = new FakeRuntime
        {
            UtcNowValue = nowUtc,
            InteractiveSessionId = 6,
            HasUserAgentBinary = true,
            RunningProcesses =
            [
                new RunningUserAgentProcessSnapshot(1111, 6, nowUtc.AddSeconds(-20))
            ],
            SessionContext = new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 1111,
                SessionId: 6,
                UpdatedAtUtc: nowUtc.AddSeconds(-3),
                AgentInstanceId: "healthy-agent")
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance);

        coordinator.EnsureHealthy(force: true);

        Assert.Empty(runtime.LaunchAttempts);
        Assert.Empty(runtime.TerminatedSessions);
    }

    [Fact]
    public void EnsureHealthy_RestartsWhenHeartbeatIsStale()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var runtime = new FakeRuntime
        {
            UtcNowValue = nowUtc,
            InteractiveSessionId = 8,
            HasUserAgentBinary = true,
            RunningProcesses =
            [
                new RunningUserAgentProcessSnapshot(2222, 8, nowUtc.AddMinutes(-3))
            ],
            SessionContext = new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 2222,
                SessionId: 8,
                UpdatedAtUtc: nowUtc.AddMinutes(-2),
                AgentInstanceId: "stale-agent")
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance);

        coordinator.EnsureHealthy(force: true);

        Assert.Equal([8], runtime.TerminatedSessions);
        Assert.Single(runtime.LaunchAttempts);
        Assert.Equal((8, "stale_heartbeat"), runtime.LaunchAttempts[0]);
    }

    [Fact]
    public void EnsureHealthy_DoesNothingWhenNoInteractiveSessionExists()
    {
        var runtime = new FakeRuntime
        {
            InteractiveSessionId = null,
            HasUserAgentBinary = true
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance);

        coordinator.EnsureHealthy(force: true);

        Assert.Empty(runtime.LaunchAttempts);
        Assert.Empty(runtime.TerminatedSessions);
    }

    [Fact]
    public void EnsureHealthy_LaunchesIntoNewInteractiveSession_WhenPreviousSessionProcessStillExists()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var runtime = new FakeRuntime
        {
            UtcNowValue = nowUtc,
            InteractiveSessionId = 9,
            HasUserAgentBinary = true,
            RunningProcesses =
            [
                new RunningUserAgentProcessSnapshot(3333, 3, nowUtc.AddMinutes(-10))
            ],
            SessionContext = new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: UserAgentCaptureContexts.UserSession,
                ProcessId: 3333,
                SessionId: 3,
                UpdatedAtUtc: nowUtc.AddSeconds(-4),
                AgentInstanceId: "previous-session-agent")
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance);

        coordinator.EnsureHealthy(force: true);

        Assert.Equal([(9, "missing_process")], runtime.LaunchAttempts);
        Assert.Empty(runtime.TerminatedSessions);
    }

    private sealed class FakeRuntime : ICurrentSessionUserAgentRuntime
    {
        public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UtcNow;

        public int? InteractiveSessionId { get; set; }

        public bool HasUserAgentBinary { get; set; }

        public IReadOnlyList<RunningUserAgentProcessSnapshot> RunningProcesses { get; set; } = [];

        public UserAgentSessionContextSnapshot? SessionContext { get; set; }

        public List<(int SessionId, string LaunchReason)> LaunchAttempts { get; } = [];

        public List<int> TerminatedSessions { get; } = [];

        DateTimeOffset ICurrentSessionUserAgentRuntime.UtcNow => UtcNowValue;

        public bool TryGetInteractiveSessionId(out int sessionId)
        {
            if (InteractiveSessionId.HasValue)
            {
                sessionId = InteractiveSessionId.Value;
                return true;
            }

            sessionId = -1;
            return false;
        }

        public IReadOnlyList<RunningUserAgentProcessSnapshot> EnumerateRunningProcesses() => RunningProcesses;

        public UserAgentSessionContextSnapshot? ReadSessionContext() => SessionContext;

        public int TerminateProcessesInSession(int sessionId, ILogger logger)
        {
            TerminatedSessions.Add(sessionId);
            RunningProcesses = RunningProcesses.Where(process => process.SessionId != sessionId).ToArray();
            return 1;
        }

        public bool HasRunningProcessInSession(int sessionId)
        {
            return RunningProcesses.Any(process => process.SessionId == sessionId);
        }

        public bool TryLaunchInSession(int sessionId, string launchReason, ILogger logger)
        {
            LaunchAttempts.Add((sessionId, launchReason));
            return true;
        }
    }
}
