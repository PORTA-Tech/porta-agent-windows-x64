namespace Porta.Agent.Core;

public interface IAgentCoreController
{
    AgentServiceStatusSnapshot GetStatus();
    AgentControlState GetControlState();
    void Pause(string? reason = null, DateTimeOffset? untilUtc = null);
    void Resume();
    void GrantConsent();
    void RevokeConsent();
    void MarkError(string message);
    void MarkHealthyHeartbeat();
    void ApplyBrainSnapshot(AgentBrainSnapshot snapshot, bool backendConnected);
    string ExportBundle();
}
