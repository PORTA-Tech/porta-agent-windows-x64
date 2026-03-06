namespace Porta.Agent.Core;

public readonly record struct AgentBrightnessActionStatus(
    bool Supported,
    bool Active,
    string RampDirection,
    int PreviousLevel,
    int TargetLevel,
    int CurrentLevel,
    string? LastError,
    DateTimeOffset? BackoffUntilUtc,
    DateTimeOffset? CooldownUntilUtc);
