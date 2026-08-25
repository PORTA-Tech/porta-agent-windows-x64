using System.Text.Json;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class InputSignalsBucketIntegrationTests
{
    [Fact]
    public void IngestedSignals_FinalizeThroughBucketPipeline_WithoutDirectSnapshotWrites()
    {
        var innerStore = new FakeBucketIngressStore();
        var stateStore = new BucketFinalizerStateStore(
            innerStore,
            () => new DateTimeOffset(2026, 03, 07, 10, 0, 12, TimeSpan.Zero));

        DateTimeOffset windowStartUtc = new(2026, 03, 07, 10, 0, 3, TimeSpan.Zero);
        DateTimeOffset windowEndUtc = new(2026, 03, 07, 10, 0, 7, TimeSpan.Zero);

        stateStore.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: 4,
            KeyboardBackspaceCount: 2,
            MouseWheelDelta: 120,
            MouseMoveDx: 20d,
            MouseMoveDy: -10d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 4d,
            LastInputEventUtc: windowEndUtc,
            KeyboardIkiMeanMs: 140d,
            KeyboardIkiStdMs: 20d,
            KeyboardIkiCv: 0.1428571429d,
            KeyboardBurstCount: 2,
            KeyboardBurstRate: 0.5d,
            KeyboardCorrectionSpikeCount: 1,
            KeyboardCorrectionSpikeRate: 0.25d,
            MouseMoveSampleCount: 8,
            MousePathLengthPx: 200d,
            MouseSpeedMeanPxPerSec: 50d,
            MouseJerkP95: 110d,
            MouseMicroCorrectionCount: 2,
            MouseMicroCorrectionRate: 0.5d,
            MouseWheelEventCount: 2,
            MouseScrollBurstCount: 1,
            MouseScrollBurstRate: 0.25d,
            MouseScrollSpeedMean: 240d));

        Assert.Empty(innerStore.RecordedWindows);

        stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 07, 10, 0, 12, TimeSpan.Zero));

        Assert.Equal(2, innerStore.FinalizedWrites.Count);
        Assert.Equal(0, innerStore.DirectSnapshotWrites);
        AgentInputSignalWindow recordedWindow = Assert.Single(innerStore.RecordedWindows);
        Assert.Equal("agent-1", recordedWindow.AgentInstanceId);
        Assert.Equal("session-1", recordedWindow.SessionId);
        Assert.Equal(windowStartUtc, recordedWindow.WindowStartUtc);
        Assert.Equal(windowEndUtc, recordedWindow.WindowEndUtc);
        Assert.Equal(4, recordedWindow.KeyboardKeydownCount);
        Assert.Equal(2, recordedWindow.KeyboardBackspaceCount);
        Assert.Equal(120, recordedWindow.MouseWheelDelta);
        Assert.Equal("a1b2c3d4", recordedWindow.FocusAppHash);
        Assert.NotNull(recordedWindow.MouseScrollSpeedMean);
        Assert.Equal(240d, recordedWindow.MouseScrollSpeedMean.Value, precision: 6);

        FinalizedWrite first = innerStore.FinalizedWrites.OrderBy(static write => write.InputRow.BucketSeq).First();
        FinalizedWrite second = innerStore.FinalizedWrites.OrderBy(static write => write.InputRow.BucketSeq).Last();

        Assert.Equal(0, first.InputRow.Partial);
        Assert.Equal(0, second.InputRow.Partial);
        Assert.Equal("a1b2c3d4", first.InputRow.DominantAppHash);
        Assert.Equal("a1b2c3d4", second.InputRow.DominantAppHash);
        Assert.Equal(2d, first.InputRow.IdleSeconds, precision: 6);
        Assert.Equal(2d, second.InputRow.IdleSeconds, precision: 6);
        Assert.Equal(140d, first.InputRow.KeyboardIkiMean, precision: 6);
        Assert.Equal(0.5d, first.InputRow.KeyboardBurstRate, precision: 6);
        Assert.Equal(50d, first.InputRow.MouseSpeedMean, precision: 6);
        Assert.Equal(110d, first.InputRow.MouseJerkP95, precision: 6);
        Assert.Equal(0.5d, first.InputRow.MouseMicroCorrectionRate, precision: 6);
        Assert.Equal(240d, first.InputRow.MouseScrollSpeed, precision: 6);

        AssertKeyboardCounts(first.InputRow, expectedKeydowns: 2, expectedBackspaces: 1);
        AssertKeyboardCounts(second.InputRow, expectedKeydowns: 2, expectedBackspaces: 1);
    }

    [Fact]
    public void IngestedSignals_V2IdleSummary_PreservesCurrentIdleAndWithinWindowBoundary()
    {
        var innerStore = new FakeBucketIngressStore();
        var stateStore = new BucketFinalizerStateStore(
            innerStore,
            () => new DateTimeOffset(2026, 03, 07, 10, 0, 8, TimeSpan.Zero));

        DateTimeOffset windowStartUtc = new(2026, 03, 07, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset windowEndUtc = new(2026, 03, 07, 10, 0, 5, TimeSpan.Zero);

        stateStore.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.75d,
            LastInputEventUtc: windowEndUtc,
            IdleObservationCount: 5,
            IdleSecondsWithinWindow: 4.5d,
            MicroIdleCount: 1,
            MicroIdleSeconds: 4.5d,
            BreakSecondsWithinWindow: 0d,
            ShortIdleBoundaryObserved: true));

        stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 07, 10, 0, 8, TimeSpan.Zero));

        FinalizedWrite write = Assert.Single(innerStore.FinalizedWrites);
        Assert.Equal(0.75d, write.InputRow.IdleSeconds, precision: 6);
        Assert.Equal(1d, write.InputRow.IdleMicroIdleCount, precision: 6);
        Assert.Equal(4.5d, write.InputRow.IdleMicroIdleSeconds, precision: 6);

        using JsonDocument json = JsonDocument.Parse(write.InputRow.ContributorsJson);
        JsonElement idle = json.RootElement.GetProperty("idle");
        Assert.Equal(0.75d, idle.GetProperty("current_idle_s").GetDouble(), precision: 6);
        Assert.Equal(4.5d, idle.GetProperty("idle_within_window_s").GetDouble(), precision: 6);
        Assert.True(idle.GetProperty("short_idle_boundary_observed").GetBoolean());
    }

    [Fact]
    public void IngestedSignals_V2LongBreakSummary_PreservesTrueBreakFactsForContinuityReset()
    {
        var innerStore = new FakeBucketIngressStore();
        var stateStore = new BucketFinalizerStateStore(
            innerStore,
            () => new DateTimeOffset(2026, 03, 07, 10, 5, 8, TimeSpan.Zero));

        DateTimeOffset windowStartUtc = new(2026, 03, 07, 10, 5, 0, TimeSpan.Zero);
        DateTimeOffset windowEndUtc = new(2026, 03, 07, 10, 5, 5, TimeSpan.Zero);

        stateStore.IngestInputSignals(new InputSignalsPacket(
            SchemaVer: 2,
            AgentInstanceId: "agent-1",
            SessionId: "session-1",
            WindowStartUtc: windowStartUtc,
            WindowEndUtc: windowEndUtc,
            KeyboardKeydownCount: 1,
            KeyboardBackspaceCount: 0,
            MouseWheelDelta: 0,
            MouseMoveDx: 0d,
            MouseMoveDy: 0d,
            FocusAppHash: "a1b2c3d4",
            IdleSeconds: 0.5d,
            LastInputEventUtc: windowEndUtc,
            IdleObservationCount: 5,
            IdleSecondsWithinWindow: 300d,
            MicroIdleCount: 0,
            MicroIdleSeconds: 0d,
            BreakSecondsWithinWindow: 300d,
            ShortIdleBoundaryObserved: true));

        stateStore.RunFinalizerTick(new DateTimeOffset(2026, 03, 07, 10, 5, 8, TimeSpan.Zero));

        FinalizedWrite write = Assert.Single(innerStore.FinalizedWrites);
        Assert.Equal(300d, write.InputRow.TrueBreakSeconds, precision: 6);
        Assert.Equal(1, write.InputRow.TrueBreakFlag);

        using JsonDocument json = JsonDocument.Parse(write.InputRow.ContributorsJson);
        JsonElement idle = json.RootElement.GetProperty("idle");
        Assert.Equal(300d, idle.GetProperty("break_s").GetDouble(), precision: 6);
        Assert.Equal(1, idle.GetProperty("true_break_flag").GetInt32());
        Assert.True(idle.GetProperty("short_idle_boundary_observed").GetBoolean());
    }

    private static void AssertKeyboardCounts(InputBucketRow row, int expectedKeydowns, int expectedBackspaces)
    {
        using JsonDocument json = JsonDocument.Parse(row.ContributorsJson);
        JsonElement keyboard = json.RootElement.GetProperty("keyboard");
        Assert.Equal(expectedKeydowns, keyboard.GetProperty("keys").GetInt32());
        Assert.Equal(expectedBackspaces, keyboard.GetProperty("backspace").GetInt32());
    }

    private readonly record struct FinalizedWrite(AgentBrainSnapshot Snapshot, InputBucketRow InputRow);

    private sealed class FakeBucketIngressStore : IBrainStateStore, IBucketFinalizerDbWriter
    {
        public int DirectSnapshotWrites { get; private set; }
        public List<FinalizedWrite> FinalizedWrites { get; } = [];
        public List<AgentInputSignalWindow> RecordedWindows { get; } = [];

        public void WriteInputSignalWindow(AgentInputSignalWindow window)
        {
            RecordedWindows.Add(window);
        }

        public void WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow)
        {
            FinalizedWrites.Add(new FinalizedWrite(snapshot, inputBucketRow));
        }

        public void WriteSnapshot(AgentBrainSnapshot snapshot)
        {
            DirectSnapshotWrites++;
            throw new InvalidOperationException("direct_snapshot_write_not_expected");
        }

        public void WriteControlEvent(AgentControlEvent controlEvent) { }
        public void WriteSystemChange(AgentSystemChange change) { }
        public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics) { }
        public void WriteConsent(AgentConsentRecord consent) { }
        public AgentConsentRecord? ReadLatestConsent() => null;
        public void WriteSettings(AgentSettingsRecord settings) { }
        public AgentSettingsRecord? ReadLatestSettings() => null;
        public void DeleteLocalData() { }
        public void RecordGroundTruthLabel(GroundTruthLabel label) { }
        public void RecordPromptShown(string labelTarget, string promptKind, DateTimeOffset shownAtUtc) { }
        public void ResolvePromptAnswered(string labelTarget, DateTimeOffset answeredAtUtc) { }
        public AgentLabelPromptCadence ReadLabelPromptCadence() => new(0, null);
        public AgentSignupNudgeContext ReadSignupNudgeContext() => new(0, 0, null);
        public void RecordSignupNudgeShown(DateTimeOffset shownAtUtc) { }
        public AgentLabelDiagnostics ReadLabelDiagnostics() => new(0, 0, 0, System.Array.Empty<AgentLabelTargetCount>());
        public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc, DateTimeOffset nowUtc) => null;
        public AgentReminder? ReadNextReminder(DateTimeOffset nowUtc) => null;
        public bool AcknowledgeReminder(long id, string action, DateTimeOffset nowUtc) => false;
        public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc) => null;
        public IReadOnlyList<AgentTask> ReadTasks(DateTimeOffset dayStartUtc, DateTimeOffset nowUtc) => System.Array.Empty<AgentTask>();
        public AgentTask? ActOnTask(long id, string action, DateTimeOffset nowUtc, string? deferUntilDay = null) => null;
        public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc, long projectId) => null;
        public AgentProject? CreateProject(string name, DateTimeOffset nowUtc) => null;
        public IReadOnlyList<AgentProject> ReadProjects(bool includeArchived = false) => System.Array.Empty<AgentProject>();
        public AgentTask? SetTaskProject(long taskId, long projectId) => null;
        public AgentProject? RenameProject(long id, string name) => null;
        public AgentProject? SetProjectArchived(long id, bool archived, DateTimeOffset nowUtc) => null;
        public AgentTask? SetTaskClientLabel(long taskId, string label) => null;
        public AgentTask? SetTaskReportFields(long taskId, string? status, string? resultLine, string? blockedReason, string? expectedFinishDay) => null;
        public AgentProject? SetProjectClientName(long id, string clientName) => null;
        public AgentProject? SetProjectLabelStyle(long id, bool useClientLabels) => null;
        public AgentProject? SetProjectRate(long id, long rateMinor, string currency) => null;
        public AgentProject? SetProjectEngagement(long id, string? status, string? startDay, string? completionDay, IReadOnlyList<string>? recipients, string? reportingFrequency) => null;
        public AgentProjectStats ReadProjectStats(long projectId) => new(projectId, 0, 0, 0, 0, 0d);
        public IReadOnlyList<AgentProjectCard> ReadProjectCards(bool includeArchived = false) => [];

        public IReadOnlyList<AgentProjectTaskRow> ReadProjectTasks(long projectId) => [];

        public IReadOnlyList<AgentTaskRow> ReadAllTaskRows() => [];

        public bool SetSentReportArchived(string reportId, bool archived) => false;
        public AgentReportDraft ReadReportDraft(long projectId, string fromLocalDate, string toLocalDate, TimeSpan localOffset)
            => new(projectId, "", "", fromLocalDate, toLocalDate, 0d, 0, 0, System.Array.Empty<AgentReportLine>(), System.Array.Empty<AgentReportDay>());
        public AgentWeeklyDigestData ReadWeeklyDigestData(string fromLocalDate, string toLocalDate, TimeSpan localOffset)
            => AgentWeeklyDigestData.Empty;

    // Step 10 phase C/D — inert stubs. This fake exists for other behaviour; reports are covered by
    // their own suites, and returning empties here keeps that boundary obvious.
    public IReadOnlyList<AgentReportRecipient> ReadRecipients() => System.Array.Empty<AgentReportRecipient>();
    public AgentReportRecipient? UpsertRecipient(string name, string email, DateTimeOffset nowUtc) => null;
    public bool DeleteRecipient(long id) => false;
    public void MarkRecipientUsed(string email, DateTimeOffset nowUtc) { }
    public bool HasSentToRecipientBefore(string email) => false;
    public bool RecordSentReport(AgentSentReport report) => false;
    public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error) { }
    public IReadOnlyList<AgentSentReport> ReadSentReports() => System.Array.Empty<AgentSentReport>();
    public void MarkSentReportDeletePending(string reportId) { }
    public bool DeleteSentReportLocally(string reportId) => false;
        public IReadOnlyList<AgentAppUsageHour> ReadAppUsage(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan localOffset) => System.Array.Empty<AgentAppUsageHour>();

    public IReadOnlyList<AgentDayTimelineHour> ReadDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan localOffset) => Array.Empty<AgentDayTimelineHour>();
        public AgentDailyGoal? UpsertMorningGoal(string localDate, string goalText, string trackingPref, DateTimeOffset nowUtc) => null;
        public AgentDailyGoal? GetDailyGoal(string localDate) => null;
        public AgentDailyGoal? RecordMiddayGoalAnswer(string localDate, string answer, DateTimeOffset nowUtc) => null;
        public AgentDailyGoal? RecordEndOfDayPlan(string localDate, string planTomorrow, DateTimeOffset nowUtc) => null;
        public AgentCheckInCorrection? RecordCheckInCorrection(
            string localDate, string cardKind, string correction, string assumedEndLocal,
            double assumedActiveSeconds, int assumedFinishedCount, int assumedOpenCount, DateTimeOffset nowUtc) => null;
        public IReadOnlyList<AgentCheckInCorrection> ReadCheckInCorrections(string localDate)
            => Array.Empty<AgentCheckInCorrection>();
        public AgentBrainSnapshot? ReadLatestSnapshot() => null;
        public AgentLatestStateDetails? ReadLatestStateDetails() => null;
        public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus()
            => new(
                Active: false,
                Ready: true,
                ActionState: "inactive",
                LastError: null,
                BackoffUntilUtc: null,
                EnableCooldownUntilUtc: null,
                MinOnUntilUtc: null,
                RestorePending: false);
        public AgentBrightnessActionStatus ReadBrightnessActionStatus()
            => new(
                Supported: true,
                ActionReady: true,
                Active: false,
                RampDirection: "idle",
                PreviousLevel: -1,
                TargetLevel: -1,
                CurrentLevel: -1,
                LastError: null,
                BackoffUntilUtc: null,
                CooldownUntilUtc: null);
        public bool ReadDbMigrationFailed() => false;
        public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = Time.bucketSeconds) => [];
        public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null) => [];
        public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200) => [];
        public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000) => [];
        public IReadOnlyDictionary<string, double> ReadFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc) => new Dictionary<string, double>();
        public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20) => [];
        public bool AcknowledgeIntervention(AgentInterventionAck ack) => true;
        public AgentStorageInfo ReadStorageInfo() => new(RetentionDays: 90, DatabaseBytes: 0);
        public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null)
            => new(DateOnly.FromDateTime(DateTime.UtcNow), 0, 0, 0, 0, null);
        public AgentDistractionSummary ReadCurrentDistractionSummary()
            => new(0d, 0d, 0d, 0d, 0, 0);
        public string ExportBundle(
            IReadOnlyList<string> logFilePaths,
            string consentPath,
            string settingsPath,
            IReadOnlyDictionary<string, string> versions,
            IReadOnlyList<string> configPaths,
            IReadOnlyDictionary<string, string> supplementalJsonFiles) => string.Empty;
    }
}
