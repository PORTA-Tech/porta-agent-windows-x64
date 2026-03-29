namespace Porta.UserAgent.Interventions;

internal enum UserInterventionPromptResponseKind
{
    Accepted,
    Dismissed,
    Snoozed,
    SuppressedByPreference
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
    int DurationSeconds);

internal sealed record UserInterventionPromptResponse(
    string InterventionId,
    string TipKey,
    UserInterventionPromptResponseKind Kind);
