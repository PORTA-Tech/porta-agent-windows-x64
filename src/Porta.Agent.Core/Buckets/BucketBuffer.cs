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
    public KeyboardRawAggregates Keyboard { get; } = new();
    public MouseRawAggregates Mouse { get; } = new();
    public FocusRawAggregates Focus { get; } = new();
    public IdleRawAggregates Idle { get; } = new();
    public double TypingBurstIntensitySum { get; set; }
    public double ScrollVelocityIndexSum { get; set; }
    public double FocusSwitchRateSum { get; set; }
    public double QualityScore { get; set; } = 1d;
    public bool Partial { get; set; }
    public AgentBrainSnapshot? LatestSnapshot { get; set; }
    public bool DirtyWhilePending { get; set; }
    public int PendingMutationCount { get; set; }

    // Staging metadata for later DB contracts (contributors/why/state_flags payloads).
    public SortedSet<string> ContributorsStage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> WhyStage { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> StateFlagsStage { get; } = new(StringComparer.Ordinal);
}
