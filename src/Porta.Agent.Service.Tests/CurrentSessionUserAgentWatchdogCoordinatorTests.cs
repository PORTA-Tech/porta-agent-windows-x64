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
            RunningProcesses = [],
            ExpectedBinaryVersion = "1.0.21.0"
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

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
            ExpectedBinaryVersion = "1.0.21.0",
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
                AgentInstanceId: "healthy-agent",
                BinaryVersion: "1.0.21.0")
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

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
            ExpectedBinaryVersion = "1.0.21.0",
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
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

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
            HasUserAgentBinary = true,
            ExpectedBinaryVersion = "1.0.21.0"
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

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
            ExpectedBinaryVersion = "1.0.21.0",
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
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

        coordinator.EnsureHealthy(force: true);

        Assert.Equal([(9, "missing_process")], runtime.LaunchAttempts);
        Assert.Empty(runtime.TerminatedSessions);
    }

    [Fact]
    public void EnsureHealthy_RestartsWhenRunningBinaryVersionIsStale()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var runtime = new FakeRuntime
        {
            UtcNowValue = nowUtc,
            InteractiveSessionId = 6,
            HasUserAgentBinary = true,
            ExpectedBinaryVersion = "1.0.21.0",
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
                AgentInstanceId: "stale-agent",
                BinaryVersion: "1.0.20.0")
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

        coordinator.EnsureHealthy(force: true);

        Assert.Equal([6], runtime.TerminatedSessions);
        Assert.Single(runtime.LaunchAttempts);
        Assert.Equal((6, "version_mismatch"), runtime.LaunchAttempts[0]);
    }

    [Fact]
    public void EnsureHealthy_DoesNothingWhenWidgetIsUnavailableButHeartbeatIsFresh()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var runtime = new FakeRuntime
        {
            UtcNowValue = nowUtc,
            InteractiveSessionId = 6,
            HasUserAgentBinary = true,
            ExpectedBinaryVersion = "1.0.21.0",
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
                AgentInstanceId: "healthy-agent",
                BinaryVersion: "1.0.21.0",
                DesktopWidgetReady: false)
        };
        var coordinator = new CurrentSessionUserAgentWatchdogCoordinator(runtime, NullLogger.Instance, NullController);

        coordinator.EnsureHealthy(force: true);

        Assert.Empty(runtime.LaunchAttempts);
        Assert.Empty(runtime.TerminatedSessions);
    }

    private static readonly Porta.Agent.Core.IAgentCoreController NullController = new NullAgentCoreController();

    private sealed class NullAgentCoreController : Porta.Agent.Core.IAgentCoreController
    {
        public Porta.Agent.Core.AgentServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentLiveSessionState GetLiveSessionState() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentControlState GetControlState() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentBrainSnapshot GetCurrentSnapshot() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentLatestStateDetails? GetLatestStateDetails() => null;
        public Porta.Agent.Core.AgentDistractionSummary GetCurrentDistractionSummary() => throw new NotSupportedException();
        public void RefreshRuntimeStateCache() { }
        public void SetWatchdogLaunchFailureCode(string? code) { }
        public Porta.Agent.Core.AgentInputSummary GetInputsSummary(int windowSeconds = 60) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => [];
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20) => [];
        public Porta.Agent.Core.AgentTimelineData GetTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Porta.Agent.Core.Time.bucketSeconds) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyDictionary<string, double> GetFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc) => new System.Collections.Generic.Dictionary<string, double>();
        public void PublishLiveSessionState(Porta.Agent.Core.AgentLiveSessionSignalPacket packet) { }
        public void IngestInputSignals(Porta.Agent.Core.InputSignalsPacket packet) { }
        public Porta.Agent.Core.AgentSettingsState GetEffectiveSettings() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDataRetentionInfo GetDataRetention() => throw new NotSupportedException();
        public void Pause(string? reason = null, DateTimeOffset? untilUtc = null) { }
        public void Resume() { }
        public void GrantConsent() { }
        public void RevokeConsent() { }
        public Porta.Agent.Core.AgentSettingsState UpdateSettings(Porta.Agent.Core.AgentSettingsUpdate update) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentRecalibrationResult RecalibrateBaseline() => throw new NotSupportedException();
        public void DeleteLocalData() { }
        public void MarkError(string message) { }
        public void MarkHealthyHeartbeat() { }
        public void ApplyBrainSnapshot(Porta.Agent.Core.AgentBrainSnapshot snapshot, bool backendConnected) { }
        public void AcknowledgeIntervention(Porta.Agent.Core.AgentInterventionAck ack) { }
        public void TriggerTestIntervention(string action) { }
        public string ExportBundle() => string.Empty;
        public Porta.Agent.Core.AgentLabelPromptDecision GetLabelPromptDecision(bool meetingInProgress) => throw new NotSupportedException();
        public bool RecordFocusLabel(Porta.Agent.Core.AgentFocusLabelRequest request) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentLabelDiagnostics GetLabelDiagnostics() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentSignupNudgeDecision GetSignupNudgeDecision(bool isLinked) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentReminder? GetNextReminder() => throw new NotSupportedException();
        public bool AcknowledgeReminder(long id, string action) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? CreateTask(string title, int plannedSeconds) => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentTask> GetTodayTasks() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? ActOnTask(long id, string action) => throw new NotSupportedException();
    }

    private sealed class FakeRuntime : ICurrentSessionUserAgentRuntime
    {
        public DateTimeOffset UtcNowValue { get; set; } = DateTimeOffset.UtcNow;

        public int? InteractiveSessionId { get; set; }

        public bool HasUserAgentBinary { get; set; }

        public string? ExpectedBinaryVersion { get; set; }

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

        public string? ReadExpectedBinaryVersion() => ExpectedBinaryVersion;

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

        public string? TryLaunchInSession(int sessionId, string launchReason, ILogger logger)
        {
            LaunchAttempts.Add((sessionId, launchReason));
            return null;
        }
    }
}
