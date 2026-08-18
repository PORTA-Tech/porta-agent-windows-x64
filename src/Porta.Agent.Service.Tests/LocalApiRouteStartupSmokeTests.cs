using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Porta.Agent.Common.Identity;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Core;
using Porta.Agent.Core.Security;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Tests;

public sealed class LocalApiRouteStartupSmokeTests
{
    [Fact]
    public async Task KeyRoutes_StartAndRespond_WithoutServerError()
    {
        Secrets.ApiTokenReadResult tokenResult = Secrets.GetApiToken();
        string? tokenHex = tokenResult.Success && tokenResult.TokenBytes is { Length: 32 }
            ? Convert.ToHexString(tokenResult.TokenBytes)
            : null;

        await using WebApplication app = BuildApp(tokenHex, tokenResult.Success);
        await app.StartAsync();

        HttpClient client = app.GetTestClient();

        HttpResponseMessage healthResponse = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        HttpResponseMessage statusResponse = await SendAuthenticatedAsync(client, "/v1/status", tokenHex);
        HttpResponseMessage snapshotResponse = await SendAuthenticatedAsync(client, "/v1/snapshot/current", tokenHex);
        HttpResponseMessage liveSessionResponse = await SendAuthenticatedAsync(client, "/v1/session/live", tokenHex);

        Assert.NotEqual(HttpStatusCode.InternalServerError, statusResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, snapshotResponse.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, liveSessionResponse.StatusCode);

        if (!string.IsNullOrWhiteSpace(tokenHex))
        {
            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, snapshotResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, liveSessionResponse.StatusCode);
        }
    }

    private static WebApplication BuildApp(string? tokenHex, bool authReady)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddPortaLocalApi();
        builder.Services.AddSingleton<IAgentCoreController>(new SmokeAgentCoreController());
        builder.Services.AddSingleton<ILocalIdentityLinkStore>(new SmokeIdentityLinkStore());
        // /session/ari-context binds IAriContextProvider from DI; without a registration the
        // route builder infers it as a GET body and the whole endpoint tree fails to start.
        builder.Services.AddSingleton<IAriContextProvider>(new SmokeAriContextProvider());
        // /narrative binds INarrativeGateway the same way — an unregistered interface parameter
        // would break the endpoint tree at startup.
        builder.Services.AddSingleton<INarrativeGateway>(new SmokeNarrativeGateway());

        WebApplication app = builder.Build();
        app.MapPortaLocalApi(new LocalApiRuntimeSettings(
            Port: 17887,
            AuthToken: tokenHex,
            LocalApiAuthReady: authReady,
            LocalApiAuthError: authReady ? null : "TOKEN_READ_FAILED",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        return app;
    }

    private static async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpClient client, string path, string? tokenHex)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(tokenHex))
        {
            request.Headers.TryAddWithoutValidation("X-PORTA-TOKEN", tokenHex);
        }

        return await client.SendAsync(request);
    }

    private sealed class SmokeAgentCoreController : IAgentCoreController
    {
        private readonly DateTimeOffset _nowUtc = DateTimeOffset.Parse("2026-04-02T18:00:00Z");

        public AgentServiceStatusSnapshot GetStatus()
        {
            return new AgentServiceStatusSnapshot(
                Status: AgentStatus.Running,
                ServiceRunning: true,
                Paused: false,
                PauseUntilUtc: null,
                ConsentActive: true,
                ConsentVersion: "1.0",
                ConsentTimestampUtc: _nowUtc.AddMinutes(-10),
                ConsentDisclosureHash: "consent-hash",
                BackendConnected: true,
                FocusStrength: 0.74d,
                OverloadLikelihood: 0.18d,
                LastAction: "maintain_focus",
                AgentVersion: "1.0.29",
                SchemaVersion: "1",
                PolicyVersion: "1",
                LastEventTimestampUtc: _nowUtc.AddSeconds(-10),
                BackendInstanceId: "backend-1",
                BackendProcessId: 4001,
                CaptureContext: "UserSession",
                UserAgentHeartbeatUpdatedAtUtc: _nowUtc.AddSeconds(-2),
                UserAgentInstanceId: "agent-1",
                UserAgentProcessId: 5001,
                UserAgentSessionId: "session-1",
                KeyboardSignalActive: true,
                MouseSignalActive: true,
                LastInputEventUtc: _nowUtc.AddSeconds(-1),
                LastInputAgentInstanceId: "agent-1",
                LastInputSessionId: "session-1",
                OwnershipWarning: null,
                CaptureFailureReason: null,
                LastError: null,
                LocalApiAuthReady: true,
                LocalApiAuthError: null,
                NotificationSuppressionActive: false,
                NotificationSuppressionReady: true,
                NotificationSuppressionLastError: null,
                NotificationSuppressionError: null,
                NotificationSuppressionBackoffUntilUtc: null,
                NotificationBackoffUntilUtc: null,
                NotificationsSuppressed: false,
                BrightnessActionReady: true,
                BrightnessActionActive: false,
                BrightnessAdjusted: false,
                BrightnessSupported: true,
                BrightnessCurrentLevel: 80,
                BrightnessTargetLevel: 80,
                BrightnessLastError: null,
                BrightnessError: null,
                BrightnessBackoffUntilUtc: null,
                DbMigrationFailed: false,
                BaselineReady: true,
                ColdStart: false,
                CalibrationStage: "calibrated",
                SystemActionsEnabled: true,
                TrustTier: "high",
                OverloadActive: false,
                DeepWorkActive: true,
                DeepWorkStreakSeconds: 600,
                VisualMode: "normal",
                LatestContributorsJson: "{\"schema_ver\":1}",
                LatestWhyJson: "{\"schema_ver\":1}",
                LastUpdatedUtc: _nowUtc,
                RuntimeStateFreshness: "fresh",
                RuntimeStateLastSuccessfulRefreshUtc: _nowUtc.AddSeconds(-2),
                RuntimeStateLastAttemptUtc: _nowUtc.AddSeconds(-1),
                RuntimeStateRefreshError: null);
        }

        public AgentLiveSessionState GetLiveSessionState()
        {
            return new AgentLiveSessionState(
                UpdatedAtUtc: _nowUtc,
                SessionStartedAtUtc: _nowUtc.AddMinutes(-20),
                LastInputEventUtc: _nowUtc.AddSeconds(-1),
                AgentInstanceId: "agent-1",
                SessionId: "session-1",
                KeyboardActive: true,
                MouseActive: false,
                UserActive: true,
                Idle: false,
                IdleSeconds: 0.7d,
                FocusAppHash: "a1b2c3",
                Freshness: "fresh",
                DegradedReason: null,
                FocusStartedAtUtc: _nowUtc.AddMinutes(-4));
        }

        public AgentControlState GetControlState() => new(
            Paused: false,
            ConsentActive: true,
            PauseUntilUtc: null,
            RegulationIntensity: "balanced",
            NotificationReductionEnabled: true,
            FocusModeEnabled: true,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false);

        public AgentBrainSnapshot GetCurrentSnapshot()
        {
            return new AgentBrainSnapshot(
                TimestampUtc: _nowUtc,
                TypingBurstIntensity: 0.12d,
                ScrollVelocityIndex: 0.04d,
                FocusSwitchRate: 0.03d,
                SmoothedTypingBurstIntensity: 0.10d,
                SmoothedScrollVelocityIndex: 0.03d,
                SmoothedFocusSwitchRate: 0.02d,
                FocusStrength: 0.74d,
                OverloadLikelihood: 0.18d,
                FatigueProbability: 0.12d,
                CognitiveStability: 0.85d,
                DominantStateValue: 0.74d,
                DominantStateName: "focus",
                Confidence: 0.89d,
                LastAction: "maintain_focus",
                ActivationWindowTriggered: false,
                CooldownTriggered: false,
                ActivationWindowOpen: false,
                Contributors: [],
                SustainedAttentionDurationSec: 620d,
                IdleFragmentationIndex: 0.03d,
                OverloadActive: false,
                StateFlagsJson: "{\"schema_ver\":1}",
                ContributorsJson: "{\"schema_ver\":1}");
        }

        public AgentLatestStateDetails? GetLatestStateDetails() => null;
        public AgentDistractionSummary GetCurrentDistractionSummary() => new(0d, 0d, 0d, 0d, 0, 0);
        public void RefreshRuntimeStateCache() { }
        public void SetWatchdogLaunchFailureCode(string? code) { }
        public AgentInputSummary GetInputsSummary(int windowSeconds = 60) => new(
            FromUtc: _nowUtc.AddSeconds(-windowSeconds),
            ToUtc: _nowUtc,
            WindowSeconds: windowSeconds,
            SampleCount: 1,
            TypingBurstIntensity: 0.12d,
            ScrollVelocityIndex: 0.04d,
            FocusSwitchRate: 0.03d,
            IdleFragmentationIndex: 0.03d,
            NotificationLatencyIndex: 0d);
        public IReadOnlyList<AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => [];
        public IReadOnlyList<AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
        public IReadOnlyList<AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20) => [];
        public AgentTimelineData GetTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Time.bucketSeconds) => new(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            BucketSeconds: bucketSeconds,
            Snapshots: [],
            Interventions: []);
        public IReadOnlyDictionary<string, double> GetFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc) => new Dictionary<string, double>();

        public IReadOnlyList<Porta.Agent.Core.AgentDayTimelineHour> GetDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc) => System.Array.Empty<Porta.Agent.Core.AgentDayTimelineHour>();
        public void PublishLiveSessionState(AgentLiveSessionSignalPacket packet) => throw new NotSupportedException();
        public void IngestInputSignals(InputSignalsPacket packet) => throw new NotSupportedException();
        public AgentSettingsState GetEffectiveSettings() => new(
            RegulationIntensity: "balanced",
            PrivacyLevel: "standard",
            NotificationReductionEnabled: true,
            FocusModeEnabled: true,
            AnimationReductionEnabled: false,
            TemporaryAppRestrictionEnabled: false,
            BaselineRecalibrationActive: false,
            BaselineRecalibrationStartedUtc: null,
            BaselineRecalibrationUntilUtc: null,
            UpdatedAtUtc: _nowUtc);
        public AgentDataRetentionInfo GetDataRetention() => new(
            RetentionDays: 30,
            DatabaseBytes: 1024,
            LogBytes: 2048,
            ConsentBytes: 256,
            TotalBytes: 3328,
            UpdatedAtUtc: _nowUtc);
        public void Pause(string? reason = null, DateTimeOffset? untilUtc = null) => throw new NotSupportedException();
        public void Resume() => throw new NotSupportedException();
        public void GrantConsent() => throw new NotSupportedException();
        public void RevokeConsent() => throw new NotSupportedException();
        public AgentSettingsState UpdateSettings(AgentSettingsUpdate update) => throw new NotSupportedException();
        public AgentRecalibrationResult RecalibrateBaseline() => throw new NotSupportedException();
        public void DeleteLocalData() => throw new NotSupportedException();
        public void MarkError(string message) => throw new NotSupportedException();
        public void MarkHealthyHeartbeat() => throw new NotSupportedException();
        public void ApplyBrainSnapshot(AgentBrainSnapshot snapshot, bool backendConnected) => throw new NotSupportedException();
        public void AcknowledgeIntervention(AgentInterventionAck ack) => throw new NotSupportedException();
        public void TriggerTestIntervention(string action) => throw new NotSupportedException();
        public string ExportBundle() => throw new NotSupportedException();
        public AgentLabelPromptDecision GetLabelPromptDecision(bool meetingInProgress) => throw new NotSupportedException();
        public bool RecordFocusLabel(AgentFocusLabelRequest request) => throw new NotSupportedException();
        public AgentLabelDiagnostics GetLabelDiagnostics() => throw new NotSupportedException();
        public AgentSignupNudgeDecision GetSignupNudgeDecision(bool isLinked) => throw new NotSupportedException();
        public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc) => throw new NotSupportedException();
        public AgentReminder? GetNextReminder() => throw new NotSupportedException();
        public bool AcknowledgeReminder(long id, string action) => throw new NotSupportedException();
        public AgentTask? CreateTask(string title, int plannedSeconds) => throw new NotSupportedException();
        public AgentTask? CreateTask(string title, int plannedSeconds, long projectId) => throw new NotSupportedException();
        public AgentProject? CreateProject(string name) => throw new NotSupportedException();
        public IReadOnlyList<AgentProject> GetProjects(bool includeArchived = false) => throw new NotSupportedException();
        public AgentTask? SetTaskProject(long taskId, long projectId) => throw new NotSupportedException();
        public AgentProject? RenameProject(long id, string name) => throw new NotSupportedException();
        public AgentProject? SetProjectArchived(long id, bool archived) => throw new NotSupportedException();
        public AgentTask? SetTaskClientLabel(long taskId, string label) => throw new NotSupportedException();
        public AgentTask? SetTaskReportFields(long taskId, string? status, string? resultLine, string? blockedReason, string? expectedFinishDay) => throw new NotSupportedException();
        public AgentProject? SetProjectClientName(long id, string clientName) => throw new NotSupportedException();
        public AgentProject? SetProjectLabelStyle(long id, bool useClientLabels) => throw new NotSupportedException();
        public AgentProject? SetProjectRate(long id, long rateMinor, string currency) => throw new NotSupportedException();

        public AgentProject? SetProjectEngagement(long id, string? status, string? startDay, string? completionDay, IReadOnlyList<string>? recipients, string? reportingFrequency) => null;

        public AgentProjectStats ReadProjectStats(long projectId) => new(projectId, 0, 0, 0, 0, 0d);

        public IReadOnlyList<AgentProjectCard> ReadProjectCards(bool includeArchived = false) => System.Array.Empty<AgentProjectCard>();

        public IReadOnlyList<AgentProjectTaskRow> ReadProjectTasks(long projectId) => System.Array.Empty<AgentProjectTaskRow>();

        public IReadOnlyList<AgentTaskRow> ReadAllTaskRows() => System.Array.Empty<AgentTaskRow>();

        public bool SetSentReportArchived(string reportId, bool archived) => false;
        public AgentReportDraft GetReportDraft(long projectId, string fromLocalDate, string toLocalDate) => throw new NotSupportedException();

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
        public IReadOnlyList<AgentAppUsageHour> GetAppUsage(DateTimeOffset fromUtc, DateTimeOffset toUtc) => throw new NotSupportedException();
        public IReadOnlyList<AgentTask> GetTodayTasks() => throw new NotSupportedException();

        public IReadOnlyList<AgentTask> GetRecentTasks(int goalDaysBack) => throw new NotSupportedException();
        public AgentTask? ActOnTask(long id, string action) => throw new NotSupportedException();
        public AgentDailyGoal? SetMorningGoal(string goalText, string trackingPref, int taskCount = 1) => throw new NotSupportedException();
        public AgentDailyGoal? GetTodayGoal() => throw new NotSupportedException();
        public AgentDailyGoal? RecordMiddayAnswer(string answer) => throw new NotSupportedException();
        public AgentDailyGoal? RecordEndOfDay(string planTomorrow) => throw new NotSupportedException();
        public string? GetCarriedOverPlan() => null;
    }

    private sealed class SmokeAriContextProvider : IAriContextProvider
    {
        public LocalApiAriContextResponse GetContext() => new(null, null, null, null, null);
    }

    private sealed class SmokeNarrativeGateway : INarrativeGateway
    {
        public Task<string?> GetNarrativeLineAsync(string kind, string? userText, CancellationToken token) =>
            Task.FromResult<string?>(null);

        public Task<NarrativeGatewayResult> GetNarrativeLineWithReasonAsync(
            string kind,
            string? userText,
            CancellationToken token) =>
            Task.FromResult(NarrativeGatewayResult.Failed("smoke_stub"));
    }

    private sealed class SmokeIdentityLinkStore : ILocalIdentityLinkStore
    {
        private LocalIdentityLinkSnapshot _snapshot = new(
            SchemaVersion: 1,
            DeviceId: "device-1",
            LinkStatus: LocalIdentityLinkStatuses.Unlinked,
            AccountId: null,
            OrgId: null,
            UpdatedAtUtc: DateTimeOffset.Parse("2026-04-02T18:00:00Z"));

        public LocalIdentityLinkSnapshot GetCurrent() => _snapshot;

        public LocalIdentityLinkSnapshot SetLink(string accountId, string orgId)
        {
            _snapshot = _snapshot with
            {
                LinkStatus = LocalIdentityLinkStatuses.Linked,
                AccountId = accountId,
                OrgId = orgId,
                LinkedAtUtc = DateTimeOffset.Parse("2026-04-02T18:00:00Z"),
                UpdatedAtUtc = DateTimeOffset.Parse("2026-04-02T18:00:00Z")
            };
            return _snapshot;
        }

        public LocalIdentityLinkSnapshot ClearLink()
        {
            _snapshot = _snapshot with
            {
                LinkStatus = LocalIdentityLinkStatuses.Unlinked,
                AccountId = null,
                OrgId = null,
                LinkedAtUtc = null,
                UpdatedAtUtc = DateTimeOffset.Parse("2026-04-02T18:00:00Z")
            };
            return _snapshot;
        }
    }
}
