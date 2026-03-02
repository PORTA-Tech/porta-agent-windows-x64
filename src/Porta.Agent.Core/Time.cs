using System.Globalization;

namespace Porta.Agent.Core;

public enum EventTimestampDropReason
{
    None = 0,
    FutureSkew = 1,
    TooOld = 2,
    LateFinal = 3
}

public readonly record struct EventTimestampValidation(
    bool ShouldDrop,
    EventTimestampDropReason DropReason);

public static class Time
{
    private const string UtcIsoWithMillisecondsFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public const int bucketSeconds = 5;
    public const int bucket_ms = 5000;
    public const int maxLateMs = 2000;
    public const int finalizeDelayMs = 2000;
    public const int skew_limit_ms = 10000;
    public const long epoch_anchor_ms = 1704067200000L; // 2024-01-01T00:00:00.000Z

    public static string toIsoUtcMsString(long ms)
    {
        DateTimeOffset utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToUniversalTime();
        return utc.ToString(UtcIsoWithMillisecondsFormat, CultureInfo.InvariantCulture);
    }

    public static long parseIsoUtcMs(string str)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            throw new FormatException("Timestamp cannot be empty.");
        }

        if (!DateTimeOffset.TryParseExact(
                str.Trim(),
                UtcIsoWithMillisecondsFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            throw new FormatException("Timestamp must be UTC ISO-8601 with milliseconds (yyyy-MM-ddTHH:mm:ss.fffZ).");
        }

        return parsed.ToUnixTimeMilliseconds();
    }

    public static long bucketSeqFromEventTs(long ms)
    {
        long deltaMs = checked(ms - epoch_anchor_ms);
        return FloorDiv(deltaMs, bucket_ms);
    }

    public static (long startUtcMs, long endUtcMs) bucketStartEnd(long bucket_seq)
    {
        long start = checked(epoch_anchor_ms + checked(bucket_seq * (long)bucket_ms));
        long end = checked(start + bucket_ms);
        return (start, end);
    }

    public static EventTimestampValidation ValidateEventTimestamp(
        long event_ts_utc_ms,
        long now_utc_ms,
        long retention_window_ms,
        bool bucket_final)
    {
        if (event_ts_utc_ms > checked(now_utc_ms + skew_limit_ms))
        {
            return new EventTimestampValidation(true, EventTimestampDropReason.FutureSkew);
        }

        long boundedRetentionMs = Math.Max(0, retention_window_ms);
        if (event_ts_utc_ms < checked(now_utc_ms - boundedRetentionMs))
        {
            return new EventTimestampValidation(true, EventTimestampDropReason.TooOld);
        }

        if (bucket_final)
        {
            return new EventTimestampValidation(true, EventTimestampDropReason.LateFinal);
        }

        return new EventTimestampValidation(false, EventTimestampDropReason.None);
    }

    private static long FloorDiv(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "Denominator must be greater than zero.");
        }

        long quotient = numerator / denominator;
        long remainder = numerator % denominator;
        if (remainder != 0 && numerator < 0)
        {
            quotient--;
        }

        return quotient;
    }
}
