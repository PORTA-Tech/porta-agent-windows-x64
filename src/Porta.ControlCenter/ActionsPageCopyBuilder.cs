namespace Porta.ControlCenter;

internal static class ActionsPageCopyBuilder
{
    public static string BuildLastSeenText(bool isSuggestion, string title, string relativeTimeText)
    {
        string prefix = isSuggestion ? "Latest recommendation" : "Latest action";
        return $"{prefix}: {title} | {relativeTimeText}";
    }

    public static string BuildEmptyDrawerTitle() => "Choose a recommendation or recent action";

    public static string BuildEmptyDrawerWhen() => "When it happened: Pick a row above.";

    public static string BuildEmptyDrawerWhy() => "Why it mattered: Pick a row above to see the reason in plain language.";

    public static string BuildEmptyDrawerOutcome() => "What Ari recommended or changed: Pick a row above.";

    public static string BuildEmptyDrawerUndo() => "How to undo: Choose a recent action to see whether it can be reversed.";

    public static string BuildDrawerWhyText(string whyText) => $"Why it mattered: {whyText}";

    public static string BuildDrawerOutcomeText(bool isSuggestion, string actionSummary) =>
        isSuggestion
            ? $"What Ari recommended: {actionSummary}"
            : $"What Ari changed: {actionSummary}";

    public static string BuildDrawerUndoText(bool isSuggestion, string undoDetails) =>
        isSuggestion
            ? "How to undo: Not needed. This was only a recommendation."
            : $"How to undo: {undoDetails}";
}
