using Porta.Agent.LocalApi;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Complaint 3, second half (2026-08-11) - "Tomorrow" has to STICK.
//
// "A card that asks me what moves and then forgets is the exact thing this project exists to fix -
// a commitment nobody follows up on is a note, not a commitment. If it doesn't stick, card four is
// theatre."
//
// So these tests are about the follow-through, not the storage: after the close-out card moves a
// task, the evening card must not ask about it again, and must say it is moving.
public sealed class CheckInDeferPersistenceTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(4);
    private static DateTimeOffset At(int hour) => new(2026, 7, 16, hour, 0, 0, Off);

    private static LocalApiTask Task(long id, string title, string deferredUntilDay = "")
        => new(
            Id: id,
            Title: title,
            PlannedSeconds: 3600,
            State: "pending",
            ActiveSeconds: 600,
            FocusedSeconds: 600,
            OfftaskSeconds: 0,
            ExtendedSeconds: 0,
            CreatedAtUtc: At(8).ToUniversalTime(),
            StartedAtUtc: null,
            EndedAtUtc: null,
            DeferredUntilDay: deferredUntilDay);

    [Fact]
    public void MovedWork_IsNotAskedAboutAgainTheSameEvening()
    {
        IReadOnlyList<TaskBoardLine> board = UserCheckInTaskBoard.BuildBoard(
            new[] { Task(1, "Pricing page"), Task(2, "Client revisions", deferredUntilDay: "2026-07-17") },
            At(18));

        IReadOnlyList<TaskBoardLine> ask = UserCheckInTaskBoard.StillToAsk(board);

        Assert.Single(ask);
        Assert.Equal("Pricing page", ask[0].Title);
    }

    [Fact]
    public void MovedWork_IsStatedRatherThanSilentlyDropped()
    {
        IReadOnlyList<TaskBoardLine> board = UserCheckInTaskBoard.BuildBoard(
            new[] { Task(1, "Pricing page"), Task(2, "Client revisions", deferredUntilDay: "2026-07-17") },
            At(18));

        Assert.Contains("Client revisions", UserCheckInTaskBoard.MovingNotice(board));
    }

    [Fact]
    public void NothingMoved_ProducesNoNotice()
    {
        IReadOnlyList<TaskBoardLine> board = UserCheckInTaskBoard.BuildBoard(
            new[] { Task(1, "Pricing page") },
            At(18));

        Assert.Equal(string.Empty, UserCheckInTaskBoard.MovingNotice(board));
    }

    [Fact]
    public void TheDeferralExpiresByItself_SoWorkCannotBeHiddenForever()
    {
        // Stored as a DAY, not a flag. On 07-16 the task is moved out; on 07-17 it is simply open
        // again and the morning card proposes it. No cleanup job, and no way to lose work.
        LocalApiTask moved = Task(1, "Client revisions", deferredUntilDay: "2026-07-17");

        IReadOnlyList<TaskBoardLine> tonight = UserCheckInTaskBoard.BuildBoard(new[] { moved }, At(18));
        Assert.Empty(UserCheckInTaskBoard.StillToAsk(tonight));

        var tomorrow = new DateTimeOffset(2026, 7, 17, 9, 0, 0, Off);
        IReadOnlyList<TaskBoardLine> nextDay = UserCheckInTaskBoard.BuildBoard(new[] { moved }, tomorrow);
        Assert.Single(UserCheckInTaskBoard.StillToAsk(nextDay));
        Assert.Single(UserCheckInTaskBoard.Propose(new[] { moved }, tomorrow));
    }

    [Fact]
    public void AMalformedDayNeverHidesWork()
    {
        // Fails toward visible: an unparseable value must not be able to make a task disappear.
        IReadOnlyList<TaskBoardLine> board = UserCheckInTaskBoard.BuildBoard(
            new[] { Task(1, "Pricing page", deferredUntilDay: "not-a-day-!!") },
            At(18));

        Assert.Single(UserCheckInTaskBoard.StillToAsk(board));
    }

    [Fact]
    public void DeferringDoesNotCloseTheTask()
    {
        // The whole safety property: "Tomorrow" records WHEN, never a state change, so a deferred
        // task is still open work and can never be lost by answering this card.
        IReadOnlyList<TaskBoardLine> board = UserCheckInTaskBoard.BuildBoard(
            new[] { Task(2, "Client revisions", deferredUntilDay: "2026-07-17") },
            At(18));

        Assert.True(board[0].IsOpen);
        Assert.False(board[0].IsDone);
    }
}
