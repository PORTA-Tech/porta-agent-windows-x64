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
    public void ApplyState_UsesInlineCriticalMessageWhileKeepingSummaryVisible()
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
                Title: "Tracking limited",
                StatusText: "Ari is receiving partial capture only. Missing keyboard, mouse, focus / idle.",
                MessageSeverity: UserDesktopWidgetMessageSeverity.Warning,
                FooterText: "Confidence High | Tracking quality limited",
                PrimaryMetric: new UserDesktopWidgetMetric("Work today", "1h 12m"),
                SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "3m"),
                TertiaryMetric: new UserDesktopWidgetMetric("Overload", "42%"),
                CanPause: true,
                CanResume: false,
                CriticalState: new UserDesktopWidgetCriticalState(
                    Title: "Tracking limited",
                    Message: "Ari is receiving partial capture only. Missing keyboard, mouse, focus / idle.",
                    PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                    PrimaryLabel: "Open System",
                    SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                    SecondaryLabel: "Retry",
                    TertiaryAction: UserDesktopWidgetAction.OpenSettings,
                    TertiaryLabel: "Settings")));

            StackPanel metricsPanel = GetPrivateField<StackPanel>(window, "_metricsPanel");
            Border messageCard = GetPrivateField<Border>(window, "_messageCard");
            TextBlock messageTitle = GetPrivateField<TextBlock>(window, "_messageTitleText");
            TextBlock primaryMetricValue = GetPrivateField<TextBlock>(window, "_primaryMetricText");
            Button primaryActionButton = GetPrivateField<Button>(window, "_messagePrimaryButton");

            Assert.Equal(Visibility.Visible, metricsPanel.Visibility);
            Assert.Equal(Visibility.Visible, messageCard.Visibility);
            Assert.Equal("Tracking limited", messageTitle.Text);
            Assert.Equal("1h 12m", primaryMetricValue.Text);
            Assert.Equal("Open System", primaryActionButton.Content);
            Assert.Equal(Visibility.Visible, primaryActionButton.Visibility);
        });
    }

    [Fact]
    public void Constructor_UsesCompactWidgetWidth()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));

            Assert.True(window.Width < 250d);
            Assert.True(window.Height >= 150d);
        });
    }

    [Fact]
    public void Menu_UsesCurrentControlCenterLabels()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));
            Popup popup = GetPrivateField<Popup>(window, "_menuPopup");

            string popupText = ReadAllText(popup.Child);

            Assert.Contains("Control Center", popupText, StringComparison.Ordinal);
            Assert.Contains("Today", popupText, StringComparison.Ordinal);
            Assert.Contains("History", popupText, StringComparison.Ordinal);
            Assert.Contains("Actions", popupText, StringComparison.Ordinal);
            Assert.Contains("Tracking", popupText, StringComparison.Ordinal);
            Assert.Contains("Settings", popupText, StringComparison.Ordinal);
            Assert.Contains("System", popupText, StringComparison.Ordinal);
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
            Title: "Tracking limited",
            StatusText: message,
            MessageSeverity: UserDesktopWidgetMessageSeverity.Warning,
            FooterText: "Confidence High | Tracking quality limited",
            PrimaryMetric: new UserDesktopWidgetMetric("Work today", "1h 11m"),
            SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "2m"),
            TertiaryMetric: new UserDesktopWidgetMetric("Overload", "38%"),
            CanPause: true,
            CanResume: false,
            CriticalState: new UserDesktopWidgetCriticalState(
                Title: "Tracking limited",
                Message: message,
                PrimaryAction: UserDesktopWidgetAction.OpenHealth,
                PrimaryLabel: "Open System",
                SecondaryAction: UserDesktopWidgetAction.RetryCritical,
                SecondaryLabel: "Retry"));
    }

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field '{fieldName}'.");
        return (T)(field.GetValue(target) ?? throw new InvalidOperationException($"Field '{fieldName}' was null."));
    }

    private static string ReadAllText(object? node)
    {
        if (node == null)
        {
            return string.Empty;
        }

        return node switch
        {
            TextBlock textBlock => textBlock.Text,
            ContentControl contentControl => ReadAllText(contentControl.Content),
            Border border => ReadAllText(border.Child),
            Panel panel => string.Join(" ", panel.Children.OfType<object>().Select(ReadAllText)),
            Decorator decorator => ReadAllText(decorator.Child),
            _ => string.Empty
        };
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
