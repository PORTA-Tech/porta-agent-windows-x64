using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Workstream A1 — the task title feeds the planned duration, routed through the SAME grammar
// the reminder quick-add uses (ReminderTimeParser). These lock the two things A1 fixes:
// small values are honoured ("2 mins" → 2, not the old silent 30m), and non-durations stay
// unrecognised so Add is blocked rather than defaulting.
public sealed class TaskDurationParseTests
{
    [Theory]
    // The A1 bug: small values used to be floored and swallowed into a 30m default.
    [InlineData("2 mins", 2)]
    [InlineData("2 minutes", 2)]
    [InlineData("1m", 1)]
    [InlineData("2m", 2)]
    [InlineData("3m", 3)]
    [InlineData("5m", 5)]
    // Larger / mixed forms, matched anywhere in the title.
    [InlineData("2h", 120)]
    [InlineData("2h of learning", 120)]
    [InlineData("learn spanish 45m", 45)]
    [InlineData("1h30m", 90)]
    [InlineData("1h30", 90)]
    [InlineData("90 min", 90)]
    [InlineData("45m", 45)]
    [InlineData("2 hours deep work", 120)]
    public void ParsesDuration(string title, int expectedMinutes)
    {
        Assert.True(UserDesktopWidgetWindow.TryParseTaskDurationMinutes(title, out int minutes));
        Assert.Equal(expectedMinutes, minutes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just a normal task")]
    [InlineData("2handball practice")]  // letter+digit run, not "2 hours"
    [InlineData("30many things")]        // not "30 min"
    [InlineData("meeting at 5pm")]       // a clock time is not a task duration
    public void RejectsNonDurations(string? title)
    {
        Assert.False(UserDesktopWidgetWindow.TryParseTaskDurationMinutes(title, out int minutes));
        Assert.Equal(0, minutes);
    }

    // A1 gate: a task is confirmable only with a non-empty title AND a resolved duration.
    // This is the predicate that blocks Add / keeps the button dimmed until a duration exists.
    [Theory]
    [InlineData("learn spanish", 45, true)]
    [InlineData("learn spanish", 0, false)]   // no duration picked/typed → blocked
    [InlineData("", 45, false)]                // no title → blocked
    [InlineData("   ", 45, false)]             // whitespace title → blocked
    [InlineData("x", 2, true)]                 // small durations are valid
    public void CanConfirmTask_GatesOnTitleAndDuration(string? title, int minutes, bool expected)
    {
        Assert.Equal(expected, UserDesktopWidgetWindow.CanConfirmTask(title, minutes));
    }
}
