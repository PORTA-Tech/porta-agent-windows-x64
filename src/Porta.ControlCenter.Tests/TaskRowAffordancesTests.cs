using Porta.Agent.LocalApi;
using Porta.ControlCenter;

namespace Porta.ControlCenter.Tests;

// Chat 5 (2026-08-24) — the line a client reads, and whether a row lets you do anything about it.
//
// Two dead ends: a SAVED line could never be edited, because the only route to the editor was the
// prompt shown while it was still empty; and an UNTAGGED task was told it could never reach a
// client report and offered nothing to do about it.
public sealed class TaskRowAffordancesTests
{
    private static LocalApiTaskRow Row(string resultLine, bool untagged = false)
        => new(
            TaskId: 1,
            Label: "Landing page copy",
            Status: "in_progress",
            ProjectName: untagged ? "" : "Northbridge",
            IsUntagged: untagged,
            Dates: "from 22 Aug",
            Hours: 2.2,
            ColourSlot: 2,
            ResultLine: resultLine,
            LastWorkedDay: "2026-08-24",
            ExpectedFinishDay: "",
            DeferredUntilDay: "");

    // The bug, stated as a test: a line that exists must be a way back into its own editor.
    [Fact]
    public void ASavedLineIsEditable()
        => Assert.True(TaskRowAffordances.For(Row("Sent for sign-off.")).EditableResultLine);

    [Fact]
    public void AnEmptyLineIsNotAnEditableLine_ItIsAPrompt()
    {
        TaskRowAffordances offers = TaskRowAffordances.For(Row(""));

        Assert.False(offers.EditableResultLine);
        Assert.True(offers.PromptForMissingLine);
    }

    // Whitespace is not a line. Saving spaces must not lock the editor away again.
    [Fact]
    public void AWhitespaceOnlyLineCountsAsMissing()
    {
        TaskRowAffordances offers = TaskRowAffordances.For(Row("   "));

        Assert.False(offers.EditableResultLine);
        Assert.True(offers.PromptForMissingLine);
    }

    // ── untagged ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUntaggedTaskIsOfferedAProject()
        => Assert.True(TaskRowAffordances.For(Row("", untagged: true)).OfferProject);

    // Prompting an untagged task for a line would be asking for something that goes nowhere. It
    // gets the project offer instead — that is the step that actually unblocks it.
    [Fact]
    public void AnUntaggedTaskIsNotPromptedForALineItCannotUse()
        => Assert.False(TaskRowAffordances.For(Row("", untagged: true)).PromptForMissingLine);

    // The worse dead end: the writing is already done and the client still cannot see it.
    [Fact]
    public void AnUntaggedTaskWithALineIsStillOfferedAProject()
    {
        TaskRowAffordances offers = TaskRowAffordances.For(Row("Sent for sign-off.", untagged: true));

        Assert.True(offers.OfferProject);
        Assert.True(offers.EditableResultLine);
    }

    [Fact]
    public void ATaggedTaskIsNotOfferedAProject()
        => Assert.False(TaskRowAffordances.For(Row("")).OfferProject);

    // ── the invariant behind both fixes ───────────────────────────────────────────────

    // Every row offers SOMETHING. A row that offers nothing is the dead end this chat removes, so
    // the four combinations are enumerated rather than sampled.
    [Fact]
    public void NoRowIsEverADeadEnd()
    {
        foreach (string line in new[] { "", "Sent for sign-off." })
        {
            foreach (bool untagged in new[] { false, true })
            {
                TaskRowAffordances offers = TaskRowAffordances.For(Row(line, untagged));

                Assert.True(
                    offers.EditableResultLine || offers.PromptForMissingLine || offers.OfferProject,
                    $"line=\"{line}\" untagged={untagged} offered nothing");
            }
        }
    }

    // The prompt and the saved line are alternatives, never both — two controls for the same thing
    // on one row would be the page arguing with itself.
    [Fact]
    public void ThePromptAndTheSavedLineAreNeverBothShown()
    {
        foreach (string line in new[] { "", "   ", "Sent for sign-off." })
        {
            foreach (bool untagged in new[] { false, true })
            {
                TaskRowAffordances offers = TaskRowAffordances.For(Row(line, untagged));
                Assert.False(offers.EditableResultLine && offers.PromptForMissingLine);
            }
        }
    }
}
