namespace Porta.Agent.Core.Tests.Harness;

public sealed class DeterministicTestClock
{
    public DeterministicTestClock(DateTimeOffset startUtc, int bucketSeconds = 5)
    {
        if (bucketSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds), bucketSeconds, "Bucket size must be greater than zero.");
        }

        UtcNow = startUtc;
        BucketSeconds = bucketSeconds;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public int BucketSeconds { get; }

    public DateTimeOffset Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(by), by, "Clock cannot move backwards.");
        }

        UtcNow = UtcNow.Add(by);
        return UtcNow;
    }

    public DateTimeOffset AdvanceSeconds(int seconds)
    {
        if (seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Clock cannot move backwards.");
        }

        return Advance(TimeSpan.FromSeconds(seconds));
    }

    public DateTimeOffset AdvanceBuckets(int bucketCount = 1)
    {
        if (bucketCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), bucketCount, "Clock cannot move backwards.");
        }

        return AdvanceSeconds(checked(bucketCount * BucketSeconds));
    }
}
