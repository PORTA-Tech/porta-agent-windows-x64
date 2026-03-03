using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class BucketRawInputContributorTests
{
    [Fact]
    public void ApplyEventToBucket_KeyboardRawContributor_UpdatesKeyboardWithoutAdvancingSampleStream()
    {
        long bucketSeq = Time.bucketSeqFromEventTs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var bucket = new BucketBuffer(bucketSeq);
        AgentBrainSnapshot snapshot = BuildRawSnapshot(
            bucket.StartUtcMs + 100,
            RawInputContributors.KeyboardKeydown(isBackspace: true));

        BucketAggregators.ApplyEventToBucket(bucket, snapshot);

        Assert.Equal(1, bucket.Keyboard.KeydownCount);
        Assert.Equal(1, bucket.Keyboard.BackspaceCount);
        Assert.Equal(0, bucket.SampleCount);
        Assert.Null(bucket.LatestSnapshot);
    }

    [Fact]
    public void ApplyEventToBucket_MouseAndIdleRawContributors_UpdateRawChannelAggregates()
    {
        long bucketSeq = Time.bucketSeqFromEventTs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var bucket = new BucketBuffer(bucketSeq);

        BucketAggregators.ApplyEventToBucket(
            bucket,
            BuildRawSnapshot(
                bucket.StartUtcMs + 120,
                RawInputContributors.MouseMove(8, -3)));

        BucketAggregators.ApplyEventToBucket(
            bucket,
            BuildRawSnapshot(
                bucket.StartUtcMs + 500,
                RawInputContributors.Idle(4.2)));

        Assert.Equal(1, bucket.Mouse.MoveSampleCount);
        Assert.Single(bucket.Idle.Samples);
        Assert.Equal(0, bucket.SampleCount);
    }

    private static AgentBrainSnapshot BuildRawSnapshot(long timestampUtcMs, string contributor)
    {
        return new AgentBrainSnapshot(
            TimestampUtc: DateTimeOffset.FromUnixTimeMilliseconds(timestampUtcMs),
            TypingBurstIntensity: 0d,
            ScrollVelocityIndex: 0d,
            FocusSwitchRate: 0d,
            SmoothedTypingBurstIntensity: 0d,
            SmoothedScrollVelocityIndex: 0d,
            SmoothedFocusSwitchRate: 0d,
            FocusStrength: 0d,
            OverloadLikelihood: 0d,
            FatigueProbability: 0d,
            CognitiveStability: 0d,
            DominantStateValue: 0d,
            DominantStateName: "focus",
            Confidence: 0d,
            LastAction: "raw_input_capture",
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: [contributor],
            PolicyId: "runtime-input-bridge",
            Domain: "input",
            EscalationLevel: "Inform");
    }
}
