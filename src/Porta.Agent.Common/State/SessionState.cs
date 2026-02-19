using System;

namespace Porta.Agent.Common.State;

public sealed class SessionState
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset SavedUtc { get; init; } = DateTimeOffset.UtcNow;
    public double BaselineArousalMean { get; init; }
    public double BaselineVariance { get; init; }
    public double DriftOffset { get; init; }
    public double LastStableKp { get; init; }
    public double LastStableKi { get; init; }
    public double LastStableKd { get; init; }
}
