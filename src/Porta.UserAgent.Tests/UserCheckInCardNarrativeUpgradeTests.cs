using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Part 5 (async narrative) — the card is shown instantly with the honest template and the LLM
// line upgrades it in place when it lands. These tests pin the REFUSAL rules, which are the
// part that protects the user: a late line must never rewrite copy underneath someone who has
// already started answering.
//
// Foreground acquisition is suppressed throughout — see UserCheckInCardWindowFocusTests.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInCardNarrativeUpgradeTests
{
    private const string Template = "Name today's focus and I'll help you protect it.";
    private const string LlmLine = "Yesterday you didn't finish the pricing page. Same again today?";

    [Fact]
    public void TryUpgradeMessage_UntouchedCard_AppliesTheLine()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine);
            window.Close();

            Assert.True(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_AfterUserTypes_IsRefused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());

            // The user starts answering before the slow line lands.
            TextBox box = Assert.IsType<TextBox>(window.FocusTarget);
            box.Text = "ship the installer fix";

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine);
            window.Close();

            Assert.False(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_PrefilledBoxTheUserHasNotTouched_StillUpgrades()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            // Carried-over plan pre-fills the box. Seeding it is NOT the user typing, so the
            // upgrade must still apply — otherwise the carry-forward path could never show a line.
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Morning,
                "Still the goal for today?",
                "Last night you planned something. Confirm it or change it below.",
                PrefillGoalText: "finish the pricing page"));

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine);
            window.Close();

            Assert.True(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_WrongKind_IsRefused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());

            // A morning card is showing; an eod line arriving late must not land on it.
            bool applied = window.TryUpgradeMessage(UserCheckInKind.EndOfDay, LlmLine);
            window.Close();

            Assert.False(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_AfterConfirmation_IsRefused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());
            window.ShowConfirmation(UserCheckInKind.Morning, "Locked in.");

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine);
            window.Close();

            Assert.False(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_HiddenCard_IsRefused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());
            window.Hide();

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine);
            window.Close();

            Assert.False(applied);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryUpgradeMessage_BlankLine_IsRefused(string line)
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());

            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, line);
            window.Close();

            Assert.False(applied);
        });
    }

    [Fact]
    public void TryUpgradeMessage_SameTextAsTemplate_IsRefused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });
            window.ShowPrompt(MorningPrompt());

            // No visible change means no reason to animate.
            bool applied = window.TryUpgradeMessage(UserCheckInKind.Morning, Template);
            window.Close();

            Assert.False(applied);
        });
    }

    [Fact]
    public void ShowPrompt_ClearsEngagementFromThePreviousCard()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            window.ShowPrompt(MorningPrompt());
            Assert.IsType<TextBox>(window.FocusTarget).Text = "answered the morning card";
            Assert.False(window.TryUpgradeMessage(UserCheckInKind.Morning, LlmLine));

            // A later card on the same reused window must start upgradeable again — the same
            // window-reuse trap that broke focus in step 1.
            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.EndOfDay, "Wrapping up", "Today's goal was something.",
                QuestionLabel: "Plan for tomorrow?"));

            bool applied = window.TryUpgradeMessage(UserCheckInKind.EndOfDay, LlmLine);
            window.Close();

            Assert.True(applied);
        });
    }

    private static UserCheckInPrompt MorningPrompt() =>
        new(UserCheckInKind.Morning, "What's the one thing today?", Template);

    private sealed class ForegroundSuppressionScope : IDisposable
    {
        public ForegroundSuppressionScope() => UserCheckInCardWindow.SuppressForegroundAcquisition = true;

        public void Dispose() => UserCheckInCardWindow.SuppressForegroundAcquisition = false;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // Shut the dispatcher down with the thread. A Dispatcher outliving its
                // STA thread keeps WPF resources resident for the whole test process,
                // which inflates the process-global counts GuiHandleLeakStressTests reads
                // and made it report a widget leak that does not exist.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for STA narrative-upgrade test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for STA narrative-upgrade test thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
