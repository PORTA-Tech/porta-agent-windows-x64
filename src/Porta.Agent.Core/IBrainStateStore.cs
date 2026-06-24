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
