using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core;

/// <summary>
/// IBrainStateStore adapter that routes all writes through BucketFinalizer single-writer flow.
/// </summary>
public sealed class BucketFinalizerStateStore : IBrainStateStore, IBucketFinalizerPump, IInputSignalsIngress
{
    private readonly IBrainStateStore _inner;
    private readonly BucketFinalizer _finalizer;

    public BucketFinalizerStateStore(IBrainStateStore inner)
        : this(inner, nowProvider: null)
    {
    }

    internal BucketFinalizerStateStore(IBrainStateStore inner, Func<DateTimeOffset>? nowProvider)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (_inner is not IBucketFinalizerDbWriter bucketDbWriter)
        {
            throw new InvalidOperationException(
                "BucketFinalizerStateStore requires an inner store that implements IBucketFinalizerDbWriter.");
        }

        _finalizer = new BucketFinalizer(_inner, bucketDbWriter, nowProvider);
    }

    public void WriteSnapshot(AgentBrainSnapshot snapshot) => _finalizer.WriteSnapshot(snapshot);
    public void IngestInputSignals(InputSignalsPacket packet) => _finalizer.IngestInputSignals(packet);
    public void RunFinalizerTick(DateTimeOffset nowUtc) => _finalizer.RunFinalizerTick(nowUtc);
    public void WriteControlEvent(AgentControlEvent controlEvent) => _finalizer.WriteControlEvent(controlEvent);
    public void WriteSystemChange(AgentSystemChange change) => _finalizer.WriteSystemChange(change);
    public void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics) => _finalizer.WriteDiagnostics(diagnostics);
    public void WriteConsent(AgentConsentRecord consent) => _finalizer.WriteConsent(consent);
    public AgentConsentRecord? ReadLatestConsent() => _inner.ReadLatestConsent();
    public void WriteSettings(AgentSettingsRecord settings) => _finalizer.WriteSettings(settings);
    public AgentSettingsRecord? ReadLatestSettings() => _inner.ReadLatestSettings();
    public void DeleteLocalData() => _finalizer.DeleteLocalData();
    public AgentBrainSnapshot? ReadLatestSnapshot() => _inner.ReadLatestSnapshot();
    public AgentLatestStateDetails? ReadLatestStateDetails() => _inner.ReadLatestStateDetails();
    public AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus() => _inner.ReadNotificationSuppressionStatus();
    public AgentBrightnessActionStatus ReadBrightnessActionStatus() => _inner.ReadBrightnessActionStatus();
    public bool ReadDbMigrationFailed() => _inner.ReadDbMigrationFailed();

    public IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds)
        => _inner.ReadTimeline(fromUtc, toUtc, bucketSeconds);

    public IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null)
        => _inner.ReadRecentEvents(limit, sinceUtc);
    public IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200)
        => _inner.ReadEventsSinceId(sinceIdExclusive, limit);

    public IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000)
        => _inner.ReadEvents(fromUtc, toUtc, limit);
    public IReadOnlyDictionary<string, double> ReadFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => _inner.ReadFocusAppDwellSeconds(fromUtc, toUtc);
    public IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20)
        => _inner.ReadPendingInterventions(sessionId, limit);
    public bool AcknowledgeIntervention(AgentInterventionAck ack)
        => _inner.AcknowledgeIntervention(ack);
    public void RecordGroundTruthLabel(GroundTruthLabel label) => _inner.RecordGroundTruthLabel(label);
    public void RecordPromptShown(string labelTarget, string promptKind, DateTimeOffset shownAtUtc)
        => _inner.RecordPromptShown(labelTarget, promptKind, shownAtUtc);
    public void ResolvePromptAnswered(string labelTarget, DateTimeOffset answeredAtUtc)
        => _inner.ResolvePromptAnswered(labelTarget, answeredAtUtc);
    public AgentLabelPromptCadence ReadLabelPromptCadence() => _inner.ReadLabelPromptCadence();
    public AgentSignupNudgeContext ReadSignupNudgeContext() => _inner.ReadSignupNudgeContext();
    public void RecordSignupNudgeShown(DateTimeOffset shownAtUtc) => _inner.RecordSignupNudgeShown(shownAtUtc);
    public AgentLabelDiagnostics ReadLabelDiagnostics() => _inner.ReadLabelDiagnostics();
    public AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc, DateTimeOffset nowUtc)
        => _inner.CreateReminder(title, dueAtUtc, nowUtc);
    public AgentReminder? ReadNextReminder(DateTimeOffset nowUtc) => _inner.ReadNextReminder(nowUtc);
    public bool AcknowledgeReminder(long id, string action, DateTimeOffset nowUtc)
        => _inner.AcknowledgeReminder(id, action, nowUtc);
    public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc)
        => _inner.CreateTask(title, plannedSeconds, nowUtc);
    public IReadOnlyList<AgentTask> ReadTasks(DateTimeOffset dayStartUtc, DateTimeOffset nowUtc)
        => _inner.ReadTasks(dayStartUtc, nowUtc);
    public AgentTask? ActOnTask(long id, string action, DateTimeOffset nowUtc, string? deferUntilDay = null)
        => _inner.ActOnTask(id, action, nowUtc, deferUntilDay);

    // Step 9a — projects, straight through like every other task call.
    public AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc, long projectId)
        => _inner.CreateTask(title, plannedSeconds, nowUtc, projectId);
    public AgentProject? CreateProject(string name, DateTimeOffset nowUtc)
        => _inner.CreateProject(name, nowUtc);
    public IReadOnlyList<AgentProject> ReadProjects(bool includeArchived = false)
        => _inner.ReadProjects(includeArchived);
    public AgentTask? SetTaskProject(long taskId, long projectId)
        => _inner.SetTaskProject(taskId, projectId);
    public AgentProject? RenameProject(long id, string name)
        => _inner.RenameProject(id, name);
    public AgentProject? SetProjectArchived(long id, bool archived, DateTimeOffset nowUtc)
        => _inner.SetProjectArchived(id, archived, nowUtc);
    public AgentTask? SetTaskClientLabel(long taskId, string label)
        => _inner.SetTaskClientLabel(taskId, label);

    public AgentTask? SetTaskReportFields(long taskId, string? status, string? resultLine, string? blockedReason, string? expectedFinishDay)
        => _inner.SetTaskReportFields(taskId, status, resultLine, blockedReason, expectedFinishDay);
    public AgentProject? SetProjectClientName(long id, string clientName)
        => _inner.SetProjectClientName(id, clientName);
    public AgentProject? SetProjectLabelStyle(long id, bool useClientLabels)
        => _inner.SetProjectLabelStyle(id, useClientLabels);
    public AgentProject? SetProjectRate(long id, long rateMinor, string currency)
        => _inner.SetProjectRate(id, rateMinor, currency);

    public AgentProject? SetProjectEngagement(
        long id,
        string? status,
        string? startDay,
        string? completionDay,
        IReadOnlyList<string>? recipients,
        string? reportingFrequency)
        => _inner.SetProjectEngagement(id, status, startDay, completionDay, recipients, reportingFrequency);

    public AgentProjectStats ReadProjectStats(long projectId)
        => _inner.ReadProjectStats(projectId);
    public IReadOnlyList<AgentProjectCard> ReadProjectCards(bool includeArchived = false)
        => _inner.ReadProjectCards(includeArchived);

    public IReadOnlyList<AgentProjectTaskRow> ReadProjectTasks(long projectId)
        => _inner.ReadProjectTasks(projectId);

    public IReadOnlyList<AgentTaskRow> ReadAllTaskRows()
        => _inner.ReadAllTaskRows();
    public AgentReportDraft ReadReportDraft(long projectId, string fromLocalDate, string toLocalDate, TimeSpan localOffset)
        => _inner.ReadReportDraft(projectId, fromLocalDate, toLocalDate, localOffset);

    // Step 10 phase C/D — straight delegation, like everything else on this decorator.
    public IReadOnlyList<AgentReportRecipient> ReadRecipients() => _inner.ReadRecipients();

    public AgentReportRecipient? UpsertRecipient(string name, string email, DateTimeOffset nowUtc)
        => _inner.UpsertRecipient(name, email, nowUtc);

    public bool DeleteRecipient(long id) => _inner.DeleteRecipient(id);

    public void MarkRecipientUsed(string email, DateTimeOffset nowUtc)
        => _inner.MarkRecipientUsed(email, nowUtc);

    public bool HasSentToRecipientBefore(string email) => _inner.HasSentToRecipientBefore(email);

    public bool RecordSentReport(AgentSentReport report) => _inner.RecordSentReport(report);

    public void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error)
        => _inner.MarkSentReportOutcome(reportId, sent, sentAtUtc, error);

    public IReadOnlyList<AgentSentReport> ReadSentReports() => _inner.ReadSentReports();

    public void MarkSentReportDeletePending(string reportId) => _inner.MarkSentReportDeletePending(reportId);

    public bool DeleteSentReportLocally(string reportId) => _inner.DeleteSentReportLocally(reportId);
    public IReadOnlyList<AgentAppUsageHour> ReadAppUsage(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan localOffset)
        => _inner.ReadAppUsage(fromUtc, toUtc, localOffset);

    public IReadOnlyList<AgentDayTimelineHour> ReadDayTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan localOffset)
        => _inner.ReadDayTimeline(fromUtc, toUtc, localOffset);

    public AgentDailyGoal? UpsertMorningGoal(string localDate, string goalText, string trackingPref, DateTimeOffset nowUtc)
        => _inner.UpsertMorningGoal(localDate, goalText, trackingPref, nowUtc);
    public AgentDailyGoal? GetDailyGoal(string localDate)
        => _inner.GetDailyGoal(localDate);
    public AgentDailyGoal? RecordMiddayGoalAnswer(string localDate, string answer, DateTimeOffset nowUtc)
        => _inner.RecordMiddayGoalAnswer(localDate, answer, nowUtc);
    public AgentDailyGoal? RecordEndOfDayPlan(string localDate, string planTomorrow, DateTimeOffset nowUtc)
        => _inner.RecordEndOfDayPlan(localDate, planTomorrow, nowUtc);

    public AgentStorageInfo ReadStorageInfo() => _inner.ReadStorageInfo();
    public AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null) => _inner.ReadTodaySummary(dateUtc);
    public AgentDistractionSummary ReadCurrentDistractionSummary() => _inner.ReadCurrentDistractionSummary();

    public string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles)
        => _inner.ExportBundle(logFilePaths, consentPath, settingsPath, versions, configPaths, supplementalJsonFiles);
}
