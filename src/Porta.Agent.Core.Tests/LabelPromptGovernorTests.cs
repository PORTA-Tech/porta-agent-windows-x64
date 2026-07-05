using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 3a: the pure governor that decides whether an active one-tap prompt is warranted.
public sealed class LabelPromptGovernorTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 01, 12, 0, 0, TimeSpan.Zero);

    // A moment where a prompt SHOULD fire: mid confidence, calibrated, focused, low pressure,
    // no recent prompts.
    private static LabelPromptGovernorInput Good() => new(
        NowUtc: Now,
        Confidence: 0.5,
        PressureLevel: 0.2,
        CalibrationStage: "calibrated",
        DominantState: "focus",
        SuggestionsEnabled: true,
        Paused: false,
        MeetingInProgress: false,
        ActivePromptsLast24h: 0,
        LastActivePromptAtUtc: null);

    [Fact]
    public void GoodMoment_PromptsForFocus()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good());
        Assert.True(d.ShouldPrompt);
        Assert.Equal("focus_state", d.LabelTarget);
        Assert.Equal("focused_now", d.PromptKind);
        Assert.Equal("prompt_due", d.ReasonCode);
    }

    [Fact]
    public void DistractionState_PromptsForDistraction()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { DominantState = "off_task_distraction" });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("distraction", d.LabelTarget);
        Assert.Equal("on_task_now", d.PromptKind);
    }

    [Fact]
    public void FocusedButElevatedPressure_PromptsForOverload()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { PressureLevel = 0.6 });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("overload", d.LabelTarget);
        Assert.Equal("overload_now", d.PromptKind);
    }

    [Fact]
    public void Paused_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { Paused = true }).ShouldPrompt);
    }

    [Fact]
    public void Meeting_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { MeetingInProgress = true }).ShouldPrompt);
    }

    [Fact]
    public void SuggestionsDisabled_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { SuggestionsEnabled = false }).ShouldPrompt);
    }

    [Fact]
    public void ColdStart_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { CalibrationStage = "cold" }).ShouldPrompt);
        Assert.False(LabelPromptGovernor.Decide(Good() with { CalibrationStage = "" }).ShouldPrompt);
    }

    [Fact]
    public void DailyCapReached_DoesNotPrompt()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(
            Good() with { ActivePromptsLast24h = PortaExecutionConstants.LabelCapture.MaxActivePromptsPerDay });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("daily_cap_reached", d.ReasonCode);
    }

    [Fact]
    public void WithinMinInterval_DoesNotPrompt()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { LastActivePromptAtUtc = Now.AddMinutes(-30) });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("min_interval_not_elapsed", d.ReasonCode);
    }

    [Fact]
    public void AfterMinInterval_Prompts()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(
            Good() with { LastActivePromptAtUtc = Now.AddMinutes(-(PortaExecutionConstants.LabelCapture.MinMinutesBetweenPrompts + 1)) });
        Assert.True(d.ShouldPrompt);
    }

    [Fact]
    public void ConfidenceTooHigh_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { Confidence = 0.95 }).ShouldPrompt);
    }

    [Fact]
    public void ConfidenceTooLow_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { Confidence = 0.1 }).ShouldPrompt);
    }

    // Progressive back-off: a gap that clears the BASE interval is still suppressed once enough recent
    // prompts have gone unanswered, so an ignoring user is nagged less and less.
    [Fact]
    public void UnansweredStreak_ExtendsCooldownBeyondBaseInterval()
    {
        int baseInterval = PortaExecutionConstants.LabelCapture.MinMinutesBetweenPrompts;

        // No back-off: a prompt just past the base interval fires.
        Assert.True(LabelPromptGovernor.Decide(
            Good() with { LastActivePromptAtUtc = Now.AddMinutes(-(baseInterval + 5)) }).ShouldPrompt);

        // 3 unanswered → 2x interval: the same gap is now too short.
        LabelPromptDecision backedOff = LabelPromptGovernor.Decide(Good() with
        {
            LastActivePromptAtUtc = Now.AddMinutes(-(baseInterval + 5)),
            UnansweredPromptsLast24h = 3,
        });
        Assert.False(backedOff.ShouldPrompt);
        Assert.Equal("min_interval_not_elapsed", backedOff.ReasonCode);
    }

    [Fact]
    public void UnansweredStreak_PromptsOnceBackedOffIntervalElapses()
    {
        int baseInterval = PortaExecutionConstants.LabelCapture.MinMinutesBetweenPrompts;

        // 3 unanswered → 2x interval; wait past 2x and it fires again.
        Assert.True(LabelPromptGovernor.Decide(Good() with
        {
            LastActivePromptAtUtc = Now.AddMinutes(-(baseInterval * 2 + 5)),
            UnansweredPromptsLast24h = 3,
        }).ShouldPrompt);
    }

    [Fact]
    public void HeavyUnansweredStreak_AppliesLongestBackoff()
    {
        int baseInterval = PortaExecutionConstants.LabelCapture.MinMinutesBetweenPrompts;

        // 5 unanswered → 4x interval: a gap of 3x is still suppressed.
        Assert.False(LabelPromptGovernor.Decide(Good() with
        {
            LastActivePromptAtUtc = Now.AddMinutes(-(baseInterval * 3)),
            UnansweredPromptsLast24h = 5,
        }).ShouldPrompt);
    }
}
