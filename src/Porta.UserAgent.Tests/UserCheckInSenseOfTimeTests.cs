using Porta.Agent.Common.Analysis;
using Porta.Agent.LocalApi;
using Porta.UserAgent;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Chat 2 (2026-08-24) - Ari's sense of time.
//
// Three symptoms with one root: Ari did not know when the working day started, so every window
// hanging off it was wrong. A morning card at 11pm, a wrap-up at 3pm after a 9am start, and cards
// on a Sunday nobody worked.
public sealed class UserCheckInSenseOfTimeTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 8, 24, hour, minute, 0, Off);

    private static Func<UserCheckInKind, bool> NoneOffered => _ => false;

    private static UserCheckInDayTiming.WorkDaySnapshot Day(
        DateTimeOffset? workStart = null,
        TimeSpan? activeWork = null,
        TimeSpan? idleFor = null,
        TimeSpan? dayLength = null)
        => new(
            WorkStartLocal: workStart ?? At(9),
            DayLength: dayLength ?? TimeSpan.FromHours(8),
            ActiveTaskSpent: null,
            ActiveTaskBudget: null,
            ActiveWorkToday: activeWork ?? TimeSpan.FromHours(6),
            IdleFor: idleFor ?? TimeSpan.FromMinutes(10));

    // ── fix 1: work start must mean work ──────────────────────────────────────────────

    private static LocalApiCurrentSnapshotResponse Bucket(
        double typing = 0d,
        double scroll = 0d,
        double focusSwitch = 0d,
        string kind = AttentionSegmentKinds.Focus)
        => new(
            TimestampUtc: DateTimeOffset.UtcNow,
            TypingBurstIntensity: typing,
            ScrollVelocityIndex: scroll,
            FocusSwitchRate: focusSwitch,
            SmoothedTypingBurstIntensity: 0d,
            SmoothedScrollVelocityIndex: 0d,
            SmoothedFocusSwitchRate: 0d,
            FocusStrength: 0d,
            OverloadLikelihood: 0d,
            FatigueProbability: 0d,
            CognitiveStability: 0d,
            DominantStateValue: 0d,
            DominantStateName: "neutral",
            Confidence: 0d,
            LastAction: string.Empty,
            ActivationWindowTriggered: false,
            CooldownTriggered: false,
            ActivationWindowOpen: false,
            Contributors: Array.Empty<string>(),
            AttentionSegmentKind: kind);

    [Fact]
    public void AnAwayBucketDoesNotStartTheDay()
        => Assert.False(UserAgentHost.IsWorkStartCandidate(
            Bucket(typing: 0.4d, kind: AttentionSegmentKinds.Away)));

    // The overnight case: the machine is on and logged in, buckets are being written, and nobody
    // has touched it. This is what used to declare a 05:00 start.
    [Fact]
    public void AnIdleBucketWithNoInputDoesNotStartTheDay()
        => Assert.False(UserAgentHost.IsWorkStartCandidate(Bucket()));

    [Fact]
    public void ABucketWithTypingStartsTheDay()
        => Assert.True(UserAgentHost.IsWorkStartCandidate(Bucket(typing: 0.2d)));

    [Fact]
    public void ABucketWithOnlyScrollingStartsTheDay()
        => Assert.True(UserAgentHost.IsWorkStartCandidate(Bucket(scroll: 0.1d)));

    [Fact]
    public void ABucketWithOnlyWindowSwitchingStartsTheDay()
        => Assert.True(UserAgentHost.IsWorkStartCandidate(Bucket(focusSwitch: 0.3d)));

    // Chat 2 draws the line at "was anyone there", not at "was it good work". What kind of work it
    // is belongs to the crediting change, and having the rule in two places is how they drift.
    [Fact]
    public void ADistractionBucketWithInputStillStartsTheDay()
        => Assert.True(UserAgentHost.IsWorkStartCandidate(
            Bucket(typing: 0.1d, kind: AttentionSegmentKinds.PassiveConsumption)));

    // ── fix 2: the morning card has an hour ceiling ───────────────────────────────────

    [Fact]
    public void MorningCardIsDueEarly()
        => Assert.Equal(
            UserCheckInKind.Morning,
            UserCheckInScheduler.DecideCheckIn(
                At(9, 5), new DailyGoalSnapshot(false, false, false, null),
                Day(workStart: At(9)), NoneOffered).Kind);

    // SUPERSEDED by item 8 (2026-08-25). This used to assert that NO card appeared at or after
    // 11:00 — which was the hole, not the fix: no card meant no tasks captured, and because the
    // midday and evening arms both gate on the morning having been offered, the whole day then
    // recorded nothing. The ceiling itself is untouched; a second, differently worded card now
    // comes through behind it. See UserCheckInLateStartTests for the rest.
    [Fact]
    public void AtElevenTheOrdinaryMorningCardIsRefusedButThePlanCardStillComes()
    {
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(11), new DailyGoalSnapshot(false, false, false, null),
            Day(workStart: At(11)), NoneOffered);

        Assert.False(UserCheckInDayTiming.IsMorningDue(At(11)).Due);
        Assert.Equal(UserCheckInKind.Morning, kind);
        Assert.Contains("late_start_plan", reason, StringComparison.Ordinal);
    }

    // The symptom exactly: laptop opened at 23:00 on a day that was never worked.
    [Fact]
    public void NoMorningCardAtElevenAtNight()
    {
        (UserCheckInKind? kind, string reason) = UserCheckInScheduler.DecideCheckIn(
            At(23), new DailyGoalSnapshot(false, false, false, null),
            Day(workStart: At(23), activeWork: TimeSpan.Zero), NoneOffered);

        Assert.Null(kind);
        Assert.Contains("morning", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMorningCeilingIsElevenLocal()
    {
        Assert.True(UserCheckInDayTiming.IsMorningDue(At(10, 59)).Due);
        Assert.False(UserCheckInDayTiming.IsMorningDue(At(11, 0)).Due);
        Assert.Equal(11, UserCheckInDayTiming.MorningLatestHour);
    }

    // ── fix 3: no wrap-up on a day with no work ───────────────────────────────────────

    [Fact]
    public void NoEveningCardBelowTheActiveWorkFloor()
    {
        // Well past the predicted end AND past the two-hour hard ceiling, which used to fire
        // unconditionally. Twenty minutes of activity is not a working day.
        UserCheckInDayTiming.TimingDecision decision = UserCheckInDayTiming.IsEveningDue(
            At(22), Day(workStart: At(9), activeWork: TimeSpan.FromMinutes(20)));

        Assert.False(decision.Due);
        Assert.Contains("below_active_work_floor", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEveningCardStillFiresOnARealDay()
        => Assert.True(UserCheckInDayTiming.IsEveningDue(
            At(17, 30), Day(workStart: At(9), activeWork: TimeSpan.FromHours(6))).Due);

    // The floor is the first midday's floor, not a second number invented for this.
    [Fact]
    public void TheEveningFloorIsTheSameNinetyMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), UserCheckInDayTiming.FirstMiddayActiveWorkFloor);

        Assert.False(UserCheckInDayTiming.IsEveningDue(
            At(22), Day(workStart: At(9), activeWork: TimeSpan.FromMinutes(89))).Due);
        Assert.True(UserCheckInDayTiming.IsEveningDue(
            At(22), Day(workStart: At(9), activeWork: TimeSpan.FromMinutes(90))).Due);
    }

    // Symptom B end to end: a 9am start now predicts a 5pm end, not a 1pm one, and nothing fires
    // at 3pm. The old behaviour anchored work start at 05:00 and fired the wrap-up at 15:00.
    [Fact]
    public void AnIneAmStartDoesNotProduceAThreePmWrapUp()
    {
        var day = Day(workStart: At(9), activeWork: TimeSpan.FromHours(5));

        Assert.False(UserCheckInDayTiming.IsEveningDue(At(15), day).Due);
        Assert.Equal(At(17), UserCheckInDayTiming.FractionAt(day, 1d));
    }

    // ── fix 4: light days do not poison the learned day length ────────────────────────

    [Fact]
    public void ADayBelowTheWorkFloorIsNotLearnedFrom()
    {
        RunWithLengthStore(store =>
        {
            store.Observe("2026-08-22", TimeSpan.FromHours(9), TimeSpan.FromMinutes(30));
            Assert.Null(store.AverageRecent("2026-08-24"));
        });
    }

    [Fact]
    public void ARealDayIsLearnedFrom()
    {
        RunWithLengthStore(store =>
        {
            store.Observe("2026-08-22", TimeSpan.FromHours(9), TimeSpan.FromHours(6));
            Assert.Equal(TimeSpan.FromHours(9), store.AverageRecent("2026-08-24"));
        });
    }

    // The creep: four half days that never crossed the floor must not drag a real day down.
    [Fact]
    public void ARunOfLightDaysCannotDragTheAverageDown()
    {
        RunWithLengthStore(store =>
        {
            store.Observe("2026-08-18", TimeSpan.FromHours(8), TimeSpan.FromHours(7));
            store.Observe("2026-08-19", TimeSpan.FromHours(1), TimeSpan.FromMinutes(20));
            store.Observe("2026-08-20", TimeSpan.FromHours(1), TimeSpan.FromMinutes(15));
            store.Observe("2026-08-21", TimeSpan.FromHours(1), TimeSpan.FromMinutes(10));
            store.Observe("2026-08-22", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5));

            // Only the real day counted, so the learned length is still 8h rather than ~2h36m.
            Assert.Equal(TimeSpan.FromHours(8), store.AverageRecent("2026-08-24"));
        });
    }

    // ── fix 5: not working today ──────────────────────────────────────────────────────

    [Fact]
    public void TheFirstCardOfTheDayCarriesTheDayOffTap()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Morning, goal: null, metrics: NoMetrics, offersNotWorkingToday: true);

        Assert.True(prompt.OffersNotWorkingToday);
    }

    [Fact]
    public void LaterCardsDoNotCarryIt()
    {
        UserCheckInPrompt prompt = UserCheckInCoordinator.BuildPrompt(
            UserCheckInKind.Midday, goal: null, metrics: NoMetrics);

        Assert.False(prompt.OffersNotWorkingToday);
    }

    // Any of the four cards can be the first one of a day. Not a [Theory]: UserCheckInKind is
    // internal, so it cannot appear in a public test signature.
    [Fact]
    public void AnyKindCanCarryTheDayOffTap()
    {
        foreach (UserCheckInKind kind in UserCheckInScheduler.AllKinds)
        {
            Assert.True(
                UserCheckInCoordinator
                    .BuildPrompt(kind, goal: null, metrics: NoMetrics, offersNotWorkingToday: true)
                    .OffersNotWorkingToday,
                $"{kind} did not carry the day-off tap");
        }
    }

    [Fact]
    public void TheDayOffResponseIsNotADismissalAndNotACorrection()
    {
        var response = new UserCheckInResponse(
            UserCheckInKind.Morning, null, null, Dismissed: false, NotWorkingToday: true);

        Assert.True(response.NotWorkingToday);
        Assert.False(response.Dismissed);
        Assert.False(response.IsCorrection);
        Assert.Null(response.TextReply);
    }

    [Fact]
    public void EveryCardKindIsMutedByTheDayOffTap()
    {
        // The tap mutes "every card class", so the list it iterates must be all four of them.
        Assert.Equal(4, UserCheckInScheduler.AllKinds.Count);
        Assert.Contains(UserCheckInKind.Morning, UserCheckInScheduler.AllKinds);
        Assert.Contains(UserCheckInKind.Midday, UserCheckInScheduler.AllKinds);
        Assert.Contains(UserCheckInKind.MiddayClose, UserCheckInScheduler.AllKinds);
        Assert.Contains(UserCheckInKind.EndOfDay, UserCheckInScheduler.AllKinds);

        // Each kind must have its OWN mute class, or muting one would silence another by accident.
        var classes = UserCheckInScheduler.AllKinds
            .Select(UserCheckInScheduler.MuteClassFor)
            .ToList();
        Assert.Equal(classes.Count, classes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheDayOffLabelIsSentenceCaseWithNoCapital()
        => Assert.Equal("not working today", UserCheckInDayOff.Label);

    // ── the mute the tap relies on ────────────────────────────────────────────────────

    [Fact]
    public void AMutedClassStaysMutedUntilTheFiveAmBoundary()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "porta-dayoff-tests", Guid.NewGuid().ToString("N"), "mute.json");
        try
        {
            var store = new UserNotificationMuteStore(path);
            store.MuteForToday("checkin:eod", At(14));

            Assert.True(store.IsMutedForToday("checkin:eod", At(23, 59)));
            Assert.True(store.IsMutedForToday("checkin:eod", At(4, 59).AddDays(1)));
            Assert.False(store.IsMutedForToday("checkin:eod", At(5, 0).AddDays(1)));
        }
        finally
        {
            TryDeleteParent(path);
        }
    }

    private static LocalTodaySummaryMetrics NoMetrics => new(
        HasProcessedTimeline: false,
        FocusedTodaySeconds: 0d,
        FocusQualityPercent: 0d,
        DistractionTodaySeconds: 0d,
        AwayTodaySeconds: 0d,
        ContextExcursionTodaySeconds: 0d,
        TrackedTodaySeconds: 0d,
        DisplayedTrackedTodaySeconds: 0d,
        MostDistractedHourLabel: string.Empty);

    private static void RunWithLengthStore(Action<UserWorkDayLengthStore> body)
    {
        string path = Path.Combine(
            Path.GetTempPath(), "porta-daylength-tests", Guid.NewGuid().ToString("N"), "len.json");
        try
        {
            body(new UserWorkDayLengthStore(path));
        }
        finally
        {
            TryDeleteParent(path);
        }
    }

    private static void TryDeleteParent(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
