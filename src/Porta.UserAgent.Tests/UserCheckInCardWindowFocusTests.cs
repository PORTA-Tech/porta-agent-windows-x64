using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

// Step 1 — the typing bug. The card is shown through a window the presenter deliberately keeps
// alive across shows (UserCheckInCardPresenter.EnsureWindowOnCurrentDispatcher), and WPF's Loaded
// does not re-fire on Show() after Hide(). Focus was armed from inside BuildMorning/BuildEndOfDay
// via Loaded, so it took effect on the FIRST card of the process lifetime and never again.
//
// The regression these tests guard is therefore specifically the SECOND and THIRD ShowPrompt on a
// reused window — a test that only asserts the first show passes against the broken code.
//
// Foreground acquisition is suppressed here: it cannot be unit-tested (it depends on which process
// owns the foreground) and is confirmed manually. See UserCheckInCardWindow.TryAcquireForeground.
[Collection(CheckInCardCollection.Name)]
public sealed class UserCheckInCardWindowFocusTests
{
    [Fact]
    public void ShowPrompt_ReusedWindow_ArmsFocusOnSecondAndThirdShow_NotOnlyTheFirst()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            var armed = new List<Control?>();
            for (int show = 0; show < 3; show++)
            {
                window.ShowPrompt(MorningPrompt($"day {show}"));
                armed.Add(window.FocusTarget);
            }

            window.Close();

            Assert.All(armed, target => Assert.IsType<TextBox>(target));

            // Each show rebuilds the body, so a target that survived from an earlier show would be
            // a TextBox no longer in the visual tree — passing the type check while focusing
            // nothing. Distinct instances is the assertion that actually catches the bug.
            Assert.Equal(3, armed.Distinct().Count());
        });
    }

    // Chat 6 (2026-08-24) — REVERSED half. This asserted BOTH cards arm a box. The evening card's
    // box is gone on a task-less day, so what this now guards is the thing it was really for: a
    // stale target must never survive from one prompt to the next. The morning arms its box; the
    // evening that follows arms nothing, and crucially does not keep pointing at the morning's.
    [Fact]
    public void ShowPrompt_EndOfDayAfterMorning_ClearsTheMorningsBoxRatherThanKeepingIt()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            window.ShowPrompt(MorningPrompt("morning"));
            Control? morningTarget = window.FocusTarget;

            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.EndOfDay, "Wrap up", "Here is how today went."));
            Control? eodTarget = window.FocusTarget;

            window.Close();

            Assert.IsType<TextBox>(morningTarget);
            Assert.Null(eodTarget);
        });
    }

    [Fact]
    public void ShowPrompt_Midday_ArmsNothing_SoAStaleBoxIsNeverFocused()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            // Morning arms a box; midday has no text input and must clear it rather than inherit
            // the morning box, which is no longer in the visual tree.
            window.ShowPrompt(MorningPrompt("morning"));
            Assert.IsType<TextBox>(window.FocusTarget);

            window.ShowPrompt(new UserCheckInPrompt(
                UserCheckInKind.Midday, "Midday", "Halfway.", QuestionLabel: "Getting to it?"));

            Control? middayTarget = window.FocusTarget;
            window.Close();

            Assert.Null(middayTarget);
        });
    }

    [Fact]
    public void ShowConfirmation_ClearsTheArmedBox()
    {
        RunOnStaThread(() =>
        {
            using ForegroundSuppressionScope _ = new();
            var window = new UserCheckInCardWindow(_ => { });

            window.ShowPrompt(MorningPrompt("morning"));
            Assert.IsType<TextBox>(window.FocusTarget);

            window.ShowConfirmation(UserCheckInKind.Morning, "Locked in.");

            Control? afterConfirmation = window.FocusTarget;
            window.Close();

            Assert.Null(afterConfirmation);
        });
    }

    private static UserCheckInPrompt MorningPrompt(string title) =>
        new(UserCheckInKind.Morning, title, "What are you working on?");

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

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for STA check-in card test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for STA check-in card test thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
