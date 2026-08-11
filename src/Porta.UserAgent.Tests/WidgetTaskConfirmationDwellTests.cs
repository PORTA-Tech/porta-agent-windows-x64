using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Complaint 6 (2026-08-11) - "the done confirmation on the widget dwells too long. Shorter, but not
// instant. Every task."
//
// The confirmation was inheriting the widget's shared 30-second attention hold. Pinned here as a
// range rather than an exact number, so the value can be tuned without a test edit but cannot drift
// back to half a minute or collapse to a flash.
public sealed class WidgetTaskConfirmationDwellTests
{
    [Fact]
    public void TaskConfirmation_IsShorterThanTheSharedAttentionHold()
    {
        Assert.True(
            UserDesktopWidgetPresenter.TaskConfirmationDwell < TimeSpan.FromSeconds(30),
            "the confirmation must not inherit the 30s attention hold again");
    }

    [Fact]
    public void TaskConfirmation_IsNotInstant()
    {
        // The measured time is the payoff for finishing a task. A confirmation that vanishes is one
        // the user stops looking for, which is the opposite of the fix.
        Assert.True(
            UserDesktopWidgetPresenter.TaskConfirmationDwell >= TimeSpan.FromSeconds(3),
            "shorter, but not instant");
    }
}
