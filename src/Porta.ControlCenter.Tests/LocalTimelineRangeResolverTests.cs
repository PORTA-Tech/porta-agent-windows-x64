using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Tests;

public sealed class LocalTimelineRangeResolverTests
{
    [Fact]
    public void ResolveLocalDayStartUtc_UsesLocalDayBoundaryInsteadOfUtcMidnight()
    {
        TimeZoneInfo timeZone = TimeZoneInfo.CreateCustomTimeZone(
            id: "UTC+4",
            baseUtcOffset: TimeSpan.FromHours(4),
            displayName: "UTC+4",
            standardDisplayName: "UTC+4");
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-04-10T15:22:25Z");

        DateTimeOffset fromUtc = LocalTimelineRangeResolver.ResolveLocalDayStartUtc(nowUtc, timeZone);

        Assert.Equal(DateTimeOffset.Parse("2026-04-09T20:00:00Z"), fromUtc);
    }
}
