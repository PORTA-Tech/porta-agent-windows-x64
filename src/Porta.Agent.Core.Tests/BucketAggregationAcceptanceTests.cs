using System.Reflection;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

public sealed class BucketAggregationAcceptanceTests
{
    [Fact]
    public void Keyboard_FixedIki_ProducesExpectedMeanStdCv()
    {
        var keyboard = new KeyboardRawAggregates();
        for (int i = 0; i < 10; i++)
        {
            keyboard.RecordKeydown(i * 100L, isBackspace: false);
        }

        keyboard.RecomputeDerivedMetrics();

        Assert.Equal(10, keyboard.KeydownCount);
        Assert.Equal(0, keyboard.BackspaceCount);
        Assert.Equal(100d, keyboard.IkiMeanMs, precision: 6);
        Assert.Equal(0d, keyboard.IkiStdMs, precision: 6);
        Assert.Equal(0d, keyboard.IkiCv, precision: 6);
    }

    [Fact]
    public void Keyboard_BackspaceCountsOnly_NoContentState()
    {
        var keyboard = new KeyboardRawAggregates();
        keyboard.RecordKeydown(1000, isBackspace: false);
        keyboard.RecordKeydown(1100, isBackspace: true);
        keyboard.RecordKeydown(1200, isBackspace: false);
        keyboard.RecordKeydown(1300, isBackspace: true);
        keyboard.RecordKeydown(1400, isBackspace: false);
        keyboard.RecomputeDerivedMetrics();

        Assert.Equal(5, keyboard.KeydownCount);
        Assert.Equal(2, keyboard.BackspaceCount);
        Assert.All(keyboard.KeydownTsList, static ts => Assert.True(ts >= 0));

        bool hasStringState = typeof(KeyboardRawAggregates)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Select(member => member switch
            {
                FieldInfo field => field.FieldType,
                PropertyInfo property => property.PropertyType,
                _ => typeof(void)
            })
            .Any(static t => t == typeof(string));

        Assert.False(hasStringState);
    }

    [Fact]
    public void Mouse_KnownSamples_ProducesDeterministicSpeedAndJerkP95()
    {
        var mouse = new MouseRawAggregates();
        mouse.RecordMove(0, 0, 0);
        mouse.RecordMove(100, 10, 0);
        mouse.RecordMove(200, 20, 0);
        mouse.RecordMove(300, 30, 0);
        mouse.RecordMove(400, 40, 0);

        mouse.RecomputeDerivedMetrics();
        double firstSpeedMean = mouse.SpeedMeanPxPerSec;
        double firstJerkP95 = mouse.JerkP95;

        mouse.RecomputeDerivedMetrics();

        Assert.Equal(250d, firstSpeedMean, precision: 6);
        Assert.Equal(1000d, firstJerkP95, precision: 6);
        Assert.Equal(firstSpeedMean, mouse.SpeedMeanPxPerSec, precision: 6);
        Assert.Equal(firstJerkP95, mouse.JerkP95, precision: 6);
    }

    [Fact]
    public void FocusSuppression_A500_B600_A10s_DiscardsBAndKeepsStableA()
    {
        var focus = new FocusRawAggregates();
        focus.RecordFocusEvent(0, "a");
        focus.RecordFocusEvent(500, "b");
        focus.RecordFocusEvent(1100, "a");

        focus.RecomputeDerivedMetrics(bucketStartUtcMs: 0, bucketEndUtcMs: 11100);

        Assert.Equal(0, focus.FocusSwitchesCount);
        Assert.Equal(1, focus.ActiveAppCount);
        Assert.True(focus.DwellByAppSeconds.ContainsKey("a"));
        Assert.False(focus.DwellByAppSeconds.ContainsKey("b"));
        Assert.InRange(focus.DwellByAppSeconds["a"], 9.99d, 10.01d);
    }

    [Fact]
    public void Idle_5sIsMicro_130sIsBreak()
    {
        var idle = new IdleRawAggregates();
        idle.RecordSample(0, 0);
        idle.RecordSample(5000, 5);
        idle.RecordSample(10000, 0);
        idle.RecordSample(20000, 0);
        idle.RecordSample(150000, 130);

        idle.RecomputeDerivedMetrics(bucketStartUtcMs: 0, bucketEndUtcMs: 200000);

        Assert.Equal(1, idle.MicroIdleCount);
        Assert.InRange(idle.BreakSeconds, 129.99d, 130.01d);
        Assert.InRange(idle.IdleSeconds, 134.99d, 135.01d);
    }

    [Fact]
    public void Quality_MissingFocusChannel_MarksPartial()
    {
        var bucket = new BucketBuffer(bucketSeq: 1);
        bucket.Keyboard.RecordKeydown(bucket.StartUtcMs + 100, isBackspace: false);
        bucket.Mouse.RecordMove(bucket.StartUtcMs + 200, 10, 0);
        bucket.Idle.RecordSample(bucket.StartUtcMs + 300, 4);

        BucketDataQualityScore quality = BucketDataQualityScorer.Compute(
            bucket,
            new BucketDropCountersDelta(FutureSkewDrops: 0, TooOldDrops: 0, LateFinalDrops: 0));

        Assert.True(quality.Partial);
        Assert.Contains("focus", quality.MissingMajorChannels);
        Assert.DoesNotContain("keyboard", quality.MissingMajorChannels);
        Assert.DoesNotContain("mouse", quality.MissingMajorChannels);
        Assert.DoesNotContain("idle", quality.MissingMajorChannels);
        Assert.Equal(0.8d, quality.QualityScore, precision: 6);
    }

    [Fact]
    public void Quality_KeyboardOnlyBucket_IsNotPartial_WhenFocusAndIdleExist()
    {
        var bucket = new BucketBuffer(bucketSeq: 1);
        bucket.Keyboard.RecordKeydown(bucket.StartUtcMs + 100, isBackspace: false);
        bucket.Focus.RecordFocusEvent(bucket.StartUtcMs + 150, "app");
        bucket.Idle.RecordSample(bucket.StartUtcMs + 200, 0.1);

        BucketDataQualityScore quality = BucketDataQualityScorer.Compute(
            bucket,
            new BucketDropCountersDelta(FutureSkewDrops: 0, TooOldDrops: 0, LateFinalDrops: 0));

        Assert.False(quality.Partial);
        Assert.Contains("mouse", quality.MissingMajorChannels);
        Assert.DoesNotContain("keyboard", quality.MissingMajorChannels);
        Assert.DoesNotContain("focus", quality.MissingMajorChannels);
        Assert.DoesNotContain("idle", quality.MissingMajorChannels);
        Assert.Equal(0.8d, quality.QualityScore, precision: 6);
    }

    [Fact]
    public void Quality_MouseOnlyBucket_IsNotPartial_WhenFocusAndIdleExist()
    {
        var bucket = new BucketBuffer(bucketSeq: 1);
        bucket.Mouse.RecordMove(bucket.StartUtcMs + 100, 12, 4);
        bucket.Focus.RecordFocusEvent(bucket.StartUtcMs + 150, "app");
        bucket.Idle.RecordSample(bucket.StartUtcMs + 200, 0.1);

        BucketDataQualityScore quality = BucketDataQualityScorer.Compute(
            bucket,
            new BucketDropCountersDelta(FutureSkewDrops: 0, TooOldDrops: 0, LateFinalDrops: 0));

        Assert.False(quality.Partial);
        Assert.Contains("keyboard", quality.MissingMajorChannels);
        Assert.DoesNotContain("mouse", quality.MissingMajorChannels);
        Assert.DoesNotContain("focus", quality.MissingMajorChannels);
        Assert.DoesNotContain("idle", quality.MissingMajorChannels);
        Assert.Equal(0.75d, quality.QualityScore, precision: 6);
    }
}
