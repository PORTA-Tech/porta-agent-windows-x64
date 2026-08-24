using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Phase 5 (2026-08-24) - the record behind the evening card's correction (migration V33).
//
// This table is the only honest signal about whether Ari reads a day correctly, so the assertions
// here are as much about what a correction must NOT do (touch the day it corrects) as about what
// it stores.
public sealed class CheckInCorrectionsStoreTests
{
    private const string Date = "2026-08-24";
    private static readonly DateTimeOffset Now = new(2026, 08, 24, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Correction_IsStoredWithWhatAriAssumed()
    {
        RunWithStore((store, _) =>
        {
            AgentCheckInCorrection? row = store.RecordCheckInCorrection(
                Date,
                AgentCheckInCorrection.EndOfDayCard,
                AgentCheckInCorrection.NotDoneYet,
                assumedEndLocal: "2026-08-24 16:00",
                assumedActiveSeconds: 21600d,
                assumedFinishedCount: 2,
                assumedOpenCount: 1,
                Now);

            Assert.NotNull(row);
            Assert.Equal(Date, row!.LocalDate);
            Assert.Equal("end_of_day", row.CardKind);
            Assert.Equal("not_done_yet", row.Correction);
            Assert.Equal("2026-08-24 16:00", row.AssumedEndLocal);
            Assert.Equal(21600d, row.AssumedActiveSeconds);
            Assert.Equal(2, row.AssumedFinishedCount);
            Assert.Equal(1, row.AssumedOpenCount);

            IReadOnlyList<AgentCheckInCorrection> read = store.ReadCheckInCorrections(Date);
            Assert.Single(read);
            Assert.Equal(row.Id, read[0].Id);
        });
    }

    // Append-only. The second correction on a day is what ends the evening card, so it must remain
    // distinguishable from the first rather than overwriting it.
    [Fact]
    public void TwoCorrectionsOnADay_AreTwoRows_OldestFirst()
    {
        RunWithStore((store, _) =>
        {
            store.RecordCheckInCorrection(
                Date, AgentCheckInCorrection.EndOfDayCard, AgentCheckInCorrection.NotDoneYet,
                "2026-08-24 16:00", 21600d, 2, 1, Now);
            store.RecordCheckInCorrection(
                Date, AgentCheckInCorrection.EndOfDayCard, AgentCheckInCorrection.NotDoneYet,
                "2026-08-24 17:30", 25200d, 2, 1, Now.AddMinutes(95));

            IReadOnlyList<AgentCheckInCorrection> read = store.ReadCheckInCorrections(Date);
            Assert.Equal(2, read.Count);
            Assert.True(read[0].CorrectedAtUtc < read[1].CorrectedAtUtc);
        });
    }

    // The whole point: "not done yet" means the day is NOT over, so nothing may stamp it.
    [Fact]
    public void ACorrection_DoesNotCloseTheDay()
    {
        RunWithStore((store, _) =>
        {
            store.UpsertMorningGoal(Date, "landing page copy", "keep_on_track", Now.AddHours(-9));
            store.RecordCheckInCorrection(
                Date, AgentCheckInCorrection.EndOfDayCard, AgentCheckInCorrection.NotDoneYet,
                "2026-08-24 16:00", 21600d, 2, 1, Now);

            AgentDailyGoal? goal = store.GetDailyGoal(Date);
            Assert.NotNull(goal);
            Assert.Null(goal!.EndOfDayAtUtc);
            Assert.Equal(string.Empty, goal.PlanTomorrow);
        });
    }

    [Fact]
    public void AnUnknownCorrection_IsRefused()
    {
        RunWithStore((store, _) =>
        {
            Assert.Null(store.RecordCheckInCorrection(
                Date, AgentCheckInCorrection.EndOfDayCard, "maybe",
                string.Empty, 0d, 0, 0, Now));
            Assert.Empty(store.ReadCheckInCorrections(Date));
        });
    }

    [Fact]
    public void CorrectionsAreScopedToTheirGoalDay()
    {
        RunWithStore((store, _) =>
        {
            store.RecordCheckInCorrection(
                Date, AgentCheckInCorrection.EndOfDayCard, AgentCheckInCorrection.WasntWorking,
                string.Empty, 0d, 0, 0, Now);

            Assert.Single(store.ReadCheckInCorrections(Date));
            Assert.Empty(store.ReadCheckInCorrections("2026-08-25"));
        });
    }

    [Fact]
    public void ADayWithNoCorrections_ReadsEmpty()
        => RunWithStore((store, _) => Assert.Empty(store.ReadCheckInCorrections(Date)));

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-correction-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
