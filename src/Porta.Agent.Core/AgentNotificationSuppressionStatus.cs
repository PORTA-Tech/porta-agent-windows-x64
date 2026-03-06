namespace Porta.Agent.Core;

public sealed record AgentNotificationSuppressionStatus(
    bool Active,
    bool Ready,
    string ActionState,
    string? LastError,
    DateTimeOffset? BackoffUntilUtc,
    DateTimeOffset? EnableCooldownUntilUtc,
    DateTimeOffset? MinOnUntilUtc,
    bool RestorePending);
