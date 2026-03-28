using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class CaptureDegradedToastGateTests
{
    [Fact]
    public void ShouldShow_SuppressesRepeatedToastInSameGroup()
    {
        CaptureDegradedToastGate gate = new();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-28T10:00:00Z");
        UserCommunicationToast first = new(
            Key: "capture:keyboard_missing",
            Title: "Ari limited",
            Message: "Keyboard capture is limited.",
            SuppressionKey: "capture:keyboard_limited");
        UserCommunicationToast second = new(
            Key: "capture:keyboard_inactive",
            Title: "Ari limited",
            Message: "Keyboard capture is limited.",
            SuppressionKey: "capture:keyboard_limited");

        Assert.True(gate.ShouldShow(first, nowUtc));
        gate.MarkShown(first, nowUtc);

        Assert.False(gate.ShouldShow(second, nowUtc.AddMinutes(2)));
    }

    [Fact]
    public void ShouldShow_AllowsRepeatAfterStableHealthyWindow()
    {
        CaptureDegradedToastGate gate = new();
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-28T10:00:00Z");
        UserCommunicationToast toast = new(
            Key: "capture:keyboard_missing",
            Title: "Ari limited",
            Message: "Keyboard capture is limited.",
            SuppressionKey: "capture:keyboard_limited");

        Assert.True(gate.ShouldShow(toast, nowUtc));
        gate.MarkShown(toast, nowUtc);

        gate.ObserveHealthyState(nowUtc.AddSeconds(20));
        gate.ObserveHealthyState(nowUtc.AddSeconds(81));
        Assert.True(gate.ShouldShow(toast, nowUtc.AddSeconds(82)));
    }
}
