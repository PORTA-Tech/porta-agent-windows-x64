namespace Porta.Agent.Core;

public interface IAgentCoreController
{
    AgentServiceStatusSnapshot GetStatus();
    AgentControlState GetControlState();
    AgentBrainSnapshot GetCurrentSnapshot();
    AgentInputSummary GetInputsSummary(int windowSeconds = 60);
    IReadOnlyList<AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null);
    IReadOnlyList<AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200);
    AgentTimelineData GetTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds);
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
    void TriggerTestIntervention(string action);
    string ExportBundle();
}
