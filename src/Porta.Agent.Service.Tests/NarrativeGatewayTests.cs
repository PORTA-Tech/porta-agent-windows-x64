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
        public IReadOnlyList<Porta.Agent.Core.AgentTask> GetTodayTasks() => throw new NotSupportedException();

        public IReadOnlyList<Porta.Agent.Core.AgentTask> GetRecentTasks(int goalDaysBack) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentTask? ActOnTask(long id, string action) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? SetMorningGoal(string goalText, string trackingPref, int taskCount = 1) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? GetTodayGoal() => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? RecordMiddayAnswer(string answer) => throw new NotSupportedException();
        public Porta.Agent.Core.AgentDailyGoal? RecordEndOfDay(string planTomorrow) => throw new NotSupportedException();
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
