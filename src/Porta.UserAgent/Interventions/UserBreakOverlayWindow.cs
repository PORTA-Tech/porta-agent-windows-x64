namespace Porta.UserAgent.Interventions;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

internal enum UserBreakOverlayAction
{
    Accept,
    Dismiss,
    Snooze,
    Suppress,
    EmergencyExit,
    // C1: "Skip today" — dismiss AND mute the break class until the next 5:00 local (B2 store),
    // so no further break prompts interrupt today. Distinct from Dismiss (a one-time skip).
    SkipToday
}

internal sealed class UserBreakOverlayWindow : Window
{
    private readonly Action<UserBreakOverlayAction> _actionRequested;
    private readonly Border _cardBorder;
    private readonly TextBlock _eyebrowText;
    private readonly TextBlock _titleText;
    private readonly TextBlock _messageText;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _footerText;
    private readonly Button _acceptButton;
    private readonly Button _dismissButton;
    private readonly Button _snoozeButton;
    private readonly Button _suppressButton;
    private readonly Button _emergencyExitButton;
    private bool _allowClose;
    private bool _enforceForeground;
    private bool _countdownVisible;

    public UserBreakOverlayWindow(Action<UserBreakOverlayAction> actionRequested)
    {
        _actionRequested = actionRequested;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(232, 8, 11, 16));
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Focusable = true;
        FocusVisualStyle = null;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Grid root = new();

        _cardBorder = new Border
        {
            Width = 560,
            MaxWidth = 560,
            Padding = new Thickness(28),
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 63, 77)),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(_cardBorder);

        StackPanel stack = new()
        {
            Orientation = Orientation.Vertical
        };
        _cardBorder.Child = stack;

        _eyebrowText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(144, 168, 191)),
            Text = "Ari break",
            Margin = new Thickness(0, 0, 0, 10)
        };
        stack.Children.Add(_eyebrowText);

        _titleText = new TextBlock
        {
            FontSize = 30,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 243, 248)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_titleText);

        _messageText = new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(202, 214, 226)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_messageText);

        _countdownText = new TextBlock
        {
            Margin = new Thickness(0, 20, 0, 0),
            FontSize = 42,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(248, 230, 182)),
            TextAlignment = TextAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(_countdownText);

        _footerText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(137, 153, 170)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(_footerText);

        WrapPanel actions = new()
        {
            Margin = new Thickness(0, 24, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(actions);

        _acceptButton = CreatePrimaryButton();
        _acceptButton.Click += (_, _) => _actionRequested(UserBreakOverlayAction.Accept);
        actions.Children.Add(_acceptButton);

        // C1: the secondary action is now "Skip today" (dismiss + mute the break class for the
        // day), replacing the one-time "Skip". The field name is retained to limit churn.
        _dismissButton = CreateSecondaryButton();
        _dismissButton.Click += (_, _) => _actionRequested(UserBreakOverlayAction.SkipToday);
        actions.Children.Add(_dismissButton);

        _snoozeButton = CreateSecondaryButton();
        _snoozeButton.Click += (_, _) => _actionRequested(UserBreakOverlayAction.Snooze);
        actions.Children.Add(_snoozeButton);

        _suppressButton = CreateLinkButton();
        _suppressButton.Click += (_, _) => _actionRequested(UserBreakOverlayAction.Suppress);
        actions.Children.Add(_suppressButton);

        _emergencyExitButton = CreateSecondaryButton();
        _emergencyExitButton.Click += (_, _) => _actionRequested(UserBreakOverlayAction.EmergencyExit);
        actions.Children.Add(_emergencyExitButton);

        Content = root;
        Loaded += (_, _) =>
        {
            ApplyVirtualScreenBounds();
            BringOverlayToFront();
        };
        Deactivated += (_, _) =>
        {
            if (_enforceForeground)
            {
                Dispatcher.BeginInvoke(BringOverlayToFront);
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowPrompt(UserInterventionPrompt prompt, string breakLabel)
    {
        _countdownVisible = false;
        _enforceForeground = true;
        _eyebrowText.Text = "Ari break";
        _titleText.Text = string.IsNullOrWhiteSpace(prompt.Title) ? breakLabel : prompt.Title.Trim();
        _messageText.Text = string.IsNullOrWhiteSpace(prompt.Message)
            ? $"You have been in a focus episode for a while. A {breakLabel.ToLowerInvariant()} now will help you sustain the session."
            : prompt.Message.Trim();
        _countdownText.Visibility = Visibility.Collapsed;
        _countdownText.Text = string.Empty;
        // C1 (LOCKED design): exactly three actions — Take break / Snooze 15m / Skip today.
        // Fixed labels: the overlay owns this locked presentation regardless of prompt copy.
        _footerText.Text = "Take break starts the countdown and holds this screen. Snooze reminds you in 15 minutes. Skip today silences breaks until tomorrow.";
        ConfigureButton(_acceptButton, "Take break", visible: true);
        ConfigureButton(_dismissButton, "Skip today", visible: true);
        ConfigureButton(_snoozeButton, "Snooze 15m", visible: true);
        ConfigureButton(_suppressButton, string.Empty, visible: false);
        ConfigureButton(_emergencyExitButton, string.Empty, visible: false);
        _cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(65, 88, 112));
        _cardBorder.Background = new SolidColorBrush(Color.FromRgb(18, 26, 35));
        ApplyVirtualScreenBounds();
        ShowIfNeeded();
    }

    public void ShowCountdown(UserBreakOverlayCountdownState state, TimeSpan remaining)
    {
        _countdownVisible = true;
        _enforceForeground = true;
        _eyebrowText.Text = "Ari break in progress";
        _titleText.Text = state.Title;
        _messageText.Text = string.IsNullOrWhiteSpace(state.Message)
            ? "Ari is holding this screen. Step away — you accepted the break."
            : $"{state.Message.Trim()} Ari is holding this screen so you can step away.";
        _countdownText.Visibility = Visibility.Visible;
        _countdownText.Text = FormatRemaining(remaining);
        _footerText.Text = "Press Esc for an emergency exit if you must return immediately. The break will be recorded as interrupted.";
        ConfigureButton(_acceptButton, string.Empty, visible: false);
        ConfigureButton(_dismissButton, string.Empty, visible: false);
        ConfigureButton(_snoozeButton, string.Empty, visible: false);
        ConfigureButton(_suppressButton, string.Empty, visible: false);
        ConfigureButton(_emergencyExitButton, "Emergency exit", visible: true);
        _cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(141, 105, 44));
        _cardBorder.Background = new SolidColorBrush(Color.FromRgb(35, 28, 18));
        ApplyVirtualScreenBounds();
        ShowIfNeeded();
    }

    public void UpdateCountdown(TimeSpan remaining)
    {
        if (!_countdownVisible)
        {
            return;
        }

        _countdownText.Text = FormatRemaining(remaining);
    }

    public void RestoreAfterSessionUnlock()
    {
        if (!_enforceForeground)
        {
            return;
        }

        ApplyVirtualScreenBounds();
        ShowIfNeeded();
        BringOverlayToFront();
    }

    public void AllowCloseForShutdown()
    {
        _allowClose = true;
        _enforceForeground = false;
    }

    public void HideOverlay()
    {
        _enforceForeground = false;
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void ShowIfNeeded()
    {
        if (!IsLoaded)
        {
            Show();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    private void BringOverlayToFront()
    {
        Topmost = false;
        Topmost = true;
        Activate();
        Focus();
    }

    private void ApplyVirtualScreenBounds()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _actionRequested(_countdownVisible
            ? UserBreakOverlayAction.EmergencyExit
            : UserBreakOverlayAction.Dismiss);
        e.Handled = true;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        TimeSpan clamped = remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
        return clamped.TotalHours >= 1d
            ? clamped.ToString(@"hh\:mm\:ss")
            : clamped.ToString(@"mm\:ss");
    }

    private static Button CreatePrimaryButton()
    {
        return new Button
        {
            Padding = new Thickness(18, 11, 18, 11),
            Margin = new Thickness(0, 0, 12, 12),
            MinWidth = 130,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(68, 102, 138)),
            Foreground = new SolidColorBrush(Color.FromRgb(245, 248, 251)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(94, 132, 171)),
            BorderThickness = new Thickness(1),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold
        };
    }

    private static Button CreateSecondaryButton()
    {
        return new Button
        {
            Padding = new Thickness(18, 11, 18, 11),
            Margin = new Thickness(0, 0, 12, 12),
            MinWidth = 120,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(31, 39, 48)),
            Foreground = new SolidColorBrush(Color.FromRgb(236, 241, 245)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(74, 89, 105)),
            BorderThickness = new Thickness(1),
            FontSize = 15
        };
    }

    private static Button CreateLinkButton()
    {
        return new Button
        {
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 12, 12),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 188, 207)),
            BorderThickness = new Thickness(0),
            FontSize = 14
        };
    }

    private static void ConfigureButton(Button button, string content, bool visible)
    {
        button.Content = content;
        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
