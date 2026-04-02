namespace Porta.Agent.Core.Tests;

public sealed class DurableBucketPipelineTests
{
    [Fact]
    public void DurablePipeline_RemainsOnFiveSecondBuckets()
    {
        Assert.Equal(5, Time.bucketSeconds);
        Assert.Equal(Time.bucketSeconds * 1000, Time.bucket_ms);
    }

    [Fact]
    public void DurablePipeline_KeepsCurrentLateAndFinalizeWindows()
    {
        Assert.Equal(2000, Time.maxLateMs);
        Assert.Equal(2000, Time.finalizeDelayMs);
    }
}
