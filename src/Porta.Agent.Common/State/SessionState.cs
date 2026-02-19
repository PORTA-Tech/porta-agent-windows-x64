using System;

namespace Porta.Agent.Common.State;

public sealed class SessionState
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset SavedUtc { get; init; } = DateTimeOffset.UtcNow;
    public ControlState Control { get; init; } = new();
}
