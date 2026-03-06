using Porta.Agent.Core;
using Porta.Agent.LocalApi.Mappers;

namespace Porta.Agent.Core.Tests;

public sealed class LocalApiEventsRecentAcceptanceTests
{
    [Fact]
    public void EventsRecent_ReturnsDescendingByTsThenId()
    {
        DateTimeOffset t0 = DateTimeOffset.UtcNow;
        AgentControlEvent[] events =
        [
            CreateEvent(id: 5, tsUtc: t0.AddSeconds(-2), action: "break_suggestion"),
            CreateEvent(id: 2, tsUtc: t0.AddSeconds(-1), action: "overload_brightness_reduced"),
            CreateEvent(id: 3, tsUtc: t0.AddSeconds(-1), action: "overload_brightness_restored"),
            CreateEvent(id: 1, tsUtc: t0.AddSeconds(-3), action: "deep_work_notifications_suppressed")
        ];

        IReadOnlyList<Porta.Agent.LocalApi.LocalApiEventResponse> mapped = EventMapper.MapRecentOrdered(events);

        Assert.Equal(new long[] { 3L, 2L, 5L, 1L }, mapped.Select(static e => e.id).ToArray());
    }

    [Fact]
    public void EventsRecent_MapsExpectedActionTypesForProofTrail()
    {
        DateTimeOffset ts = DateTimeOffset.UtcNow;
        AgentControlEvent[] events =
        [
            CreateEvent(1, ts, "deep_work_notifications_suppressed"),
            CreateEvent(2, ts, "deep_work_notifications_restored"),
            CreateEvent(3, ts, "overload_brightness_reduced"),
            CreateEvent(4, ts, "overload_brightness_restored"),
            CreateEvent(5, ts, "notification_suppress_failed"),
            CreateEvent(6, ts, "brightness_control_failed"),
            CreateEvent(7, ts, "break_suggestion")
        ];

        IReadOnlyList<Porta.Agent.LocalApi.LocalApiEventResponse> mapped = EventMapper.MapRecentOrdered(events);
        HashSet<string> types = mapped.Select(static e => e.type).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("DEEP_WORK_NOTIFICATIONS_SUPPRESSED", types);
        Assert.Contains("DEEP_WORK_NOTIFICATIONS_RESTORED", types);
        Assert.Contains("OVERLOAD_BRIGHTNESS_REDUCED", types);
        Assert.Contains("OVERLOAD_BRIGHTNESS_RESTORED", types);
        Assert.Contains("NOTIFICATION_SUPPRESS_FAILED", types);
        Assert.Contains("BRIGHTNESS_CONTROL_FAILED", types);
        Assert.Contains("BREAK_SUGGESTION", types);
    }

    [Fact]
    public void EventsRecent_WhenNoEvents_ReturnsEmptyArray()
    {
        IReadOnlyList<Porta.Agent.LocalApi.LocalApiEventResponse> mapped =
            EventMapper.MapRecentOrdered(Array.Empty<AgentControlEvent>());

        Assert.Empty(mapped);
    }

    private static AgentControlEvent CreateEvent(long id, DateTimeOffset tsUtc, string action)
    {
        return new AgentControlEvent(
            TimestampUtc: tsUtc,
            Action: action,
            TriggerState: "test",
            TriggerValue: 0.5d,
            Confidence: 0.8d,
            DurationSeconds: null,
            EscalationLevel: "Suggest",
            Reversible: true,
            Reason: "test",
            Contributors: [],
            EventId: id,
            WhyJson: "{\"schema_ver\":1,\"reason\":\"test\"}",
            StateFlagsJson: "{\"schema_ver\":1}");
    }
}
