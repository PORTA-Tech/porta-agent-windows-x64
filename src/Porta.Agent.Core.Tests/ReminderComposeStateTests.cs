using Porta.Agent.Common;

namespace Porta.Agent.Core.Tests;

// Workstream 2 — the pure state machine behind the quick-add time picker. The invariant
// under test everywhere: CanConfirm is true ONLY when an explicit, visible time exists;
// unrecognized text alone can never produce a schedulable snapshot.
public sealed class ReminderComposeStateTests
{
    // Tuesday 14:00 local (offset +05:00 like the dev machine).
    private static readonly DateTimeOffset Now = new(2026, 07, 07, 14, 0, 0, TimeSpan.FromHours(5));

    [Fact]
    public void UnrecognizedText_CannotConfirm()
    {
        var state = new ReminderComposeState();
        state.SetText("call aymene", Now);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.False(snap.CanConfirm);
        Assert.Null(snap.DueAt);
        Assert.Equal(ReminderComposeState.PickTimeLabel, snap.PreviewLabel);
        Assert.Equal("Call aymene", snap.Title);
    }

    [Fact]
    public void TypedDuration_ConfirmsAndHighlightsChip()
    {
        var state = new ReminderComposeState();
        state.SetText("standup in 30m", Now);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.True(snap.CanConfirm);
        Assert.Equal(Now.AddMinutes(30), snap.DueAt);
        Assert.Equal(30, snap.SelectedDurationMinutes);
        Assert.Equal("Standup", snap.Title);
    }

    [Fact]
    public void Chip_SetsDuration()
    {
        var state = new ReminderComposeState();
        state.SetText("call aymene", Now);
        state.SelectChip(60);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.True(snap.CanConfirm);
        Assert.Equal(Now.AddHours(1), snap.DueAt);
        Assert.Equal(60, snap.SelectedDurationMinutes);
    }

    [Fact]
    public void ChipPlusStepper_ReachesNinetyMinutes()
    {
        // The locked decision: 1h30 = 1h chip + 15 + 15.
        var state = new ReminderComposeState();
        state.SelectChip(60);
        state.Step(+15, Now);
        state.Step(+15, Now);
        Assert.Equal(Now.AddMinutes(90), state.Snapshot(Now).DueAt);
    }

    [Fact]
    public void Stepper_FloorsAtFiveMinutes()
    {
        var state = new ReminderComposeState();
        state.SelectChip(10);
        state.Step(-15, Now);
        Assert.Equal(Now.AddMinutes(5), state.Snapshot(Now).DueAt);
    }

    [Fact]
    public void Stepper_WithNothingSelected_PlusSeedsMinusDoesNothing()
    {
        var state = new ReminderComposeState();
        state.Step(-15, Now);
        Assert.False(state.Snapshot(Now).CanConfirm);
        state.Step(+15, Now);
        Assert.Equal(Now.AddMinutes(15), state.Snapshot(Now).DueAt);
    }

    [Fact]
    public void Stepper_SeedsFromTypedDuration()
    {
        var state = new ReminderComposeState();
        state.SetText("review in 1h", Now);
        state.Step(+15, Now);
        Assert.Equal(Now.AddMinutes(75), state.Snapshot(Now).DueAt);
    }

    [Fact]
    public void Stepper_ShiftsTypedClockTime()
    {
        var state = new ReminderComposeState();
        state.SetText("ship at 16:30", Now);
        state.Step(+15, Now);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.Equal(new DateTimeOffset(2026, 07, 07, 16, 45, 0, Now.Offset), snap.DueAt);
    }

    [Fact]
    public void ExactTime_PastRollsToTomorrow()
    {
        var state = new ReminderComposeState();
        Assert.True(state.SetExactTime(9, 0)); // 09:00 already passed (now 14:00)
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.Equal(new DateTimeOffset(2026, 07, 08, 9, 0, 0, Now.Offset), snap.DueAt);
        Assert.StartsWith("Rings tomorrow at 09:00", snap.PreviewLabel);
    }

    [Fact]
    public void ExactTime_RejectsInvalidValues()
    {
        var state = new ReminderComposeState();
        Assert.False(state.SetExactTime(24, 0));
        Assert.False(state.SetExactTime(10, 60));
        Assert.False(state.Snapshot(Now).CanConfirm);
    }

    [Fact]
    public void ManualPick_SurvivesTypingMoreTitleWords()
    {
        var state = new ReminderComposeState();
        state.SetText("call", Now);
        state.SelectChip(120);
        state.SetText("call aymene about the digest", Now);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.Equal(Now.AddMinutes(120), snap.DueAt);
        Assert.Equal(120, snap.SelectedDurationMinutes);
    }

    [Fact]
    public void NewlyTypedTime_ReplacesManualPick()
    {
        var state = new ReminderComposeState();
        state.SelectChip(120);
        state.SetText("ship at 16:30", Now);
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.Equal(new DateTimeOffset(2026, 07, 07, 16, 30, 0, Now.Offset), snap.DueAt);
        Assert.Equal(0, snap.SelectedDurationMinutes);
        Assert.Equal(16, snap.ClockHour);
    }

    [Fact]
    public void Preview_ShowsRingTimeAndCountdown()
    {
        var state = new ReminderComposeState();
        state.SelectChip(30);
        Assert.Equal("Rings at 14:30 · in 30m", state.Snapshot(Now).PreviewLabel);
        state.Step(+15, Now);
        Assert.Equal("Rings at 14:45 · in 45m", state.Snapshot(Now).PreviewLabel);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var state = new ReminderComposeState();
        state.SetText("ship in 2h", Now);
        state.SelectChip(30);
        state.Reset();
        ReminderComposeSnapshot snap = state.Snapshot(Now);
        Assert.False(snap.CanConfirm);
        Assert.Equal("Reminder", snap.Title);
    }
}
