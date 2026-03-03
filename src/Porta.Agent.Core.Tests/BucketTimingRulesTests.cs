namespace Porta.Agent.Core.Tests;

public sealed class BucketTimingRulesTests
{
    [Fact]
    public void PendingTrigger_StartsExactlyAtBucketEnd()
    {
        (_, long end) = Time.bucketStartEnd(bucket_seq: 5);

        Assert.False(Time.IsBucketWindowEnded(end - 1, end));
        Assert.True(Time.IsBucketWindowEnded(end, end));
        Assert.True(Time.IsBucketWindowEnded(end + 1, end));
    }

    [Fact]
    public void LateCutoff_IsBucketEndPlusMaxLate()
    {
        (long start, long end) = Time.bucketStartEnd(bucket_seq: 42);
        _ = start;

        long cutoff = Time.bucketLateCutoffFromEnd(end);

        Assert.Equal(end + Time.maxLateMs, cutoff);
    }

    [Fact]
    public void FinalizeEligibility_StartsExactlyAtLateCutoff()
    {
        (_, long end) = Time.bucketStartEnd(bucket_seq: 5);
        long cutoff = Time.bucketLateCutoffFromEnd(end);

        Assert.False(Time.IsBucketFinalizeEligible(cutoff - 1, end));
        Assert.True(Time.IsBucketFinalizeEligible(cutoff, end));
        Assert.True(Time.IsBucketFinalizeEligible(cutoff + 1, end));
    }
}
