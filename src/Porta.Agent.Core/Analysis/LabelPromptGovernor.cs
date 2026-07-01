namespace Porta.Agent.Core.Analysis;

// Phase 1b — Step 3a: decides whether an active one-tap ground-truth prompt is warranted right
// now, and which question teaches the most. PURE logic (no I/O): callers gather the inputs and
// act on the decision. Conservative and opportunistic by design — see
// PortaExecutionConstants.LabelCapture. Worst case is "no prompt"; it can never affect nudges,
// pressure, or actuation.

internal readonly record struct LabelPromptGovernorSettings(
    int MaxActivePromptsPerDay,
    int MinMinutesBetweenPrompts,
    double ConfidenceInformativeMin,
    double ConfidenceInformativeMax,
    double OverloadCheckPressureMin);

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
    DateTimeOffset? LastActivePromptAtUtc);

internal readonly record struct LabelPromptDecision(
    bool ShouldPrompt,
    string LabelTarget,   // focus_state | distraction | overload | ""
    string PromptKind,    // focused_now | on_task_now | overload_now | ""
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

        if (input.LastActivePromptAtUtc is DateTimeOffset last
            && (input.NowUtc - last).TotalMinutes < s.MinMinutesBetweenPrompts)
        {
            return No("min_interval_not_elapsed");
        }

        // Informative-moment gate: only ask when Ari is genuinely uncertain (mid confidence).
        double confidence = Clamp01(input.Confidence);
        if (confidence < s.ConfidenceInformativeMin || confidence > s.ConfidenceInformativeMax)
        {
            return No("confidence_out_of_informative_band");
        }

        (string target, string promptKind) = ChooseTarget(input, s);
        return new LabelPromptDecision(
            ShouldPrompt: true,
            LabelTarget: target,
            PromptKind: promptKind,
            ReasonCode: "prompt_due");
    }

    // Pick the question that validates whatever Ari currently believes — the highest-leverage label.
    private static (string Target, string PromptKind) ChooseTarget(
        LabelPromptGovernorInput input,
        LabelPromptGovernorSettings s)
    {
        // If Ari currently thinks the user is distracted, validate that call (buggiest signal).
        if (IsDistractionState(input.DominantState))
        {
            return ("distraction", "on_task_now");
        }

        // If focused but pressure is elevated, validate the overload/pressure read.
        if (Clamp01(input.PressureLevel) >= s.OverloadCheckPressureMin)
        {
            return ("overload", "overload_now");
        }

        // Otherwise validate the core focus signal.
        return ("focus_state", "focused_now");
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
        OverloadCheckPressureMin: PortaExecutionConstants.LabelCapture.OverloadCheckPressureMin);

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
