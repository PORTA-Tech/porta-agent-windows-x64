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

        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentDayTimelineHour> GetDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc) => System.Array.Empty<Porta.Agent.Core.AgentDayTimelineHour>();
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
        public Porta.Agent.Core.AgentTask? CreateTask(string title, int plannedSeconds, long projectId) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? CreateProject(string name) => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentProject> GetProjects(bool includeArchived = false) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? SetTaskProject(long taskId, long projectId) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? RenameProject(long id, string name) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? SetProjectArchived(long id, bool archived) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? SetTaskClientLabel(long taskId, string label) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? SetTaskReportFields(long taskId, string? status, string? resultLine, string? blockedReason, string? expectedFinishDay) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? SetProjectClientName(long id, string clientName) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? SetProjectLabelStyle(long id, bool useClientLabels) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentProject? SetProjectRate(long id, long rateMinor, string currency) => throw new NotSupportedException();

        public Porta.Agent.Core.AgentProject? SetProjectEngagement(long id, string? status, string? startDay, string? completionDay, IReadOnlyList<string>? recipients, string? reportingFrequency) => throw new NotSupportedException();

        public Porta.Agent.Core.AgentProjectStats ReadProjectStats(long projectId) => throw new NotSupportedException();

        public IReadOnlyList<Porta.Agent.Core.AgentProjectCard> ReadProjectCards(bool includeArchived = false) => throw new NotSupportedException();

        public IReadOnlyList<Porta.Agent.Core.AgentProjectTaskRow> ReadProjectTasks(long projectId) => throw new NotSupportedException();

        public IReadOnlyList<Porta.Agent.Core.AgentTaskRow> ReadAllTaskRows() => throw new NotSupportedException();

        public bool SetSentReportArchived(string reportId, bool archived) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentReportDraft GetReportDraft(long projectId, string fromLocalDate, string toLocalDate) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentWeeklyDigestData GetWeeklyDigestData(string fromLocalDate, string toLocalDate) => throw new NotSupportedException();

        // Step 10 phase C/D — this controller is deliberately unusable; the watchdog tests never
        // touch reports, and throwing keeps it that way rather than quietly returning empties.
        public IReadOnlyList<Porta.Agent.Core.AgentReportRecipient> GetRecipients() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentReportRecipient? UpsertRecipient(string name, string email) => throw new NotSupportedException();
        public bool DeleteRecipient(long id) => throw new NotSupportedException();
        public bool HasSentToRecipientBefore(string email) => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentSentReport> GetSentReports() => throw new NotSupportedException();
        public bool RecordSentReport(Porta.Agent.Core.AgentSentReport report) => throw new NotSupportedException();
        public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error) => throw new NotSupportedException();
        public void MarkSentReportDeletePending(string reportId) => throw new NotSupportedException();
        public bool DeleteSentReportLocally(string reportId) => throw new NotSupportedException();
        public void MarkRecipientUsed(string email) => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentAppUsageHour> GetAppUsage(DateTimeOffset fromUtc, DateTimeOffset toUtc) => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentTask> GetTodayTasks() => throw new NotSupportedException();

        public IReadOnlyList<Porta.Agent.Core.AgentTask> GetRecentTasks(int goalDaysBack) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? ActOnTask(long id, string action) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? SetMorningGoal(string goalText, string trackingPref, int taskCount = 1) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? GetTodayGoal() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? RecordMiddayAnswer(string answer) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? RecordEndOfDay(string planTomorrow) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentCheckInCorrection? RecordCheckInCorrection(
            string cardKind, string correction, string assumedEndLocal,
            double assumedActiveSeconds, int assumedFinishedCount, int assumedOpenCount)
            => throw new NotSupportedException();
        public IReadOnlyList<Porta.Agent.Core.AgentCheckInCorrection> GetCheckInCorrectionsToday()
            => Array.Empty<Porta.Agent.Core.AgentCheckInCorrection>();
        public double GetUnassignedSecondsToday() => 0d;
        public int GetDriftHoldCountToday() => 0;
        public string? GetCarriedOverPlan() => null;
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
