namespace Porta.Agent.Core.Analysis;

// Phase 1b — Step 3a (v2, moment selection): decides whether an active one-tap ground-truth
// prompt is warranted right now, and which question teaches the most. PURE logic (no I/O):
// callers gather the inputs and act on the decision. Conservative and opportunistic by design —
// see PortaExecutionConstants.LabelCapture. Worst case is "no prompt"; it can never affect
// nudges, pressure, or actuation.
//
// v2 rationale: v1 only gated on caps + a confidence band, so the prompt fired at the FIRST
// eligible poll tick of the day — i.e. right after login, labelling an away/boot window (a
// garbage training example). v2 makes each question wait for ITS moment:
//   - overload   → a pressure-threshold crossing was latched (event, not poll snapshot);
//   - distraction → the user just returned from a mid-session away segment;
//   - focus      → a natural lull after a sustained work session.
// A warm-up gate blocks all "just now" questions until real activity has accrued since resume.

internal readonly record struct LabelPromptGovernorSettings(
    int MaxActivePromptsPerDay,
    int MinMinutesBetweenPrompts,
    double ConfidenceInformativeMin,
    double ConfidenceInformativeMax,
    double OverloadCheckPressureMin,
    int WarmupMinutesAfterResume,
    int AwayMinMinutes,
    int AwayReturnMaxMinutes,
    int AwayReturnMinPriorActiveMinutes,
    int FocusCheckMinSustainedSec,
    int FocusLullMinSeconds,
    int FocusLullMaxSeconds);

internal readonly record struct LabelPromptGovernorInput(
    DateTimeOffset NowUtc,
    double Confidence,
    double PressureLevel,
    string CalibrationStage,   // cold | provisional | calibrated
    string DominantState,      // focus | *_distraction | overload | away | ...
    bool SuggestionsEnabled,
    bool Paused,
    bool MeetingInProgress,
    int ActivePromptsLast24h,
    DateTimeOffset? LastActivePromptAtUtc,
    int UnansweredPromptsLast24h = 0,
    // --- v2 moment inputs (defaults are the SAFE values: unknown → no prompt) ---
    double SecondsSinceActivityResume = -1,  // time since input activity resumed after an away gap; -1 = unknown
    double SustainedAttentionSec = 0,        // current sustained-attention streak from the derived state
    double SecondsSinceLastInput = -1,       // -1 = unknown
    bool OverloadCrossingLatched = false,    // pressure rose through the threshold recently (latched by caller)
    bool AwayReturnActive = false,           // caller: we are within the prompt window after an away return
    double AwayReturnAwayMinutes = 0,        // how long that away segment was
    double AwayReturnPriorActiveMinutes = 0);// how long the active span BEFORE the away was

internal readonly record struct LabelPromptDecision(
    bool ShouldPrompt,
    string LabelTarget,   // focus_state | distraction | overload | ""
    string PromptKind,    // focused_now | on_task_now | away_return | overload_now | ""
    string ReasonCode);

internal static class LabelPromptGovernor
{
    internal static LabelPromptDecision Decide(
        LabelPromptGovernorInput input,
        LabelPromptGovernorSettings? settings = null)
    {
        LabelPromptGovernorSettings s = settings ?? DefaultSettings();

        // Suppression: never interrupt when paused, disabled, or in a meeting.
        if (input.Paused)
        {
            return No("paused");
        }

        if (!input.SuggestionsEnabled)
        {
            return No("suggestions_disabled");
        }

        if (input.MeetingInProgress)
        {
            return No("meeting_in_progress");
        }

        // Never ask before calibration is meaningful — the answer would teach little.
        if (string.IsNullOrWhiteSpace(input.CalibrationStage)
            || string.Equals(input.CalibrationStage, "cold", StringComparison.OrdinalIgnoreCase))
        {
            return No("cold_start");
        }

        // Caps: rare by design.
        if (input.ActivePromptsLast24h >= Math.Max(0, s.MaxActivePromptsPerDay))
        {
            return No("daily_cap_reached");
        }

        // Cooldown with progressive back-off: each recently SHOWN-but-unanswered prompt stretches the
        // required gap, so a user who keeps ignoring prompts is asked less and less often (instead of
        // being re-asked every poll cycle). Answering resets the streak, restoring the base interval.
        double effectiveInterval = s.MinMinutesBetweenPrompts * BackoffMultiplier(input.UnansweredPromptsLast24h);
        if (input.LastActivePromptAtUtc is DateTimeOffset last
            && (input.NowUtc - last).TotalMinutes < effectiveInterval)
        {
            return No("min_interval_not_elapsed");
        }

        // 1) Away-return distraction check — exempt from warm-up (it asks about the PAST away
        //    segment, and by definition fires right after the return) and from the confidence band
        //    (the moment itself is what makes the label informative). Guards: the away must have
        //    interrupted a real session (kills login/overnight prompts) and be short enough that
        //    the answer isn't obvious.
        if (input.AwayReturnActive
            && input.AwayReturnAwayMinutes >= s.AwayMinMinutes
            && input.AwayReturnAwayMinutes <= s.AwayReturnMaxMinutes
            && input.AwayReturnPriorActiveMinutes >= s.AwayReturnMinPriorActiveMinutes)
        {
            return new LabelPromptDecision(true, "distraction", "away_return", "prompt_due");
        }

        // Warm-up gate: every "just now" question below needs real activity since the last resume.
        // Unknown resume time (-1) is treated as NOT warmed up — safe default.
        if (input.SecondsSinceActivityResume < s.WarmupMinutesAfterResume * 60d)
        {
            return No("warmup_not_elapsed");
        }

        // 2) Overload check — fires on a latched pressure-threshold crossing. Deliberately EXEMPT
        //    from the confidence band: high-pressure moments often come with out-of-band confidence,
        //    which is exactly when validating the pressure read matters most.
        if (input.OverloadCrossingLatched)
        {
            return new LabelPromptDecision(true, "overload", "overload_now", "prompt_due");
        }

        // Informative-moment band for the remaining questions: only ask when Ari is genuinely
        // uncertain (mid confidence).
        double confidence = Clamp01(input.Confidence);
        bool inBand = confidence >= s.ConfidenceInformativeMin && confidence <= s.ConfidenceInformativeMax;

        // 3) State-based distraction check — validates a live distraction call. Structurally rare
        //    until the off-task engine (Phase 2f) emits these states; kept so it lights up then.
        if (inBand && IsDistractionState(input.DominantState))
        {
            return new LabelPromptDecision(true, "distraction", "on_task_now", "prompt_due");
        }

        // 4) Focus check — only at a genuine boundary: a sustained session AND a natural input
        //    lull (not mid-typing; not after the user has already wandered off).
        if (inBand
            && input.SustainedAttentionSec >= s.FocusCheckMinSustainedSec
            && input.SecondsSinceLastInput >= s.FocusLullMinSeconds
            && input.SecondsSinceLastInput <= s.FocusLullMaxSeconds)
        {
            return new LabelPromptDecision(true, "focus_state", "focused_now", "prompt_due");
        }

        return No(inBand ? "no_informative_moment" : "confidence_out_of_informative_band");
    }

    private static bool IsDistractionState(string dominantState)
    {
        if (string.IsNullOrWhiteSpace(dominantState))
        {
            return false;
        }

        string s = dominantState.Trim().ToLowerInvariant();
        return s.Contains("distraction", StringComparison.Ordinal)
            || s.Contains("off_task", StringComparison.Ordinal)
            || s is "context_excursion" or "comm_monitoring_loop" or "social_media_loop";
    }

    private static LabelPromptGovernorSettings DefaultSettings() => new(
        MaxActivePromptsPerDay: PortaExecutionConstants.LabelCapture.MaxActivePromptsPerDay,
        MinMinutesBetweenPrompts: PortaExecutionConstants.LabelCapture.MinMinutesBetweenPrompts,
        ConfidenceInformativeMin: PortaExecutionConstants.LabelCapture.ConfidenceInformativeMin,
        ConfidenceInformativeMax: PortaExecutionConstants.LabelCapture.ConfidenceInformativeMax,
        OverloadCheckPressureMin: PortaExecutionConstants.LabelCapture.OverloadCheckPressureMin,
        WarmupMinutesAfterResume: PortaExecutionConstants.LabelCapture.WarmupMinutesAfterResume,
        AwayMinMinutes: PortaExecutionConstants.LabelCapture.AwayMinMinutes,
        AwayReturnMaxMinutes: PortaExecutionConstants.LabelCapture.AwayReturnMaxMinutes,
        AwayReturnMinPriorActiveMinutes: PortaExecutionConstants.LabelCapture.AwayReturnMinPriorActiveMinutes,
        FocusCheckMinSustainedSec: PortaExecutionConstants.LabelCapture.FocusCheckMinSustainedSec,
        FocusLullMinSeconds: PortaExecutionConstants.LabelCapture.FocusLullMinSeconds,
        FocusLullMaxSeconds: PortaExecutionConstants.LabelCapture.FocusLullMaxSeconds);

    // Progressive back-off factor keyed off how many prompts were shown-but-unanswered in the last 24h.
    // Kept as a small step function (not per-prompt exponential) so behaviour is easy to reason about
    // and the daily cap still bounds the worst case. 0-1 unanswered = base gap; 2-3 = 2x; 4+ = 4x.
    private static double BackoffMultiplier(int unansweredLast24h)
    {
        if (unansweredLast24h >= 4)
        {
            return 4.0;
        }

        if (unansweredLast24h >= 2)
        {
            return 2.0;
        }

        return 1.0;
    }

    private static LabelPromptDecision No(string reasonCode) =>
        new(ShouldPrompt: false, LabelTarget: string.Empty, PromptKind: string.Empty, ReasonCode: reasonCode);

    private static double Clamp01(double value)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }
}
