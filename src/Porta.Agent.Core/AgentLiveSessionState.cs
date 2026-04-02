namespace Porta.Agent.Core;

public sealed record AgentLiveSessionSignalPacket(
    string AgentInstanceId,
    string SessionId,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset SessionStartedAtUtc,
    DateTimeOffset? LastInputEventUtc,
    bool KeyboardActive,
    bool MouseActive,
    double IdleSeconds,
    string? FocusAppHash);

public sealed record AgentLiveSessionState(
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? SessionStartedAtUtc,
    DateTimeOffset? LastInputEventUtc,
    string? AgentInstanceId,
    string? SessionId,
    bool KeyboardActive,
    bool MouseActive,
    bool UserActive,
    bool Idle,
    double IdleSeconds,
    string? FocusAppHash,
    string Freshness,
    string? DegradedReason);
