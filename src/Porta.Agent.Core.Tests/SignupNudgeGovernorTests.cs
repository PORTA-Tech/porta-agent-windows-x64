using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

// Phase 1c: the pure governor that decides whether an unlinked device should be nudged to sign in.
public sealed class SignupNudgeGovernorTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 01, 12, 0, 0, TimeSpan.Zero);

    // A moment where a nudge SHOULD fire: unlinked, 2+ days of data, consent on, no prior nudges.
    private static SignupNudgeGovernorInput Good() => new(
        NowUtc: Now,
        IsLinked: false,
        DaysOfData: 2,
        Paused: false,
        ConsentActive: true,
        NudgesShownTotal: 0,
        LastNudgeAtUtc: null);

    [Fact]
    public void GoodMoment_Nudges()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(Good());
        Assert.True(d.ShouldNudge);
        Assert.Equal("nudge_due", d.ReasonCode);
    }

    [Fact]
    public void Linked_DoesNotNudge()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(Good() with { IsLinked = true });
        Assert.False(d.ShouldNudge);
        Assert.Equal("already_linked", d.ReasonCode);
    }

    [Fact]
    public void InsufficientData_DoesNotNudge()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(Good() with { DaysOfData = 1 });
        Assert.False(d.ShouldNudge);
        Assert.Equal("insufficient_data", d.ReasonCode);
    }

    [Fact]
    public void Paused_DoesNotNudge()
    {
        Assert.False(SignupNudgeGovernor.Decide(Good() with { Paused = true }).ShouldNudge);
    }

    [Fact]
    public void ConsentInactive_DoesNotNudge()
    {
        Assert.False(SignupNudgeGovernor.Decide(Good() with { ConsentActive = false }).ShouldNudge);
    }

    [Fact]
    public void LifetimeCapReached_DoesNotNudge()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(
            Good() with { NudgesShownTotal = PortaExecutionConstants.SignupNudge.MaxLifetimeNudges });
        Assert.False(d.ShouldNudge);
        Assert.Equal("lifetime_cap_reached", d.ReasonCode);
    }

    [Fact]
    public void WithinWeeklyCap_DoesNotNudge()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(
            Good() with { NudgesShownTotal = 1, LastNudgeAtUtc = Now.AddDays(-3) });
        Assert.False(d.ShouldNudge);
        Assert.Equal("weekly_cap_not_elapsed", d.ReasonCode);
    }

    [Fact]
    public void AfterWeeklyInterval_Nudges()
    {
        SignupNudgeDecision d = SignupNudgeGovernor.Decide(
            Good() with
            {
                NudgesShownTotal = 1,
                LastNudgeAtUtc = Now.AddDays(-(PortaExecutionConstants.SignupNudge.MinDaysBetweenNudges + 1)),
            });
        Assert.True(d.ShouldNudge);
    }
}
