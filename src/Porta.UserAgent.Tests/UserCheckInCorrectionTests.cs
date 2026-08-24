using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Phase 5 (2026-08-24) - the evening card's correction, and the one re-arm "not done yet" buys.
//
// The whole point of the correction is that the card was WRONG about the day, so the tests here are
// mostly about what must NOT happen afterwards: the day must not be stamped, the card must not come
// straight back, and it must not come back forever.
public sealed class UserCheckInCorrectionTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);

    private static DateTimeOffset At(int hour, int minute = 0) => new(2026, 8, 24, hour, minute, 0, Off);

    private static Func<UserCheckInKind, bool> NoneOffered => _ => false;

    // An 8-hour day starting at 08:00, so the predicted end is 16:00 and the evening card is due.
    private static UserCheckInDayTiming.WorkDaySnapshot EveningDay()
        => new(
            WorkStartLocal: At(8),
            DayLength: TimeSpan.FromHours(8),
            ActiveTaskSpent: null,
            ActiveTaskBudget: null,
            ActiveWorkToday: TimeSpan.FromHours(6),
            IdleFor: TimeSpan.FromMinutes(20));

    private static DailyGoalSnapshot OpenDay => new(true, true, false, At(8));

    private static UserCheckInKind? Decide(
        DateTimeOffset now,
        UserCheckInScheduler.EveningCorrectionState corrections)
        => UserCheckInScheduler
            .DecideCheckIn(now, OpenDay, EveningDay(), NoneOffered, corrections)
            .Kind;

    // ---- the gate itself -------------------------------------------------------------------

    [Fact]
    public void NoCorrection_AllowsTheEveningCard()
    {
        Assert.True(UserCheckInScheduler.EveningCorrectionAllows(
            UserCheckInScheduler.EveningCorrectionState.None, At(17)));
    }

    [Fact]
    public void NotDoneYet_SuppressesTheCardBeforeTheReArmDelay()
    {
        var corrected = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 1, WasntWorkingToday: false, LastCorrectedLocal: At(17));

        // 89 minutes later is still inside the delay: the card owes the user the full 90.
        Assert.False(UserCheckInScheduler.EveningCorrectionAllows(corrected, At(18, 29)));
    }

    [Fact]
    public void NotDoneYet_AllowsTheCardOnceTheReArmDelayHasPassed()
    {
        var corrected = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 1, WasntWorkingToday: false, LastCorrectedLocal: At(17));

        Assert.True(UserCheckInScheduler.EveningCorrectionAllows(corrected, At(18, 30)));
    }

    [Fact]
    public void NotDoneYetTwice_EndsTheEveningCardForTheDay()
    {
        var correctedTwice = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 2, WasntWorkingToday: false, LastCorrectedLocal: At(17));

        // Even a full day later in the same goal-day, a third card is not offered.
        Assert.False(UserCheckInScheduler.EveningCorrectionAllows(correctedTwice, At(23, 59)));
    }

    [Fact]
    public void WasntWorking_EndsTheEveningCardForTheDayImmediately()
    {
        var notWorking = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 0, WasntWorkingToday: true, LastCorrectedLocal: At(17));

        Assert.False(UserCheckInScheduler.EveningCorrectionAllows(notWorking, At(23, 59)));
    }

    [Fact]
    public void WasntWorking_OutranksAnEarlierNotDoneYetThatWouldOtherwiseBeDue()
    {
        // Corrected once at 17:00, then told "wasn't working" at 17:30. By 19:00 the re-arm delay
        // has passed, but the day-level answer is the later and broader one and it wins.
        var both = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 1, WasntWorkingToday: true, LastCorrectedLocal: At(17, 30));

        Assert.False(UserCheckInScheduler.EveningCorrectionAllows(both, At(19)));
    }

    [Fact]
    public void CorrectedWithAnUnreadableTimestamp_StaysQuietRatherThanAskingImmediately()
    {
        var corrected = new UserCheckInScheduler.EveningCorrectionState(
            NotDoneYetCount: 1, WasntWorkingToday: false, LastCorrectedLocal: null);

        Assert.False(UserCheckInScheduler.EveningCorrectionAllows(corrected, At(23)));
    }

    // ---- the gate as the scheduler applies it ----------------------------------------------

    [Fact]
    public void EveningCardIsDue_WhenNothingHasBeenCorrected()
        => Assert.Equal(
            UserCheckInKind.EndOfDay,
            Decide(At(17), UserCheckInScheduler.EveningCorrectionState.None));

    [Fact]
    public void EveningCardIsNotDue_RightAfterNotDoneYet()
        => Assert.Null(Decide(
            At(17, 5),
            new UserCheckInScheduler.EveningCorrectionState(1, false, At(17))));

    [Fact]
    public void EveningCardComesBackOnce_AfterTheReArmDelay()
        => Assert.Equal(
            UserCheckInKind.EndOfDay,
            Decide(At(18, 31), new UserCheckInScheduler.EveningCorrectionState(1, false, At(17))));

    [Fact]
    public void EveningCardNeverComesBack_AfterWasntWorking()
        => Assert.Null(Decide(
            At(22), new UserCheckInScheduler.EveningCorrectionState(0, true, At(17))));

    // A correction must not resurrect the card on a day that was properly closed, and must not
    // reach past the other gates. The eod stamp still wins on its own.
    [Fact]
    public void AClosedDayStaysClosed_EvenWithAReArmDue()
    {
        DailyGoalSnapshot closed = new(true, true, EndOfDayRecorded: true, At(8));
        Assert.Null(UserCheckInScheduler.DecideCheckIn(
            At(18, 31),
            closed,
            EveningDay(),
            NoneOffered,
            new UserCheckInScheduler.EveningCorrectionState(1, false, At(17))).Kind);
    }

    // ---- the response shape ------------------------------------------------------------------

    [Fact]
    public void ACorrectionResponseIsRecognisedAsOne()
    {
        var response = new UserCheckInResponse(
            UserCheckInKind.EndOfDay, null, null, Dismissed: false,
            TaskOutcomes: null, Correction: UserCheckInCorrections.NotDoneYet);

        Assert.True(response.IsCorrection);

        // The two things the EOD branch keys off must be untouched, or a correction would be
        // indistinguishable from a real close-out that ticked nothing.
        Assert.Null(response.TaskOutcomes);
        Assert.Null(response.TextReply);
        Assert.Null(response.Choice);
    }

    [Fact]
    public void AnOrdinaryCloseOutIsNotACorrection()
    {
        var response = new UserCheckInResponse(
            UserCheckInKind.EndOfDay, null, null, Dismissed: false,
            TaskOutcomes: Array.Empty<UserCheckInTaskOutcome>());

        Assert.False(response.IsCorrection);
    }

    [Fact]
    public void ADismissalIsNotACorrection()
        => Assert.False(UserCheckInResponse.DismissedFor(UserCheckInKind.EndOfDay).IsCorrection);

    [Fact]
    public void OnlyTheTwoKnownCorrectionsAreAccepted()
    {
        Assert.True(UserCheckInCorrections.IsKnown(UserCheckInCorrections.NotDoneYet));
        Assert.True(UserCheckInCorrections.IsKnown(UserCheckInCorrections.WasntWorking));
        Assert.False(UserCheckInCorrections.IsKnown("done"));
        Assert.False(UserCheckInCorrections.IsKnown(""));
        Assert.False(UserCheckInCorrections.IsKnown(null));
    }

    // The copy is the agreed mockup's, and the second chip says "today" - it is a statement about
    // the whole day, which is exactly why it ends the evening card for the day.
    [Fact]
    public void TheCardOffersExactlyTwoCorrections_WithTheAgreedCopy()
    {
        Assert.Equal(2, UserCheckInCorrections.Options.Length);
        Assert.Equal("Not done yet", UserCheckInCorrections.Options[0].Label);
        Assert.Equal("Wasn't working today", UserCheckInCorrections.Options[1].Label);
        Assert.Equal("Ari thinks your day is done.", UserCheckInCorrections.AssumptionLabel);
    }
}
