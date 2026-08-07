using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

// Workstream D — the conversational check-in goals store: one row per local goal-day, the
// morning goal + tracking preference, the midday yes/partly/not_yet answer, the EOD plan, and
// the local-data reset wipe.
public sealed class DailyGoalsStoreTests
{
    private const string Date = "2026-07-16";
    private static readonly DateTimeOffset Now = new(2026, 07, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Morning_UpsertThenGet_ReturnsGoalAndPreference()
    {
        RunWithStore((store, _) =>
        {
            AgentDailyGoal? created = store.UpsertMorningGoal(Date, "ship the digest section", "keep_on_track", Now);
            Assert.NotNull(created);
            Assert.Equal("ship the digest section", created!.GoalText);
            Assert.Equal("keep_on_track", created.TrackingPref);
            Assert.Equal(string.Empty, created.MiddayAnswer);

            AgentDailyGoal? read = store.GetDailyGoal(Date);
            Assert.Equal(created.Id, read!.Id);
            Assert.Equal("ship the digest section", read.GoalText);
        });
    }

    [Fact]
    public void Morning_UpsertTwice_UpdatesInPlace_OneRowPerDay()
    {
        RunWithStore((store, _) =>
        {
            AgentDailyGoal first = store.UpsertMorningGoal(Date, "first", "just_track", Now)!;
            AgentDailyGoal second = store.UpsertMorningGoal(Date, "revised goal", "remind_me", Now.AddMinutes(5))!;

            Assert.Equal(first.Id, second.Id); // same row (unique local_date)
            Assert.Equal("revised goal", store.GetDailyGoal(Date)!.GoalText);
            Assert.Equal("remind_me", store.GetDailyGoal(Date)!.TrackingPref);
        });
    }

    [Theory]
    [InlineData("yes", "yes")]
    [InlineData("Partly", "partly")]
    [InlineData("not yet", "not_yet")]
    [InlineData("NOT_YET", "not_yet")]
    public void Midday_RecordsNormalizedAnswer(string input, string expected)
    {
        RunWithStore((store, _) =>
        {
            store.UpsertMorningGoal(Date, "goal", "just_track", Now);
            AgentDailyGoal? updated = store.RecordMiddayGoalAnswer(Date, input, Now.AddHours(2));
            Assert.Equal(expected, updated!.MiddayAnswer);
            Assert.NotNull(updated.MiddayAtUtc);
        });
    }

    [Fact]
    public void Midday_RejectsUnknownAnswer_OrMissingGoal()
    {
        RunWithStore((store, _) =>
        {
            store.UpsertMorningGoal(Date, "goal", "just_track", Now);
            Assert.Null(store.RecordMiddayGoalAnswer(Date, "maybe", Now));       // unknown answer
            Assert.Null(store.RecordMiddayGoalAnswer("2026-01-01", "yes", Now)); // no goal that day
        });
    }

    [Fact]
    public void EndOfDay_SavesPlan_AndCreatesRowWhenNoMorningGoal()
    {
        RunWithStore((store, _) =>
        {
            // No morning goal was set today; EOD still records the plan for tomorrow.
            AgentDailyGoal? eod = store.RecordEndOfDayPlan(Date, "start with the API contract", Now.AddHours(8));
            Assert.NotNull(eod);
            Assert.Equal("start with the API contract", eod!.PlanTomorrow);
            Assert.NotNull(eod.EndOfDayAtUtc);
            Assert.Equal(string.Empty, eod.GoalText); // no morning goal
        });
    }

    [Fact]
    public void FullLocalDataReset_WipesDailyGoals()
    {
        RunWithStore((store, _) =>
        {
            store.UpsertMorningGoal(Date, "goal", "just_track", Now);
            Assert.NotNull(store.GetDailyGoal(Date));

            store.DeleteLocalData();

            Assert.Null(store.GetDailyGoal(Date));
        });
    }

    [Theory]
    [InlineData(2026, 7, 16, 14, 0, "2026-07-16")] // afternoon → today
    [InlineData(2026, 7, 16, 23, 30, "2026-07-16")] // late night → still today
    [InlineData(2026, 7, 16, 4, 59, "2026-07-15")] // before 5am → yesterday's goal-day
    [InlineData(2026, 7, 16, 5, 0, "2026-07-16")] // 5am → today
    public void GoalDay_UsesTheFiveAmBoundary(int year, int month, int day, int hour, int minute, string expected)
    {
        var now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(4));
        Assert.Equal(expected, AgentCoreController.GoalDay(now));
    }

    // Step 3 — the task list and the goal row are now two halves of one day, so the boundary that
    // decides which day it is has to be the same one. GoalDayStartLocal is GoalDay expressed as a
    // timestamp: it must always land on 05:00 of exactly the day GoalDay names, including inside
    // the 00:00–05:00 window where the two used to disagree.
    [Theory]
    [InlineData(2026, 7, 16, 14, 0)]   // afternoon
    [InlineData(2026, 7, 16, 23, 30)]  // late night
    [InlineData(2026, 7, 16, 4, 59)]   // before 5am → yesterday's goal-day
    [InlineData(2026, 7, 16, 5, 0)]    // exactly 5am → today
    [InlineData(2026, 7, 16, 0, 0)]    // midnight — the case local-midnight got wrong
    public void GoalDayStartLocal_IsFiveAmOfTheDayGoalDayNames(int year, int month, int day, int hour, int minute)
    {
        var now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(4));

        DateTimeOffset start = AgentCoreController.GoalDayStartLocal(now);

        Assert.Equal(AgentCoreController.GoalDay(now), start.ToString("yyyy-MM-dd"));
        Assert.Equal(5, start.Hour);
        Assert.Equal(0, start.Minute);
        // The day cannot start in the future, or "today's tasks" would be empty by construction.
        Assert.True(start <= now);
    }

    // ── Part 3 — memory: plan_tomorrow on day N carries into the morning of day N+1 ──

    [Fact]
    public void CarryOver_PlanOnDayN_SurfacesOnMorningOfDayN1()
    {
        RunWithStore((store, _) =>
        {
            // Night of day N: the EOD check-in records tomorrow's plan.
            store.RecordEndOfDayPlan(Date, "start with the API contract", Now.AddHours(10));

            // Morning of day N+1: no goal row yet for the new day.
            AgentDailyGoal? today = store.GetDailyGoal("2026-07-17");
            AgentDailyGoal? yesterday = store.GetDailyGoal(Date);

            Assert.Equal(
                "start with the API contract",
                AgentCoreController.ResolveCarriedOverPlan(today, yesterday));
        });
    }

    [Fact]
    public void CarryOver_SuppressedOnceTodayHasItsOwnGoal()
    {
        RunWithStore((store, _) =>
        {
            store.RecordEndOfDayPlan(Date, "old plan", Now.AddHours(10));
            store.UpsertMorningGoal("2026-07-17", "a fresh goal", "keep_on_track", Now.AddDays(1));

            AgentDailyGoal? today = store.GetDailyGoal("2026-07-17");
            AgentDailyGoal? yesterday = store.GetDailyGoal(Date);

            Assert.Null(AgentCoreController.ResolveCarriedOverPlan(today, yesterday));
        });
    }

    [Fact]
    public void CarryOver_NothingWithoutAPlan_OrWithoutYesterdayRow()
    {
        Assert.Null(AgentCoreController.ResolveCarriedOverPlan(today: null, yesterday: null));

        var emptyPlan = new AgentDailyGoal(
            Id: 1, LocalDate: Date, GoalText: "", TrackingPref: "", MiddayAnswer: "",
            MiddayAtUtc: null, EndOfDayAtUtc: Now, PlanTomorrow: "   ", CreatedAtUtc: Now);
        Assert.Null(AgentCoreController.ResolveCarriedOverPlan(today: null, yesterday: emptyPlan));
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-goal-tests", Guid.NewGuid().ToString("N"));
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
