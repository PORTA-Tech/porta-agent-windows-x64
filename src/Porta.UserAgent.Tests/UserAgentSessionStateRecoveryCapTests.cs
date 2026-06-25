using Porta.UserAgent.Input;

namespace Porta.UserAgent.Tests;

/// <summary>
/// Caps for the "you were close" recovery nudge (Launch 2.2): at most a few per day and never
/// twice within an hour, on top of the global toast gate.
/// </summary>
public sealed class UserAgentSessionStateRecoveryCapTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-27T08:00:00Z");

    [Fact]
    public void CanShowRecoveryNudge_TrueInitially()
    {
        var state = new UserAgentSessionState();
        Assert.True(state.CanShowRecoveryNudge(Start));
    }

    [Fact]
    public void CanShowRecoveryNudge_FalseWithinAnHourOfLast()
    {
        var state = new UserAgentSessionState();
        state.RecordRecoveryNudgeShown(Start);

        Assert.False(state.CanShowRecoveryNudge(Start.AddMinutes(30)));
        Assert.False(state.CanShowRecoveryNudge(Start.AddMinutes(59)));
        Assert.True(state.CanShowRecoveryNudge(Start.AddMinutes(60)));
    }

    [Fact]
    public void CanShowRecoveryNudge_FalseAfterDailyCapReached()
    {
        var state = new UserAgentSessionState();

        // Three nudges, each at least an hour apart, satisfies the hourly rule but hits the cap.
        state.RecordRecoveryNudgeShown(Start);
        state.RecordRecoveryNudgeShown(Start.AddHours(2));
        state.RecordRecoveryNudgeShown(Start.AddHours(4));

        Assert.False(state.CanShowRecoveryNudge(Start.AddHours(6)));
    }

    [Fact]
    public void CanShowRecoveryNudge_ResetsOnNewDay()
    {
        var state = new UserAgentSessionState();
        state.RecordRecoveryNudgeShown(Start);
        state.RecordRecoveryNudgeShown(Start.AddHours(2));
        state.RecordRecoveryNudgeShown(Start.AddHours(4));
        Assert.False(state.CanShowRecoveryNudge(Start.AddHours(6)));

        state.ResetIfNewDay(Start.AddDays(1));

        Assert.True(state.CanShowRecoveryNudge(Start.AddDays(1)));
    }
}
