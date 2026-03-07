namespace Porta.Agent.Core;

public sealed record InputSignalsPacket(
    int SchemaVer,
    string? AgentInstanceId,
    string? SessionId,
    DateTimeOffset? WindowStartUtc,
    DateTimeOffset? WindowEndUtc,
    int KeyboardKeydownCount,
    int KeyboardBackspaceCount,
    int MouseWheelDelta,
    double MouseMoveDx,
    double MouseMoveDy,
    string? FocusAppHash,
    double IdleSeconds,
    DateTimeOffset? LastInputEventUtc);
