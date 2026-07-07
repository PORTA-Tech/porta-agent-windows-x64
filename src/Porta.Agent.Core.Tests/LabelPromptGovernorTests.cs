using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

// Phase 1b — Step 3a (v2, moment selection): the pure governor that decides whether an active
// one-tap prompt is warranted, and which question. v2 adds moment gating: a warm-up period after
// activity resumes (kills the "asks the second I log in" failure), an away-return trigger for the
// distraction question, a latched pressure-crossing trigger for the overload question (exempt from
// the confidence band), and a sustained-session + input-lull boundary for the focus question.
public sealed class LabelPromptGovernorTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 01, 12, 0, 0, TimeSpan.Zero);

    // A moment where a FOCUS prompt should fire: mid confidence, calibrated, focused, low
    // pressure, no recent prompts, warmed up, sustained session, natural input lull.
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
        LastActivePromptAtUtc: null,
        SecondsSinceActivityResume: 3600,
        SustainedAttentionSec: 900,
        SecondsSinceLastInput: 30);

    [Fact]
    public void GoodMoment_PromptsForFocus()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good());
        Assert.True(d.ShouldPrompt);
        Assert.Equal("focus_state", d.LabelTarget);
        Assert.Equal("focused_now", d.PromptKind);
        Assert.Equal("prompt_due", d.ReasonCode);
    }

    // --- v2: warm-up gate (the login-prompt fix) ---

    [Fact]
    public void SessionJustResumed_DoesNotPrompt()
    {
        // 2 minutes after login/resume: "were you focused just now?" would label an away window.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { SecondsSinceActivityResume = 120 });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("warmup_not_elapsed", d.ReasonCode);
    }

    [Fact]
    public void UnknownResumeTime_DoesNotPrompt()
    {
        // Unknown (-1) must behave as NOT warmed up — the safe default.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { SecondsSinceActivityResume = -1 });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("warmup_not_elapsed", d.ReasonCode);
    }

    // --- v2: overload fires on a latched pressure crossing, not a poll snapshot ---

    [Fact]
    public void OverloadCrossingLatched_PromptsForOverload()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { OverloadCrossingLatched = true });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("overload", d.LabelTarget);
        Assert.Equal("overload_now", d.PromptKind);
    }

    [Fact]
    public void OverloadCrossing_ExemptFromConfidenceBand()
    {
        // High pressure often comes with out-of-band confidence — exactly when validating the
        // pressure read matters most, so the overload check must not be band-gated.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with
        {
            OverloadCrossingLatched = true,
            Confidence = 0.95,
        });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("overload", d.LabelTarget);
    }

    [Fact]
    public void HighPressureWithoutCrossing_DoesNotPromptForOverload()
    {
        // v1 bug: a high pressure SNAPSHOT alone selected the overload question. v2 requires the
        // latched crossing event; the moment falls through to the focus check instead.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { PressureLevel = 0.7 });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("focus_state", d.LabelTarget);
    }

    [Fact]
    public void OverloadCrossing_StillRequiresWarmup()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with
        {
            OverloadCrossingLatched = true,
            SecondsSinceActivityResume = 60,
        });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("warmup_not_elapsed", d.ReasonCode);
    }

    // --- v2: away-return triggers the distraction question ---

    private static LabelPromptGovernorInput AwayReturn() => Good() with
    {
        // Fires right after the return, so warm-up is definitionally not elapsed — the question
        // is about the PAST away segment, which is exactly why it's exempt.
        SecondsSinceActivityResume = 60,
        AwayReturnActive = true,
        AwayReturnAwayMinutes = 15,
        AwayReturnPriorActiveMinutes = 45,
    };

    [Fact]
    public void AwayReturn_PromptsForDistraction()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn());
        Assert.True(d.ShouldPrompt);
        Assert.Equal("distraction", d.LabelTarget);
        Assert.Equal("away_return", d.PromptKind);
    }

    [Fact]
    public void AwayReturn_WithoutPriorSession_DoesNotPrompt()
    {
        // Morning login: huge "away" but no session before it — never ask.
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn() with { AwayReturnPriorActiveMinutes = 0 });
        Assert.False(d.ShouldPrompt);
    }

    [Fact]
    public void AwayReturn_TooLongAway_DoesNotPrompt()
    {
        // A 10-hour gap is obviously a real break/overnight — asking teaches nothing.
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn() with { AwayReturnAwayMinutes = 600 });
        Assert.False(d.ShouldPrompt);
    }

    [Fact]
    public void AwayReturn_TooShortAway_DoesNotPrompt()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn() with { AwayReturnAwayMinutes = 2 });
        Assert.False(d.ShouldPrompt);
    }

    [Fact]
    public void AwayReturn_ExemptFromConfidenceBand()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn() with { Confidence = 0.95 });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("distraction", d.LabelTarget);
    }

    [Fact]
    public void AwayReturn_StillRespectsCapsAndCooldown()
    {
        Assert.False(LabelPromptGovernor.Decide(AwayReturn() with
        {
            ActivePromptsLast24h = PortaExecutionConstants.LabelCapture.MaxActivePromptsPerDay,
        }).ShouldPrompt);
        Assert.False(LabelPromptGovernor.Decide(AwayReturn() with
        {
            LastActivePromptAtUtc = Now.AddMinutes(-30),
        }).ShouldPrompt);
    }

    // --- v2: focus question needs a genuine boundary ---

    [Fact]
    public void Focus_WithoutSustainedSession_DoesNotPrompt()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { SustainedAttentionSec = 60 });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("no_informative_moment", d.ReasonCode);
    }

    [Fact]
    public void Focus_MidTyping_DoesNotPrompt()
    {
        // 1s since last input = the user is actively working; never interrupt.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { SecondsSinceLastInput = 1 });
        Assert.False(d.ShouldPrompt);
    }

    [Fact]
    public void Focus_LongAfterLastInput_DoesNotPrompt()
    {
        // 10 minutes since last input: "just now" is already ambiguous — too late to ask.
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { SecondsSinceLastInput = 600 });
        Assert.False(d.ShouldPrompt);
    }

    // --- v2: priority when several moments coincide ---

    [Fact]
    public void AwayReturn_WinsOverOverloadCrossing()
    {
        // The away-return window is minutes; the overload latch lasts far longer — ask the
        // time-critical question first.
        LabelPromptDecision d = LabelPromptGovernor.Decide(AwayReturn() with { OverloadCrossingLatched = true });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("distraction", d.LabelTarget);
        Assert.Equal("away_return", d.PromptKind);
    }

    [Fact]
    public void OverloadCrossing_WinsOverStateDistraction()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with
        {
            OverloadCrossingLatched = true,
            DominantState = "off_task_distraction",
        });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("overload", d.LabelTarget);
    }

    // --- state-based distraction check (rare until Phase 2f emits these states) ---

    [Fact]
    public void DistractionState_PromptsForDistraction()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { DominantState = "off_task_distraction" });
        Assert.True(d.ShouldPrompt);
        Assert.Equal("distraction", d.LabelTarget);
        Assert.Equal("on_task_now", d.PromptKind);
    }

    // --- suppressions, caps, cooldown (unchanged from v1) ---

    [Fact]
    public void Paused_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { Paused = true }).ShouldPrompt);
    }

    [Fact]
    public void Meeting_DoesNotPrompt()
    {
        Assert.False(LabelPromptGovernor.Decide(Good() with { MeetingInProgress = true }).ShouldPrompt);
        // Meeting also suppresses the band-exempt questions.
        Assert.False(LabelPromptGovernor.Decide(AwayReturn() with { MeetingInProgress = true }).ShouldPrompt);
        Assert.False(LabelPromptGovernor.Decide(Good() with
        {
            MeetingInProgress = true,
            OverloadCrossingLatched = true,
        }).ShouldPrompt);
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
    public void ConfidenceTooHigh_DoesNotPromptForFocus()
    {
        LabelPromptDecision d = LabelPromptGovernor.Decide(Good() with { Confidence = 0.95 });
        Assert.False(d.ShouldPrompt);
        Assert.Equal("confidence_out_of_informative_band", d.ReasonCode);
    }

    [Fact]
    public void ConfidenceTooLow_DoesNotPromptForFocus()
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
