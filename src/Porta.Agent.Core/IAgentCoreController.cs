namespace Porta.Agent.Core;

public interface IAgentCoreController
{
    AgentServiceStatusSnapshot GetStatus();
    AgentLiveSessionState GetLiveSessionState();
    AgentControlState GetControlState();
    AgentBrainSnapshot GetCurrentSnapshot();
    AgentLatestStateDetails? GetLatestStateDetails();
    AgentDistractionSummary GetCurrentDistractionSummary();
    void RefreshRuntimeStateCache();
    void SetWatchdogLaunchFailureCode(string? code);
    AgentInputSummary GetInputsSummary(int windowSeconds = 60);
    IReadOnlyList<AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null);
    IReadOnlyList<AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200);
    IReadOnlyList<AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20);
    AgentTimelineData GetTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds);

    /// <summary>Foreground dwell seconds per focus-identity hash over the window (for the digest breakdown).</summary>
    IReadOnlyDictionary<string, double> GetFocusAppDwellSeconds(DateTimeOffset fromUtc, DateTimeOffset toUtc);

    // Digest redesign (2026-08-11) — the day's shape by local hour for the email's timeline strip.
    // Hours that recorded nothing are absent from the result, never zeroed.
    IReadOnlyList<AgentDayTimelineHour> GetDayTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc);
    void PublishLiveSessionState(AgentLiveSessionSignalPacket packet);
    void IngestInputSignals(InputSignalsPacket packet);
    AgentSettingsState GetEffectiveSettings();
    AgentDataRetentionInfo GetDataRetention();
    void Pause(string? reason = null, DateTimeOffset? untilUtc = null);
    void Resume();
    void GrantConsent();
    void RevokeConsent();
    AgentSettingsState UpdateSettings(AgentSettingsUpdate update);
    AgentRecalibrationResult RecalibrateBaseline();
    void DeleteLocalData();
    void MarkError(string message);
    void MarkHealthyHeartbeat();
    void ApplyBrainSnapshot(AgentBrainSnapshot snapshot, bool backendConnected);
    void AcknowledgeIntervention(AgentInterventionAck ack);
    void TriggerTestIntervention(string action);
    string ExportBundle();

    // Phase 1b — Step 3b: sampled ground-truth prompts.
    AgentLabelPromptDecision GetLabelPromptDecision(bool meetingInProgress);
    bool RecordFocusLabel(AgentFocusLabelRequest request);

    // Phase 1b — Step 4: local readout of accumulated ground-truth labels (counts only, no content).
    AgentLabelDiagnostics GetLabelDiagnostics();

    // Phase 1c: decide whether an unlinked device should be nudged to sign in.
    AgentSignupNudgeDecision GetSignupNudgeDecision(bool isLinked);

    // Phase 2c — reminders: create (null = pending cap reached), read the next one to show,
    // and act on it (done | dismiss | snooze).
    AgentReminder? CreateReminder(string title, DateTimeOffset dueAtUtc);
    AgentReminder? GetNextReminder();
    bool AcknowledgeReminder(long id, string action);

    // Workstream 4 — timed tasks: create (null = cap reached / invalid), read today's list,
    // and act on one (start | done | abandon | extend).
    AgentTask? CreateTask(string title, int plannedSeconds);
    IReadOnlyList<AgentTask> GetTodayTasks();

    // Tasks from the last N goal-days as well as today. The narrative needs it: the morning
    // card fires after the 05:00 boundary, so yesterday's finished and abandoned tasks are
    // already outside GetTodayTasks' window at exactly the moment they matter most.
    IReadOnlyList<AgentTask> GetRecentTasks(int goalDaysBack);
    AgentTask? ActOnTask(long id, string action);

    // Step 9a — projects, the thing a task belongs to. Ari never guesses one: every value here
    // came from a tap. LOCAL ONLY — no payload gains a project field until 9b.
    AgentTask? CreateTask(string title, int plannedSeconds, long projectId);
    AgentProject? CreateProject(string name);
    IReadOnlyList<AgentProject> GetProjects(bool includeArchived = false);
    AgentTask? SetTaskProject(long taskId, long projectId);
    AgentProject? RenameProject(long id, string name);
    AgentProject? SetProjectArchived(long id, bool archived);
    // Step 10 phase A/B (2026-08-13) — what a client is shown, the user-side rate, and the report
    // draft. All local: no payload gains a field from any of these.
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
    AgentReportDraft GetReportDraft(long projectId, string fromLocalDate, string toLocalDate);

    // Step 10 phase C/D — the address book (local only) and the record of what was sent.
    IReadOnlyList<AgentReportRecipient> GetRecipients();

    AgentReportRecipient? UpsertRecipient(string name, string email);

    bool DeleteRecipient(long id);

    bool HasSentToRecipientBefore(string email);

    IReadOnlyList<AgentSentReport> GetSentReports();

    /// <summary>Written BEFORE a send is attempted. False = this reportId is already recorded.</summary>
    bool RecordSentReport(AgentSentReport report);

    void MarkSentReportOutcome(string reportId, bool sent, string sentAtUtc, string error);

    void MarkSentReportDeletePending(string reportId);

    bool DeleteSentReportLocally(string reportId);

    void MarkRecipientUsed(string email);

    // 2026-08-12 — hourly per-app time, resolved to projects at read time. The grain the projects
    // page will read, and the only record of it that outlives the 14-day capture window.
    IReadOnlyList<AgentAppUsageHour> GetAppUsage(DateTimeOffset fromUtc, DateTimeOffset toUtc);

    // Workstream D — conversational check-in goals (goal-day resolved at the 5:00 boundary).
    // Step 3 — taskCount is how many tasks the morning check-in recorded; it drives one ground-truth
    // label per task. Optional, so a caller that only knows about the goal sentence is unchanged.
    AgentDailyGoal? SetMorningGoal(string goalText, string trackingPref, int taskCount = 1);
    AgentDailyGoal? GetTodayGoal();
    AgentDailyGoal? RecordMiddayAnswer(string answer);
    AgentDailyGoal? RecordEndOfDay(string planTomorrow);

    // Part 3 — yesterday's plan_tomorrow, surfaced only while today has no stated goal, so the
    // morning check-in can open with it instead of a blank ask.
    string? GetCarriedOverPlan();
}
