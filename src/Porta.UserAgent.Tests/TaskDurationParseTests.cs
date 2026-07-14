using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// v1.2.114 — the task title now feeds the planned duration ("2h of learning" → 2h). This
// locks the grammar so a regex tweak can't silently break it, and pins the false-positive
// guards (a bare word that merely contains a letter+digit must NOT read as a duration).
public sealed class TaskDurationParseTests
{
    [Theory]
    [InlineData("2h", 120)]
    [InlineData("2h of learning", 120)]
    [InlineData("learn spanish 45m", 45)]
    [InlineData("1h30m", 90)]
    [InlineData("1h30", 90)]
    [InlineData("1.5h", 90)]
    [InlineData("90 min", 90)]
    [InlineData("45m", 45)]
    [InlineData("2 hours deep work", 120)]
    [InlineData("5m", 5)]
    [InlineData("24h marathon", 720)] // clamped to the 12h ceiling
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
    [InlineData("3m")]                   // below the 5-minute floor
    public void RejectsNonDurations(string? title)
    {
        Assert.False(UserDesktopWidgetWindow.TryParseTaskDurationMinutes(title, out int minutes));
        Assert.Equal(0, minutes);
    }
}
