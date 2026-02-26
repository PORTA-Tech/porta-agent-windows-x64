namespace Porta.Agent.Core;

public sealed record AgentControlState(
    bool Paused,
    bool ConsentActive,
    DateTimeOffset? PauseUntilUtc);
