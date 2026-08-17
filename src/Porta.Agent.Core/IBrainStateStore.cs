namespace Porta.Agent.Core;

public interface IBrainStateStore
{
    void WriteSnapshot(AgentBrainSnapshot snapshot);
    void WriteControlEvent(AgentControlEvent controlEvent);
    void WriteSystemChange(AgentSystemChange change);
    void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics);
    void WriteConsent(AgentConsentRecord consent);
    AgentConsentRecord? ReadLatestConsent();
    void WriteSettings(AgentSettingsRecord settings);
    AgentSettingsRecord? ReadLatestSettings();
    void DeleteLocalData();
    AgentBrainSnapshot? ReadLatestSnapshot();
    AgentLatestStateDetails? ReadLatestStateDetails();
    AgentNotificationSuppressionStatus ReadNotificationSuppressionStatus();
    AgentBrightnessActionStatus ReadBrightnessActionStatus();
    bool ReadDbMigrationFailed();
    IReadOnlyList<AgentBrainSnapshot> ReadTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds);
    IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null);
    IReadOnlyList<AgentControlEvent> ReadEventsSinceId(long sinceIdExclusive, int limit = 200);
    IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000);

    /// <summary>
    /// Sums foreground dwell time (seconds) per focus-identity hash over the window, read from
    /// input_signal_windows. Powers the "where your time went" per-app breakdown in the digest.
    /// </summary>
    IReadOnlyDictionary<string, double> ReadFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc);

    IReadOnlyList<AgentPendingIntervention> ReadPendingInterventions(string sessionId, int limit = 20);
    bool AcknowledgeIntervention(AgentInterventionAck ack);

    // Phase 1b: durable ground-truth labels (see ground_truth_labels).
    void RecordGroundTruthLabel(GroundTruthLabel label);
    // Phase 1b — anti-spam: prompt_events shown/answered tracking for the governor's back-off.
    void RecordPromptShown(string labelTarget, string promptKind, DateTimeOffset shownAtUtc);
    void ResolvePromptAnswered(string labelTarget, DateTimeOffset answeredAtUtc);
    AgentLabelPromptCadence ReadLabelPromptCadence();
    // Phase 1c — signup nudge cadence (days of data + caps) and recording a shown nudge.
    AgentSignupNudgeContext ReadSignupNudgeContext();
    void RecordSignupNudgeShown(DateTimeOffset shownAtUtc);
    AgentLabelDiagnostics ReadLabelDiagnostics();
    // Phase 2c — reminders (service-side so they survive user-agent restarts).
    AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc, DateTimeOffset nowUtc);
    AgentReminder? ReadNextReminder(DateTimeOffset nowUtc);
    bool AcknowledgeReminder(long id, string action, DateTimeOffset nowUtc);
    // Workstream 4 — timed tasks (plan vs. reality), service-side like reminders.
    AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc);
    IReadOnlyList<AgentTask> ReadTasks(DateTimeOffset dayStartUtc, DateTimeOffset nowUtc);
    // deferUntilDay carries the goal-day ("yyyy-MM-dd") for the "defer" action only; the day is a
    // LOCAL concept, so the caller resolves it rather than the store guessing from nowUtc.
    AgentTask? ActOnTask(long id, string action, DateTimeOffset nowUtc, string? deferUntilDay = null);

    // Step 9a — projects. LOCAL ONLY: no payload gains a project field until 9b.
    AgentTask? CreateTask(string title, int plannedSeconds, DateTimeOffset nowUtc, long projectId);
    AgentProject? CreateProject(string name, DateTimeOffset nowUtc);
    IReadOnlyList<AgentProject> ReadProjects(bool includeArchived = false);
    AgentTask? SetTaskProject(long taskId, long projectId);
    AgentProject? RenameProject(long id, string name);
    AgentProject? SetProjectArchived(long id, bool archived, DateTimeOffset nowUtc);

    // Step 10 phase A (2026-08-13) — what a client is shown, and the user-side rate. Local only:
    // no payload gains a field from any of these. Step10_Report_Spec.md §3, §7, §12.7.
    AgentTask? SetTaskClientLabel(long taskId, string label);

    /// Phase 1.1 + 1.3 (2026-08-17) — the reporting fields, written in ONE call.
    /// Consolidated on purpose: status, its blocked reason, the result line and the expected
    /// finish are edited together on the report draft, and four separate round trips is four
    /// chances for them to disagree. Any argument left null is left unchanged.
    AgentTask? SetTaskReportFields(
        long taskId,
        string? status,
        string? resultLine,
        string? blockedReason,
        string? expectedFinishDay);

    AgentProject? SetProjectClientName(long id, string clientName);
    AgentProject? SetProjectLabelStyle(long id, bool useClientLabels);
    AgentProject? SetProjectRate(long id, long rateMinor, string currency);

    // Step 10 phase B (2026-08-13) — the report as it stands, derived and never stored. Local only.
    AgentReportDraft ReadReportDraft(
        long projectId,
        string fromLocalDate,
        string toLocalDate,
        TimeSpan localOffset);

    // Step 10 phase C/D (2026-08-14) — the address book and the record of what was sent.
    // The book is LOCAL ONLY: nothing here uploads it, and only the one address on the report
    // being sent ever leaves the machine (§12.4).
    IReadOnlyList<AgentReportRecipient> ReadRecipients();

    AgentReportRecipient? UpsertRecipient(string name, string email, DateTimeOffset nowUtc);

    bool DeleteRecipient(long id);

    void MarkRecipientUsed(string email, DateTimeOffset nowUtc);

    bool HasSentToRecipientBefore(string email);

    /// <summary>Written BEFORE the send is attempted. False = this reportId already exists.</summary>
    bool RecordSentReport(AgentSentReport report);

    void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error);

    IReadOnlyList<AgentSentReport> ReadSentReports();

    void MarkSentReportDeletePending(string reportId);

    /// <summary>Runs ONLY after the server half succeeded. Never report success on a half-delete.</summary>
    bool DeleteSentReportLocally(string reportId);

    // 2026-08-12 — the hourly app rollup, resolved to projects at read time. localOffset is passed
    // in for the same reason ReadDayTimeline takes it: the local hour is a local concept.
    IReadOnlyList<AgentAppUsageHour> ReadAppUsage(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan localOffset);

    // Digest redesign — the day's shape by local hour. localOffset is passed in for the same reason
    // as above: the hour is a local concept and the store holds UTC.
    IReadOnlyList<AgentDayTimelineHour> ReadDayTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeSpan localOffset);

    // Workstream D — conversational check-in goals.
    AgentDailyGoal? UpsertMorningGoal(string localDate, string goalText, string trackingPref, DateTimeOffset nowUtc);
    AgentDailyGoal? GetDailyGoal(string localDate);
    AgentDailyGoal? RecordMiddayGoalAnswer(string localDate, string answer, DateTimeOffset nowUtc);
    AgentDailyGoal? RecordEndOfDayPlan(string localDate, string planTomorrow, DateTimeOffset nowUtc);
    AgentStorageInfo ReadStorageInfo();
    AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null);
    AgentDistractionSummary ReadCurrentDistractionSummary();
    string ExportBundle(
        IReadOnlyList<string> logFilePaths,
        string consentPath,
        string settingsPath,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyList<string> configPaths,
        IReadOnlyDictionary<string, string> supplementalJsonFiles);
}
