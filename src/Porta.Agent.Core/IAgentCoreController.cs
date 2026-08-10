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
