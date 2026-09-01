using Porta.Agent.Common.Identity;
using Porta.Agent.Core;
using Porta.Agent.Service.Fleet;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// Part 5 fail-soft contract at the agent seam: every refusal path returns null (the card then
/// renders its honest template) and never throws — a narrative problem must never cost a check-in.
/// </summary>
public sealed class NarrativeGatewayTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("streak_hype")]
    [InlineData("MORNING")] // kinds are exact lowercase tokens
    public async Task InvalidKind_ReturnsNull(string kind)
    {
        NarrativeGateway gateway = BuildGateway(linked: true);

        Assert.Null(await gateway.GetNarrativeLineAsync(kind, userText: null, CancellationToken.None));
    }

    [Fact]
    public async Task DisabledControlPlaneClient_ReturnsNull()
    {
        // Client constructed with no PORTA_BACKEND_URL is permanently disabled.
        string? previous = Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");
        Environment.SetEnvironmentVariable("PORTA_BACKEND_URL", null);
        try
        {
            NarrativeGateway gateway = BuildGateway(linked: true);
            Assert.Null(await gateway.GetNarrativeLineAsync("morning", userText: null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORTA_BACKEND_URL", previous);
        }
    }

    [Fact]
    public async Task UnlinkedDevice_ReturnsNull()
    {
        string? previous = Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");
        Environment.SetEnvironmentVariable("PORTA_BACKEND_URL", null);
        try
        {
            NarrativeGateway gateway = BuildGateway(linked: false);
            Assert.Null(await gateway.GetNarrativeLineAsync("midday", userText: null, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORTA_BACKEND_URL", previous);
        }
    }

    private static NarrativeGateway BuildGateway(bool linked)
    {
        return new NarrativeGateway(
            new ThrowingAgentCoreController(),
            new ControlPlaneClient(),
            new FixedIdentityLinkStore(linked));
    }

    // The refusal paths above must all bail out BEFORE touching the controller; a throwing
    // controller proves it (the gateway's outer catch would swallow a throw into null anyway,
    // but these tests document that the cheap gates come first).
    private sealed class ThrowingAgentCoreController : Porta.Agent.Core.IAgentCoreController
    {
        public Porta.Agent.Core.AgentServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentLiveSessionState GetLiveSessionState() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentControlState GetControlState() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentBrainSnapshot GetCurrentSnapshot() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentLatestStateDetails? GetLatestStateDetails() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDistractionSummary GetCurrentDistractionSummary() => throw new NotSupportedException();
        public void RefreshRuntimeStateCache() => throw new NotSupportedException();
        public void SetWatchdogLaunchFailureCode(string? code) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentInputSummary GetInputsSummary(int windowSeconds = 60) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTimelineData GetTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Porta.Agent.Core.Time.bucketSeconds) => throw new NotSupportedException();
        public System.Collections.Generic.IReadOnlyDictionary<string, double> GetFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc) => throw new NotSupportedException();

        public System.Collections.Generic.IReadOnlyList<Porta.Agent.Core.AgentDayTimelineHour> GetDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc) => throw new NotSupportedException();
        public void PublishLiveSessionState(Porta.Agent.Core.AgentLiveSessionSignalPacket packet) => throw new NotSupportedException();
        public void IngestInputSignals(Porta.Agent.Core.InputSignalsPacket packet) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentSettingsState GetEffectiveSettings() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDataRetentionInfo GetDataRetention() => throw new NotSupportedException();
        public void Pause(string? reason = null, DateTimeOffset? untilUtc = null) => throw new NotSupportedException();
        public void Resume() => throw new NotSupportedException();
        public void GrantConsent() => throw new NotSupportedException();
        public void RevokeConsent() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentSettingsState UpdateSettings(Porta.Agent.Core.AgentSettingsUpdate update) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentRecalibrationResult RecalibrateBaseline() => throw new NotSupportedException();
        public void DeleteLocalData() => throw new NotSupportedException();
        public void MarkError(string message) => throw new NotSupportedException();
        public void MarkHealthyHeartbeat() => throw new NotSupportedException();
        public void ApplyBrainSnapshot(Porta.Agent.Core.AgentBrainSnapshot snapshot, bool backendConnected) => throw new NotSupportedException();
        public void AcknowledgeIntervention(Porta.Agent.Core.AgentInterventionAck ack) => throw new NotSupportedException();
        public void TriggerTestIntervention(string action) => throw new NotSupportedException();
        public string ExportBundle() => throw new NotSupportedException();
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

    // Step 10 phase C/D — inert stubs, same reasoning as above.
    public IReadOnlyList<AgentReportRecipient> GetRecipients() => System.Array.Empty<AgentReportRecipient>();
    public AgentReportRecipient? UpsertRecipient(string name, string email) => null;
    public bool DeleteRecipient(long id) => false;
    public bool HasSentToRecipientBefore(string email) => false;
    public IReadOnlyList<AgentSentReport> GetSentReports() => System.Array.Empty<AgentSentReport>();
    public bool RecordSentReport(AgentSentReport report) => false;
    public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error) { }
    public void MarkSentReportDeletePending(string reportId) { }
    public bool DeleteSentReportLocally(string reportId) => false;
    public void MarkRecipientUsed(string email) { }
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
        public double GetUnassignedSecondsToday() => throw new NotSupportedException();
        public int GetDriftHoldCountToday() => throw new NotSupportedException();
        public string? GetCarriedOverPlan() => throw new NotSupportedException();
        public void SetLocalApiAuthState(bool ready, string? error) => throw new NotSupportedException();
    }

    private sealed class FixedIdentityLinkStore : ILocalIdentityLinkStore
    {
        private readonly LocalIdentityLinkSnapshot _snapshot;

        public FixedIdentityLinkStore(bool linked)
        {
            _snapshot = new LocalIdentityLinkSnapshot(
                SchemaVersion: 1,
                DeviceId: linked ? "device-1" : "device-1",
                LinkStatus: linked ? LocalIdentityLinkStatuses.Linked : LocalIdentityLinkStatuses.Unlinked,
                AccountId: linked ? "acc-1" : null,
                OrgId: null,
                UpdatedAtUtc: DateTimeOffset.Parse("2026-07-18T08:00:00Z"));
        }

        public LocalIdentityLinkSnapshot GetCurrent() => _snapshot;

        public LocalIdentityLinkSnapshot SetLink(string accountId, string orgId) => _snapshot;

        public LocalIdentityLinkSnapshot ClearLink() => _snapshot;
    }
}
