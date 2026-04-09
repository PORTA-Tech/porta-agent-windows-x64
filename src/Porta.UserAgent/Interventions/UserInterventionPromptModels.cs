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
    OverlayAborted
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
