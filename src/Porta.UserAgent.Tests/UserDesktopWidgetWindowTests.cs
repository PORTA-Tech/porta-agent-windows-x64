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
    public void ApplyState_RendersCompactSummaryMetricsAndWrappedMessage()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));

            window.ApplyState(new UserDesktopWidgetState(
                Mode: UserDesktopWidgetMode.Monitoring,
                Title: "Today",
                StatusText: "Ari is using the inline message rail only, so long status text needs to wrap inside the compact widget without hiding any summary facts or controls.",
                MessageSeverity: UserDesktopWidgetMessageSeverity.Info,
                FooterText: "Confidence High | Tracking quality strong",
                PrimaryMetric: new UserDesktopWidgetMetric("Work today", "5h 14m"),
                SecondaryMetric: new UserDesktopWidgetMetric("Last focus session", "28m"),
                TertiaryMetric: new UserDesktopWidgetMetric("Overload", "31%"),
                CanPause: true,
                CanResume: false,
                CriticalState: null));

            TextBlock primaryMetricLabel = GetPrivateField<TextBlock>(window, "_primaryMetricLabelText");
            TextBlock secondaryMetricLabel = GetPrivateField<TextBlock>(window, "_secondaryMetricLabelText");
            TextBlock tertiaryMetricLabel = GetPrivateField<TextBlock>(window, "_tertiaryMetricLabelText");
            TextBlock messageBody = GetPrivateField<TextBlock>(window, "_messageBodyText");
            Border rootBorder = GetPrivateField<Border>(window, "_rootBorder");

            Assert.Equal("Work today", primaryMetricLabel.Text);
            Assert.Equal("Last focus session", secondaryMetricLabel.Text);
            Assert.Equal("Overload", tertiaryMetricLabel.Text);
            Assert.Equal(TextWrapping.Wrap, messageBody.TextWrapping);
            Assert.True(messageBody.MaxWidth < window.Width);
            Assert.True(rootBorder.DesiredSize.Height > 150d);
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
    public void Menu_UsesCurrentControlCenterRoutes()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            List<UserDesktopWidgetAction> actions = [];
            var window = new UserDesktopWidgetWindow(
                actions.Add,
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));
            Popup popup = GetPrivateField<Popup>(window, "_menuPopup");

            ClickMenuButton(popup.Child, "Today");
            ClickMenuButton(popup.Child, "History");
            ClickMenuButton(popup.Child, "Actions");
            ClickMenuButton(popup.Child, "Tracking");
            ClickMenuButton(popup.Child, "Settings");
            ClickMenuButton(popup.Child, "System");

            Assert.Equal(
                [
                    UserDesktopWidgetAction.OpenNow,
                    UserDesktopWidgetAction.OpenHistory,
                    UserDesktopWidgetAction.OpenActions,
                    UserDesktopWidgetAction.OpenTracking,
                    UserDesktopWidgetAction.OpenSettings,
                    UserDesktopWidgetAction.OpenHealth
                ],
                actions);
        });
    }

    [Fact]
    public void Menu_DisablesPopupFadeAndDefaultButtonChrome()
    {
        RunOnStaThread(() =>
        {
            using TempDirectoryScope tempDirectory = new();
            var window = new UserDesktopWidgetWindow(
                _ => { },
                _ => { },
                new UserDesktopWidgetPlacementStore(Path.Combine(tempDirectory.Path, "widget-placement.json")));

            Popup popup = GetPrivateField<Popup>(window, "_menuPopup");
            Button menuButton = GetPrivateField<Button>(window, "_menuButton");
            Button historyButton = FindMenuButton(popup.Child, "History");

            Assert.Equal(PopupAnimation.None, popup.PopupAnimation);
            Assert.False(menuButton.Focusable);
            Assert.Null(menuButton.FocusVisualStyle);
            Assert.NotNull(menuButton.Template);
            Assert.False(historyButton.Focusable);
            Assert.Null(historyButton.FocusVisualStyle);
            Assert.NotNull(historyButton.Template);
            Assert.Contains(historyButton.Template.Triggers.OfType<Trigger>(), trigger => Equals(trigger.Property, UIElement.IsMouseOverProperty));
            Assert.Contains(historyButton.Template.Triggers.OfType<Trigger>(), trigger => Equals(trigger.Property, ButtonBase.IsPressedProperty));
            Assert.DoesNotContain(
                historyButton.Template.Triggers
                    .OfType<Trigger>()
                    .SelectMany(static trigger => trigger.Setters.OfType<Setter>()),
                setter => Equals(setter.Property, Control.ForegroundProperty));
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

    private static Button FindMenuButton(object? node, string text)
    {
        Button? match = EnumerateButtons(node)
            .FirstOrDefault(button => string.Equals(ReadAllText(button.Content), text, StringComparison.Ordinal));
        return match ?? throw new InvalidOperationException($"Could not find menu button '{text}'.");
    }

    private static void ClickMenuButton(object? node, string text)
    {
        Button button = FindMenuButton(node, text);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
    }

    private static IEnumerable<Button> EnumerateButtons(object? node)
    {
        if (node == null)
        {
            yield break;
        }

        switch (node)
        {
            case Button button:
                yield return button;
                foreach (Button nested in EnumerateButtons(button.Content))
                {
                    yield return nested;
                }

                yield break;

            case ContentControl contentControl:
                foreach (Button nested in EnumerateButtons(contentControl.Content))
                {
                    yield return nested;
                }

                yield break;

            case Border border:
                foreach (Button nested in EnumerateButtons(border.Child))
                {
                    yield return nested;
                }

                yield break;

            case Panel panel:
                foreach (object child in panel.Children.OfType<object>())
                {
                    foreach (Button nested in EnumerateButtons(child))
                    {
                        yield return nested;
                    }
                }

                yield break;

            case Decorator decorator:
                foreach (Button nested in EnumerateButtons(decorator.Child))
                {
                    yield return nested;
                }

                yield break;
        }
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
