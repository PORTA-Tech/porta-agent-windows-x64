using Porta.Agent.Common;

namespace Porta.Agent.Core.Tests;

// Phase 2c — the quick-add parser. Deliberately minimal grammar: "in Nm", "in Nh",
// "at HH:MM [am/pm]", "tomorrow [H[:MM]] [am/pm]"; anything else falls back to +20m with
// TimeRecognized=false so the widget's preview stays honest.
public sealed class ReminderTimeParserTests
{
    // Tuesday 14:00 local (offset +05:00 like the dev machine — parser must respect it).
    private static readonly DateTimeOffset Now = new(2026, 07, 07, 14, 0, 0, TimeSpan.FromHours(5));

    [Fact]
    public void InMinutes_ParsesTitleAndDue()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("standup in 20m", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Standup", r.Title);
        Assert.Equal(Now.AddMinutes(20), r.DueAt);
        Assert.Equal("in 20m", r.PreviewLabel);
    }

    [Fact]
    public void InMinutes_LongForm()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("check oven in 45 minutes", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Check oven", r.Title);
        Assert.Equal(Now.AddMinutes(45), r.DueAt);
    }

    [Fact]
    public void InHours_Parses()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("ship build in 2h", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Ship build", r.Title);
        Assert.Equal(Now.AddHours(2), r.DueAt);
        Assert.Equal("in 2h", r.PreviewLabel);
    }

    [Fact]
    public void AtClock_FutureToday()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("ship build at 16:30", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Ship build", r.Title);
        Assert.Equal(new DateTimeOffset(2026, 07, 07, 16, 30, 0, Now.Offset), r.DueAt);
        Assert.Equal("at 16:30", r.PreviewLabel);
    }

    [Fact]
    public void AtClock_PastTodayRollsToTomorrow()
    {
        // 09:00 already passed (now is 14:00) → tomorrow 09:00.
        ReminderParseResult r = ReminderTimeParser.Parse("review at 9:00", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal(new DateTimeOffset(2026, 07, 08, 9, 0, 0, Now.Offset), r.DueAt);
    }

    [Fact]
    public void AtClock_PmSuffix()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("call kamal at 4:30 pm", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Call kamal", r.Title);
        Assert.Equal(new DateTimeOffset(2026, 07, 07, 16, 30, 0, Now.Offset), r.DueAt);
    }

    [Fact]
    public void AtClock_BareHourWithoutAmPm_IsNotAccepted()
    {
        // "at 7" is too ambiguous — falls back to the +20m default.
        ReminderParseResult r = ReminderTimeParser.Parse("dinner at 7", Now);
        Assert.False(r.TimeRecognized);
        Assert.Equal(Now.AddMinutes(20), r.DueAt);
    }

    [Fact]
    public void AtClock_BareHourWithAmPm_IsAccepted()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("dinner at 7 pm", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal(new DateTimeOffset(2026, 07, 07, 19, 0, 0, Now.Offset), r.DueAt);
    }

    [Fact]
    public void Tomorrow_WithTime()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("call kamal tomorrow 9am", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Call kamal", r.Title);
        Assert.Equal(new DateTimeOffset(2026, 07, 08, 9, 0, 0, Now.Offset), r.DueAt);
        Assert.Equal("tomorrow 09:00", r.PreviewLabel);
    }

    [Fact]
    public void Tomorrow_Bare_DefaultsToNine()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("submit report tomorrow", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Submit report", r.Title);
        Assert.Equal(new DateTimeOffset(2026, 07, 08, 9, 0, 0, Now.Offset), r.DueAt);
    }

    // ── Workstream 2: expanded grammar — bare durations, word forms, combined h+m ──

    [Theory]
    [InlineData("standup 20m", 20, "Standup")]
    [InlineData("ship build 1h", 60, "Ship build")]
    [InlineData("review 90min", 90, "Review")]
    [InlineData("deep work 2 hours", 120, "Deep work")]
    [InlineData("in 2 hours deep work", 120, "Deep work")]
    public void BareAndWordDurations_Parse(string input, int minutes, string title)
    {
        ReminderParseResult r = ReminderTimeParser.Parse(input, Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal(ReminderTimeKind.Duration, r.Kind);
        Assert.Equal(minutes, r.DurationMinutes);
        Assert.Equal(title, r.Title);
        Assert.Equal(Now.AddMinutes(minutes), r.DueAt);
    }

    [Theory]
    [InlineData("call mom an hour", 60)]
    [InlineData("call mom in an hour", 60)]
    [InlineData("call mom one hour", 60)]
    [InlineData("stretch half an hour", 30)]
    [InlineData("stretch in half an hour", 30)]
    [InlineData("stretch half hour", 30)]
    public void WordHourForms_Parse(string input, int minutes)
    {
        ReminderParseResult r = ReminderTimeParser.Parse(input, Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal(minutes, r.DurationMinutes);
        Assert.Equal(Now.AddMinutes(minutes), r.DueAt);
    }

    [Theory]
    [InlineData("focus 1h30", 90)]
    [InlineData("focus in 1h30", 90)]
    [InlineData("focus 1h30m", 90)]
    [InlineData("focus 2h30", 150)]
    [InlineData("focus in 1 hour 30", 90)]
    [InlineData("focus 2 hours 15", 135)]
    public void CombinedHoursMinutes_Parse(string input, int minutes)
    {
        ReminderParseResult r = ReminderTimeParser.Parse(input, Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal(ReminderTimeKind.Duration, r.Kind);
        Assert.Equal(minutes, r.DurationMinutes);
        Assert.Equal("Focus", r.Title);
    }

    [Fact]
    public void CombinedHoursMinutes_PreviewIsCompact()
    {
        Assert.Equal("in 1h30m", ReminderTimeParser.Parse("focus 1h30", Now).PreviewLabel);
        Assert.Equal("in 1h30m", ReminderTimeParser.Parse("review 90min", Now).PreviewLabel);
        Assert.Equal("in 2h", ReminderTimeParser.Parse("ship in 2 hours", Now).PreviewLabel);
    }

    [Fact]
    public void ClockForms_ReportClockKind()
    {
        Assert.Equal(ReminderTimeKind.Clock, ReminderTimeParser.Parse("ship at 16:30", Now).Kind);
        Assert.Equal(ReminderTimeKind.Clock, ReminderTimeParser.Parse("ship tomorrow 9am", Now).Kind);
        Assert.Equal(ReminderTimeKind.None, ReminderTimeParser.Parse("banana", Now).Kind);
    }

    [Fact]
    public void Garbage_FallsBackToDefault()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("banana", Now);
        Assert.False(r.TimeRecognized);
        Assert.Equal("Banana", r.Title);
        Assert.Equal(Now.AddMinutes(20), r.DueAt);
        Assert.Equal("in 20m", r.PreviewLabel);
    }

    [Fact]
    public void Empty_FallsBackToDefaultTitleAndTime()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("   ", Now);
        Assert.False(r.TimeRecognized);
        Assert.Equal("Reminder", r.Title);
        Assert.Equal(Now.AddMinutes(20), r.DueAt);
    }

    [Fact]
    public void TimeOnly_NoTitle_UsesDefaultTitle()
    {
        ReminderParseResult r = ReminderTimeParser.Parse("in 15m", Now);
        Assert.True(r.TimeRecognized);
        Assert.Equal("Reminder", r.Title);
        Assert.Equal(Now.AddMinutes(15), r.DueAt);
    }

    [Fact]
    public void InvalidClockValues_FallBack()
    {
        Assert.False(ReminderTimeParser.Parse("at 25:00 review", Now).TimeRecognized);
        Assert.False(ReminderTimeParser.Parse("at 13 pm review", Now).TimeRecognized);
    }
}
