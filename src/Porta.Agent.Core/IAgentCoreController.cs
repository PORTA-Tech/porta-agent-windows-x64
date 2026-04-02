namespace Porta.Agent.Core;

public interface IAgentCoreController
{
    AgentServiceStatusSnapshot GetStatus();
    AgentLiveSessionState GetLiveSessionState();
    AgentControlState GetControlState();
    AgentBrainSnapshot GetCurrentSnapshot();
    AgentLatestStateDetails? GetLatestStateDetails();
    void RefreshRuntimeStateCache();
    AgentInputSummary GetInputsSummary(int windowSeconds = 60);
    IReadOnlyList<AgentControlEvent> GetRecentEvents(int limit = 50, DateTimeOffset? sinceUtc = null);
    IReadOnlyList<AgentControlEvent> GetEventsSinceId(long sinceIdExclusive, int limit = 200);
    IReadOnlyList<AgentPendingIntervention> GetPendingInterventions(string sessionId, int limit = 20);
    AgentTimelineData GetTimeline(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds);
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
}
