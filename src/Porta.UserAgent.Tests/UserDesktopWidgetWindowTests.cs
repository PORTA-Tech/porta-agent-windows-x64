using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserDesktopWidgetWindowTests
{
    [Fact]
    public void ApplyState_ShowsCriticalSpotlightWithoutDuplicatingSummarySurface()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));

            window.ApplyState(new UserDesktopWidgetState(
                Mode: UserDesktopWidgetMode.Limited,
                Title: "Attention",
                StatusText: "Ari is receiving partial capture only. Missing keyboard, mouse, focus / idle.",
                PrimaryMetricText: "Focus 3m",
                SecondaryMetricText: "Overload 42%",
                FooterText: "Session 17m | Typing active | Mouse idle",
                NotificationsSuppressed: false,
                BrightnessAdjusted: false,
                CanPause: true,
                CanResume: false,
                TertiaryMetricText: "Today 17m",
                CriticalState: new UserDesktopWidgetCriticalState(
                    Title: "Attention",
                    Message: "Ari is receiving partial capture only. Missing keyboard, mouse, focus / idle.",
                    PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                    PrimaryLabel: "Open Health",
                    SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                    SecondaryLabel: "Retry",
                    TertiaryAction: UserDesktopWidgetAction.OpenSettings,
                    TertiaryLabel: "Settings")));

            TextBlock statusText = GetPrivateField<TextBlock>(window, "_statusText");
            UniformGrid metricsPanel = GetPrivateField<UniformGrid>(window, "_metricsPanel");
            TextBlock footerText = GetPrivateField<TextBlock>(window, "_footerText");
            Border criticalCard = GetPrivateField<Border>(window, "_criticalCard");
            Grid summaryRow = GetPrivateField<Grid>(window, "_summaryRow");

            Assert.Equal(Visibility.Collapsed, statusText.Visibility);
            Assert.Equal(Visibility.Collapsed, metricsPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, footerText.Visibility);
            Assert.Equal(Visibility.Visible, criticalCard.Visibility);
            Assert.Equal(Visibility.Visible, summaryRow.Visibility);
        });
    }

    [Fact]
    public void ApplyState_AdjustsCriticalSpotlightDurationToMessageLength()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));
            DispatcherTimer timer = GetPrivateField<DispatcherTimer>(window, "_criticalSpotlightTimer");

            window.ApplyState(BuildCriticalState("Short warning."));
            TimeSpan shortDuration = timer.Interval;

            window.ApplyState(BuildCriticalState(new string('x', 260)));
            TimeSpan longDuration = timer.Interval;

            Assert.True(shortDuration >= TimeSpan.FromSeconds(5));
            Assert.True(longDuration <= TimeSpan.FromSeconds(12));
            Assert.True(longDuration > shortDuration);
        });
    }

    [Fact]
    public void Close_WhenCloseIsNotAllowed_DoesNotMakeWindowUnusable()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));

            window.Show();
            Exception? closeFailure = Record.Exception(window.Close);

            Assert.Null(closeFailure);
            Assert.True(window.CanAcceptUiActions);

            window.AllowCloseForShutdown();
            window.Close();
            Assert.True(window.IsClosingOrClosed);
        });
    }

    private static UserDesktopWidgetState BuildCriticalState(string message)
    {
        return new UserDesktopWidgetState(
            Mode: UserDesktopWidgetMode.Limited,
            Title: "Attention",
            StatusText: message,
            PrimaryMetricText: "Focus 2m",
            SecondaryMetricText: "Overload 38%",
            FooterText: "Session 11m | Typing active | Mouse active",
            NotificationsSuppressed: false,
            BrightnessAdjusted: false,
            CanPause: true,
            CanResume: false,
            TertiaryMetricText: "Today 11m",
            CriticalState: new UserDesktopWidgetCriticalState(
                Title: "Attention",
                Message: message,
                PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                PrimaryLabel: "Open Health",
                SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                SecondaryLabel: "Retry"));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field '{fieldName}'.");
        return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
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
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Timed out waiting for STA widget test to finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Timed out waiting for STA widget test thread to join.");

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "porta-widget-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
