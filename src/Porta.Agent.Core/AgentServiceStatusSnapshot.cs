using Porta.Agent.Common.Contracts;

namespace Porta.Agent.Core;

public sealed record AgentServiceStatusSnapshot(
    AgentStatus Status,
    bool ServiceRunning,
    bool Paused,
    bool ConsentActive,
    string ConsentVersion,
    DateTimeOffset? ConsentTimestampUtc,
    string ConsentDisclosureHash,
    bool BackendConnected,
    double FocusStrength,
    double OverloadLikelihood,
    string LastAction,
    string AgentVersion,
    string SchemaVersion,
    string PolicyVersion,
    DateTimeOffset? LastEventTimestampUtc,
    string? LastError,
    DateTimeOffset LastUpdatedUtc);
