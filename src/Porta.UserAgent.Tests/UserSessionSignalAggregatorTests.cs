using Porta.UserAgent.Input;

namespace Porta.UserAgent.Tests;

public sealed class UserSessionSignalAggregatorTests
{
    [Fact]
    public void SnapshotAndReset_ComputesKeyboardMouseAndIdleSummariesFromCapturedSamples()
    {
        var aggregator = new UserSessionSignalAggregator();
        DateTimeOffset windowStartUtc = new(2026, 03, 10, 8, 0, 0, TimeSpan.Zero);

        aggregator.RecordKeyboard(isBackspace: false, windowStartUtc.AddMilliseconds(100));
        aggregator.RecordKeyboard(isBackspace: false, windowStartUtc.AddMilliseconds(260));
        aggregator.RecordKeyboard(isBackspace: false, windowStartUtc.AddMilliseconds(410));
        aggregator.RecordKeyboard(isBackspace: true, windowStartUtc.AddMilliseconds(900));

        aggregator.RecordMouseMove(3, 4, windowStartUtc.AddMilliseconds(200));
        aggregator.RecordMouseMove(-3, -4, windowStartUtc.AddMilliseconds(320));
        aggregator.RecordMouseWheel(120, windowStartUtc.AddMilliseconds(500));
        aggregator.RecordMouseWheel(120, windowStartUtc.AddMilliseconds(700));

        aggregator.RecordFocusObservation("app-a", windowStartUtc.AddSeconds(1));
        aggregator.RecordFocusObservation("app-a", windowStartUtc.AddSeconds(2));
        aggregator.RecordFocusObservation("app-b", windowStartUtc.AddSeconds(3));
        aggregator.RecordFocusObservation("app-a", windowStartUtc.AddSeconds(4));

        aggregator.RecordIdleObservation(0d, windowStartUtc.AddSeconds(1));
        aggregator.RecordIdleObservation(4d, windowStartUtc.AddSeconds(4));
        aggregator.RecordIdleObservation(0d, windowStartUtc.AddSeconds(5));

        UserSessionSignalSnapshot snapshot = aggregator.SnapshotAndReset();
        UserSessionKeyboardTelemetrySummary keyboard = snapshot.BuildKeyboardSummary(5d);
        UserSessionMouseTelemetrySummary mouse = snapshot.BuildMouseSummary(5d);
        UserSessionFocusTelemetrySummary focus = snapshot.BuildFocusSummary(windowStartUtc, windowStartUtc.AddSeconds(5), "app-a");
        UserSessionIdleTelemetrySummary idle = snapshot.BuildIdleSummary(windowStartUtc, windowStartUtc.AddSeconds(5), fallbackEndIdleSeconds: 0d);

        Assert.Equal(4, snapshot.KeyboardKeydownCount);
        Assert.Equal(1, snapshot.KeyboardBackspaceCount);
        Assert.Equal(1, keyboard.BurstCount);
        Assert.Equal(1, keyboard.CorrectionSpikeCount);
        Assert.True(keyboard.IkiMeanMs > 0d);

        Assert.Equal(2, mouse.MoveSampleCount);
        Assert.Equal(2, mouse.WheelEventCount);
        Assert.True(mouse.PathLengthPx > 0d);
        Assert.Equal(1, mouse.MicroCorrectionCount);
        Assert.Equal(1, mouse.ScrollBurstCount);

        Assert.Equal(4, focus.ObservationCount);
        Assert.Equal(2, focus.SwitchCount);
        Assert.Equal(2, focus.ActiveAppCount);
        Assert.Equal("app-a", focus.DominantAppHash);

        Assert.Equal(3, idle.ObservationCount);
        Assert.True(idle.IdleSecondsWithinWindow > 0d);
        Assert.Equal(1, idle.MicroIdleCount);
        Assert.True(idle.ShortIdleBoundaryObserved);
    }

    [Fact]
    public void Requeue_PreservesCapturedSamplesAcrossRetry()
    {
        var aggregator = new UserSessionSignalAggregator();
        DateTimeOffset timestampUtc = new(2026, 03, 10, 9, 0, 0, TimeSpan.Zero);

        aggregator.RecordKeyboard(isBackspace: false, timestampUtc);
        aggregator.RecordFocusObservation("app-a", timestampUtc);
        aggregator.RecordIdleObservation(2.5d, timestampUtc);

        UserSessionSignalSnapshot snapshot = aggregator.SnapshotAndReset();
        aggregator.Requeue(snapshot);
        UserSessionSignalSnapshot retried = aggregator.SnapshotAndReset();

        Assert.Single(retried.KeyboardSamples);
        Assert.Single(retried.FocusObservations);
        Assert.Single(retried.IdleObservations);
        Assert.Equal(snapshot.LastInputEventUtc, retried.LastInputEventUtc);
    }
}
