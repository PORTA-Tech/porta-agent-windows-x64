using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Analysis;
using Porta.Agent.Core.Buckets;

namespace Porta.Agent.Core.Tests;

// Phase 2b — meeting measurement: low input while a meeting app is foreground is
// ATTENTIVE-IN-MEETING, never away or distraction. Interaction during a meeting is normal
// focus. A machine parked on a meeting app for hours is away again (the stillness cap).
// Helpers mirror DerivedSessionMetricsComputerTests' private harness (same values).
public sealed class MeetingMeasurementTests
{
    private static class TestDb
    {
        public static SqliteConnection OpenInMemoryWithSchema()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE runtime_state (
                    key TEXT PRIMARY KEY,
                    value_json TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
            return connection;
        }
    }

    private static class TestBuckets
    {
        public static PrimitiveComputationResult Primitive()
        {
            return new PrimitiveComputationResult(
                TypingRaw: 0.3d,
                ScrollRaw: 0.4d,
                FocusRaw: 0.5d,
                TypingSmoothed: 0.3d,
                ScrollSmoothed: 0.4d,
                FocusSmoothed: 0.5d,
                TypingChannelAvailable: true,
                ScrollChannelAvailable: true,
                FocusChannelAvailable: true);
        }

        public static InputBucketRow Stable()
        {
            return new InputBucketRow(
                BucketSeq: 1,
                TimestampUtc: "2026-03-03T00:00:05.000Z",
                StartUtc: "2026-03-03T00:00:05.000Z",
                TypingBurstIntensity: 0.2d,
                ScrollVelocityIndex: 0.3d,
                FocusSwitchRate: 0.10d,
                KeyboardIkiMean: 120d,
                KeyboardIkiCv: 0.2d,
                KeyboardBurstRate: 0.4d,
                KeyboardBackspaceRate: 0.1d,
                MouseSpeedMean: 150d,
                MouseJerkP95: 1000d,
                MouseScrollSpeed: 80d,
                MouseMicroCorrectionRate: 0.1d,
                FocusAppEntropy: 1.0d,
                FocusSwitchCount: 1,
                DominantAppHash: "app_hash_meeting",
                FocusActiveAppCount: 2d,
                IdleSeconds: 0.2d,
                IdleMicroIdleCount: 1d,
                IdleMicroIdleSeconds: 0.2d,
                TrueBreakSeconds: 0d,
                TrueBreakFlag: 0,
                QualityScore: 0.95d,
                Partial: 0,
                ContributorsJson: "{\"schema_ver\":1}");
        }
    }

    [Fact]
    public void IdleDuringMeeting_ClassifiesInMeeting_NotAway()
    {
        using SqliteConnection connection = TestDb.OpenInMemoryWithSchema();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = TestBuckets.Stable() with
        {
            TrueBreakFlag = 1,
            TrueBreakSeconds = 305d,
            IdleSeconds = 305d
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            TestBuckets.Primitive(),
            BaselineGatingResult.Warm,
            meetingInProgress: true);

        Assert.Equal("in_meeting", result.AttentionSegmentKind);
        Assert.Equal("in_meeting", result.AttentionSegmentReasonCode);
        Assert.False(result.DistractionEpisodeActive);
        Assert.True(result.AttentionSegmentSeconds >= 305d);
    }

    [Fact]
    public void IdleWithoutMeeting_StaysAway()
    {
        using SqliteConnection connection = TestDb.OpenInMemoryWithSchema();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = TestBuckets.Stable() with
        {
            TrueBreakFlag = 1,
            TrueBreakSeconds = 305d,
            IdleSeconds = 305d
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            TestBuckets.Primitive(),
            BaselineGatingResult.Warm,
            meetingInProgress: false);

        Assert.Equal("away", result.AttentionSegmentKind);
    }

    [Fact]
    public void ActiveTypingDuringMeeting_StaysNormalFocusPath()
    {
        using SqliteConnection connection = TestDb.OpenInMemoryWithSchema();
        using SqliteTransaction tx = connection.BeginTransaction();

        // Stable, interactive bucket — the meeting flag must not touch the focus path.
        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            TestBuckets.Stable(),
            TestBuckets.Primitive(),
            BaselineGatingResult.Warm,
            meetingInProgress: true);

        Assert.NotEqual("in_meeting", result.AttentionSegmentKind);
        Assert.NotEqual("away", result.AttentionSegmentKind);
        Assert.False(result.DistractionEpisodeActive);
    }

    [Fact]
    public void OverlongStillnessDuringMeeting_BecomesAway()
    {
        using SqliteConnection connection = TestDb.OpenInMemoryWithSchema();
        using SqliteTransaction tx = connection.BeginTransaction();

        InputBucketRow row = TestBuckets.Stable() with
        {
            TrueBreakFlag = 1,
            TrueBreakSeconds = PortaExecutionConstants.MeetingMeasurement.MaxStillSeconds + 60d,
            IdleSeconds = PortaExecutionConstants.MeetingMeasurement.MaxStillSeconds + 60d
        };

        DerivedSessionMetricsResult result = DerivedSessionMetricsComputer.ComputeForFinalizedBucket(
            connection,
            tx,
            row,
            TestBuckets.Primitive(),
            BaselineGatingResult.Warm,
            meetingInProgress: true);

        Assert.Equal("away", result.AttentionSegmentKind);
    }
}
