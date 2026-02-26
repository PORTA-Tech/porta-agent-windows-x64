namespace Porta.Agent.Core;

public interface IBrainStateStore
{
    void WriteSnapshot(AgentBrainSnapshot snapshot);
    void WriteControlEvent(AgentControlEvent controlEvent);
    void WriteDiagnostics(AgentDiagnosticsSnapshot diagnostics);
    void DeleteLocalData();
    AgentBrainSnapshot? ReadLatestSnapshot();
    IReadOnlyList<AgentBrainSnapshot> ReadTimeline(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = 5);
    IReadOnlyList<AgentControlEvent> ReadRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null);
    IReadOnlyList<AgentControlEvent> ReadEvents(DateTimeOffset fromUtc, DateTimeOffset toUtc, int limit = 2000);
    AgentStorageInfo ReadStorageInfo();
    AgentTodaySummary ReadTodaySummary(DateOnly? dateUtc = null);
    string ExportBundle(string logFilePath, string consentPath);
}
