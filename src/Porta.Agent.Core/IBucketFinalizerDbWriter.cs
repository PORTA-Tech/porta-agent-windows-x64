using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core;

/// <summary>
/// Minimal DB write surface for finalized bucket snapshots.
/// Only BucketFinalizer should hold this dependency.
/// </summary>
public interface IBucketFinalizerDbWriter
{
    void WriteInputSignalWindow(AgentInputSignalWindow window);
    void WriteFinalizedBucketSnapshot(AgentBrainSnapshot snapshot, InputBucketRow inputBucketRow);
}
