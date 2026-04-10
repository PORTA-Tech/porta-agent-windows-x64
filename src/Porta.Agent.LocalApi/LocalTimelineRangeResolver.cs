namespace Porta.Agent.LocalApi;

public static class LocalTimelineRangeResolver
{
    public static DateTimeOffset ResolveLocalDayStartUtc(DateTimeOffset nowUtc, TimeZoneInfo? timeZone = null)
    {
        TimeZoneInfo resolvedTimeZone = timeZone ?? TimeZoneInfo.Local;
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(nowUtc, resolvedTimeZone);
        DateTimeOffset localStartOfDay = new(localNow.Year, localNow.Month, localNow.Day, 0, 0, 0, localNow.Offset);
        return localStartOfDay.ToUniversalTime();
    }
}
