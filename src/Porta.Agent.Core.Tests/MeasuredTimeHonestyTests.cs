using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Core.Tests;

// Chat 3 (2026-08-24) — measured time is the evidence under the client report, so it has to be
// true. A film played for hours with the widget unanswered was credited as 4.9 hours of work on
// the active task. These are the rules that stop that.
public sealed class MeasuredTimeHonestyTests
{
    // ── the allow-list ────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusIsWork()
        => Assert.True(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.Focus));

    // A meeting IS work. The guard against a call left open in front of an empty chair is the
    // stillness cap, not the working-kind list.
    [Fact]
    public void AMeetingIsWork()
        => Assert.True(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.InMeeting));

    [Fact]
    public void AContextExcursionIsStillWork()
        => Assert.True(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.ContextExcursion));

    [Fact]
    public void AwayIsNotWork()
        => Assert.False(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.Away));

    // The film. This is the bug.
    [Fact]
    public void PassiveConsumptionIsNotWork()
        => Assert.False(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.PassiveConsumption));

    [Fact]
    public void NoDistractionKindIsWork()
    {
        foreach (string kind in AttentionSegmentKinds.AllDistractionKinds)
        {
            Assert.False(
                AttentionSegmentKinds.IsWorkingKind(kind),
                $"{kind} was counted as work");
        }
    }

    // The direction matters: an allow-list means a kind added later is not work until someone
    // decides it is. Under the old deny-list every new kind became work by default.
    [Fact]
    public void TheWorkingListIsExactlyThreeKinds()
    {
        Assert.Equal(3, AttentionSegmentKinds.WorkingKinds.Count);
        Assert.Contains(AttentionSegmentKinds.Focus, AttentionSegmentKinds.WorkingKinds);
        Assert.Contains(AttentionSegmentKinds.InMeeting, AttentionSegmentKinds.WorkingKinds);
        Assert.Contains(AttentionSegmentKinds.ContextExcursion, AttentionSegmentKinds.WorkingKinds);
    }

    // ── unknown is not focus ──────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyKindIsUnknown_NotFocus()
    {
        Assert.Equal(AttentionSegmentKinds.Unknown, AttentionSegmentKinds.NormalizeKind(null));
        Assert.Equal(AttentionSegmentKinds.Unknown, AttentionSegmentKinds.NormalizeKind(""));
        Assert.Equal(AttentionSegmentKinds.Unknown, AttentionSegmentKinds.NormalizeKind("   "));
    }

    [Fact]
    public void AnUnrecognisedKindIsUnknown_NotFocus()
        => Assert.Equal(
            AttentionSegmentKinds.Unknown,
            AttentionSegmentKinds.NormalizeKind("something_the_classifier_invented"));

    [Fact]
    public void UnknownIsNotWork()
    {
        Assert.False(AttentionSegmentKinds.IsWorkingKind(null));
        Assert.False(AttentionSegmentKinds.IsWorkingKind(""));
        Assert.False(AttentionSegmentKinds.IsWorkingKind("something_the_classifier_invented"));
        Assert.False(AttentionSegmentKinds.IsWorkingKind(AttentionSegmentKinds.Unknown));
    }

    // Unknown must not have quietly become "away" either — away is a claim about the user having
    // stepped out, and we do not know that.
    [Fact]
    public void UnknownIsNotAwayEither()
    {
        Assert.False(AttentionSegmentKinds.IsAway(null));
        Assert.False(AttentionSegmentKinds.IsAway(AttentionSegmentKinds.Unknown));
        Assert.False(AttentionSegmentKinds.IsDistraction(AttentionSegmentKinds.Unknown));
    }

    // Known kinds still normalise to themselves — the registry's drift guard depends on it.
    [Fact]
    public void EveryKnownKindStillNormalisesToItself()
    {
        foreach (string kind in AttentionSegmentKinds.AllKinds)
        {
            Assert.Equal(kind, AttentionSegmentKinds.NormalizeKind(kind));
        }
    }

    // ── the meeting stillness cap ─────────────────────────────────────────────────────

    // Two hours of complete stillness is an empty chair, not a meeting.
    [Fact]
    public void TheMeetingStillnessCapIsTwentyMinutes()
        => Assert.Equal(1200d, PortaExecutionConstants.MeetingMeasurement.MaxStillSeconds);

    // ── the stale-task ceiling ────────────────────────────────────────────────────────

    [Fact]
    public void AStaleActiveTaskIsClosedAfterFortyFiveMinutes()
        => Assert.Equal(45d * 60d, SqliteBrainStateStore.StaleActiveTaskSeconds);
}
