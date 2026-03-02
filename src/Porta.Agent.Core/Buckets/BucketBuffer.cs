namespace Porta.Agent.Core.Buckets;

internal sealed class BucketBuffer
{
    public BucketBuffer(long bucketSeq)
    {
        BucketSeq = bucketSeq;
        (StartUtcMs, EndUtcMs) = Time.bucketStartEnd(bucketSeq);
        State = BucketLifecycleState.OPEN;
    }

    public long BucketSeq { get; }
    public long StartUtcMs { get; }
    public long EndUtcMs { get; }
    public object Gate { get; } = new();
    public BucketLifecycleState State { get; set; }
    public int SampleCount { get; set; }
    public AgentBrainSnapshot? LatestSnapshot { get; set; }
    public bool DirtyWhilePending { get; set; }
    public int PendingMutationCount { get; set; }

    // Staging metadata for later DB contracts (contributors/why/state_flags payloads).
    public SortedSet<string> ContributorsStage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> WhyStage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> StateFlagsStage { get; } = new(StringComparer.Ordinal);
}
