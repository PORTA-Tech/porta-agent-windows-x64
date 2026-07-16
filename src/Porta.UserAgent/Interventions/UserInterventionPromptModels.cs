namespace Porta.UserAgent.Interventions;

internal enum UserInterventionPromptResponseKind
{
    OverlayStarted,
    Accepted,
    Skipped,
    Dismissed,
    Snoozed,
    SuppressedByPreference,
    OverlayRecovered,
    OverlayCompleted,
    OverlayAborted,
    // C1: user chose "Skip today" on the break overlay — a skip that also mutes the break class
    // for the rest of the day (via the B2 mute store, handled in the executor). Acked to the
    // core as a normal skip; the daily mute is a local UserAgent addition.
    SkippedForToday
}

internal sealed record UserInterventionPrompt(
    string InterventionId,
    string TipKey,
    string Title,
    string Message,
    string AcceptLabel,
    string DismissLabel,
    string SnoozeLabel,
    string SuppressLabel,
    int DurationSeconds,
    string InteractionKind = "choice_prompt",
    string DeliverySurface = "widget",
    string? BreakKind = null,
    int? CountdownSeconds = null);

internal sealed record UserInterventionPromptResponse(
    string InterventionId,
    string TipKey,
    UserInterventionPromptResponseKind Kind,
    string? ErrorCode = null);
