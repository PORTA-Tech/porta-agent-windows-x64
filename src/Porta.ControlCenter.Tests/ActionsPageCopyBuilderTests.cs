namespace Porta.ControlCenter.Tests;

public sealed class ActionsPageCopyBuilderTests
{
    [Fact]
    public void BuildLastSeenText_UsesRecommendationPrefix_ForSuggestions()
    {
        string text = ActionsPageCopyBuilder.BuildLastSeenText(
            isSuggestion: true,
            title: "Take a break",
            relativeTimeText: "5m");

        Assert.Equal("Latest recommendation: Take a break | 5m", text);
    }

    [Fact]
    public void BuildDrawerOutcomeText_UsesHumanChangeLabel_ForRecentActions()
    {
        string text = ActionsPageCopyBuilder.BuildDrawerOutcomeText(
            isSuggestion: false,
            actionSummary: "Notification reduction turned on");

        Assert.Equal("What Ari changed: Notification reduction turned on", text);
    }

    [Fact]
    public void BuildDrawerUndoText_HidesUndoForRecommendations()
    {
        string text = ActionsPageCopyBuilder.BuildDrawerUndoText(
            isSuggestion: true,
            undoDetails: "Open Settings and turn it off.");

        Assert.Equal("How to undo: Not needed. This was only a recommendation.", text);
    }

    [Fact]
    public void EmptyDrawerCopy_UsesAboveLanguage_ForStackedLayout()
    {
        Assert.Equal("When it happened: Pick a row above.", ActionsPageCopyBuilder.BuildEmptyDrawerWhen());
        Assert.Equal("Why it mattered: Pick a row above to see the reason in plain language.", ActionsPageCopyBuilder.BuildEmptyDrawerWhy());
        Assert.Equal("What Ari recommended or changed: Pick a row above.", ActionsPageCopyBuilder.BuildEmptyDrawerOutcome());
    }
}
