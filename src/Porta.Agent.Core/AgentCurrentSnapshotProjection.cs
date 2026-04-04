namespace Porta.Agent.Core;

public readonly record struct AgentCurrentSnapshotProjection(
    AgentBrainSnapshot Snapshot,
    AgentLatestStateDetails? LatestState,
    AgentServiceStatusSnapshot Status);
