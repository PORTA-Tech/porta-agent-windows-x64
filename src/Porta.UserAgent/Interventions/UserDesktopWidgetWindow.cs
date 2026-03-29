namespace Porta.UserAgent.Interventions;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using FormsCursor = System.Windows.Forms.Cursor;

internal sealed class UserDesktopWidgetWindow : Window
{
    private const double ExpandedWidth = 392d;
    private const double CollapsedWidth = 68d;
    private const double BaseHeight = 114d;
    private const double AttentionHeight = 70d;
    private const double PromptHeight = 116d;
    private const double CriticalHeight = 108d;
    private const double TopMargin = 18d;
    private const double RightMargin = 18d;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan AttentionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReexpandPollInterval = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(180);

    private readonly Action<UserDesktopWidgetAction> _actionRequested;
    private readonly Action<UserInterventionPromptResponse> _promptResponseRequested;
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly DispatcherTimer _attentionTimer;
    private readonly DispatcherTimer _promptTimer;
    private readonly DispatcherTimer _reexpandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly Border _rootBorder;
    private readonly Border _accentBar;
    private readonly StackPanel _expandedStack;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private readonly Border _primaryMetricChip;
    private readonly TextBlock _primaryMetricText;
    private readonly Border _secondaryMetricChip;
    private readonly TextBlock _secondaryMetricText;
    private readonly Border _tertiaryMetricChip;
    private readonly TextBlock _tertiaryMetricText;
    private readonly Border _quaternaryMetricChip;
    private readonly TextBlock _quaternaryMetricText;
    private readonly TextBlock _footerText;
    private readonly Border _notificationsChip;
    private readonly Border _brightnessChip;
    private readonly Border _attentionCard;
    private readonly TextBlock _attentionTitleText;
    private readonly TextBlock _attentionMessageText;
    private readonly Border _criticalCard;
    private readonly TextBlock _criticalTitleText;
    private readonly TextBlock _criticalMessageText;
    private readonly Button _criticalPrimaryButton;
    private readonly Button _criticalSecondaryButton;
    private readonly Button _criticalTertiaryButton;
    private readonly Border _promptCard;
    private readonly TextBlock _promptTitleText;
    private readonly TextBlock _promptMessageText;
    private readonly Button _promptAcceptButton;
    private readonly Button _promptDismissButton;
    private readonly Button _promptSnoozeButton;
    private readonly Button _promptSuppressButton;
    private readonly Button _menuButton;
    private readonly Popup _menuPopup;
    private readonly Button _pauseFifteenButton;
    private readonly Button _pauseHourButton;
    private readonly Button _resumeButton;

    private HwndSource? _hwndSource;
    private bool _collapsed;
    private bool _menuOpen;
    private bool _dragInProgress;
    private bool _placementInitialized;
    private double _expandedAnchorLeft;
    private double _expandedAnchorTop;
    private UserDesktopWidgetState? _currentState;
    private UserInterventionPrompt? _activePrompt;

    public UserDesktopWidgetWindow(
        Action<UserDesktopWidgetAction> actionRequested,
        Action<UserInterventionPromptResponse> promptResponseRequested,
        UserDesktopWidgetPlacementStore placementStore)
    {
        _actionRequested = actionRequested;
        _promptResponseRequested = promptResponseRequested;
        _placementStore = placementStore;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = ExpandedWidth;
        Height = BaseHeight;

        _attentionTimer = new DispatcherTimer { Interval = AttentionDuration };
        _attentionTimer.Tick += (_, _) =>
        {
            _attentionTimer.Stop();
            SetAttentionVisible(false);
        };

        _promptTimer = new DispatcherTimer();
        _promptTimer.Tick += (_, _) =>
        {
            _promptTimer.Stop();
            ResolvePrompt(UserInterventionPromptResponseKind.Dismissed);
        };

        _reexpandTimer = new DispatcherTimer { Interval = ReexpandPollInterval };
        _reexpandTimer.Tick += (_, _) =>
        {
            if (_menuOpen || !_collapsed)
            {
                return;
            }

            if (!IsPointerNearWidget())
            {
                Expand();
            }
        };

        _collapseTimer = new DispatcherTimer { Interval = CollapseDelay };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_menuOpen || _collapsed || _dragInProgress || !IsPointerNearWidget())
            {
                return;
            }

            Collapse();
        };

        _rootBorder = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(17, 21, 26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(38, 47, 59)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.25,
                Color = Color.FromRgb(6, 9, 12)
            }
        };
        _rootBorder.MouseEnter += (_, _) => ScheduleCollapse();
        _rootBorder.MouseLeave += (_, _) => HandlePointerLeave();

        Grid shellGrid = new();
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _accentBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(61, 148, 255)),
            CornerRadius = new CornerRadius(16, 16, 0, 0)
        };
        Grid.SetRow(_accentBar, 0);
        shellGrid.Children.Add(_accentBar);

        Grid bodyGrid = new()
        {
            Margin = new Thickness(14, 12, 12, 12)
        };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(bodyGrid, 1);
        shellGrid.Children.Add(bodyGrid);

        _expandedStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(_expandedStack, 0);
        bodyGrid.Children.Add(_expandedStack);

        Border headerDragSurface = new()
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll
        };
        headerDragSurface.PreviewMouseLeftButtonDown += BeginDragFromHeader;
        _expandedStack.Children.Add(headerDragSurface);

        StackPanel headerStack = new()
        {
            Orientation = Orientation.Vertical
        };
        headerDragSurface.Child = headerStack;

        _titleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 16
        };
        headerStack.Children.Add(_titleText);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(163, 172, 184)),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 110d
        };
        headerStack.Children.Add(_statusText);

        WrapPanel metricsPanel = new()
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        _expandedStack.Children.Add(metricsPanel);

        _primaryMetricChip = CreateMetricChip(out _primaryMetricText);
        _secondaryMetricChip = CreateMetricChip(out _secondaryMetricText);
        _tertiaryMetricChip = CreateMetricChip(out _tertiaryMetricText);
        _quaternaryMetricChip = CreateMetricChip(out _quaternaryMetricText);
        _notificationsChip = CreateStatusChip("Notifications muted");
        _brightnessChip = CreateStatusChip("Brightness reduced");

        metricsPanel.Children.Add(_primaryMetricChip);
        metricsPanel.Children.Add(_secondaryMetricChip);
        metricsPanel.Children.Add(_tertiaryMetricChip);
        metricsPanel.Children.Add(_quaternaryMetricChip);
        metricsPanel.Children.Add(_notificationsChip);
        metricsPanel.Children.Add(_brightnessChip);

        _footerText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(125, 137, 152)),
            FontSize = 11.5,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _expandedStack.Children.Add(_footerText);

        _attentionCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(21, 28, 36)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 61, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        StackPanel attentionStack = new();
        _attentionCard.Child = attentionStack;
        _attentionTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        };
        _attentionMessageText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(177, 186, 196)),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 110d
        };
        attentionStack.Children.Add(_attentionTitleText);
        attentionStack.Children.Add(_attentionMessageText);
        _expandedStack.Children.Add(_attentionCard);

        _criticalCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 25, 25)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(111, 58, 52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        StackPanel criticalStack = new();
        _criticalCard.Child = criticalStack;
        _criticalTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(244, 231, 229)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        };
        _criticalMessageText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(226, 204, 199)),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 110d
        };
        criticalStack.Children.Add(_criticalTitleText);
        criticalStack.Children.Add(_criticalMessageText);
        WrapPanel criticalActions = new()
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        _criticalPrimaryButton = CreatePromptButton();
        _criticalPrimaryButton.Click += (_, _) => TriggerCriticalAction(_criticalPrimaryButton.Tag);
        _criticalSecondaryButton = CreatePromptButton();
        _criticalSecondaryButton.Click += (_, _) => TriggerCriticalAction(_criticalSecondaryButton.Tag);
        _criticalTertiaryButton = CreatePromptButton();
        _criticalTertiaryButton.Click += (_, _) => TriggerCriticalAction(_criticalTertiaryButton.Tag);
        criticalActions.Children.Add(_criticalPrimaryButton);
        criticalActions.Children.Add(_criticalSecondaryButton);
        criticalActions.Children.Add(_criticalTertiaryButton);
        criticalStack.Children.Add(criticalActions);
        _expandedStack.Children.Add(_criticalCard);

        _promptCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 31, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(55, 70, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        StackPanel promptStack = new();
        _promptCard.Child = promptStack;
        _promptTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5
        };
        _promptMessageText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(177, 186, 196)),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 110d
        };
        promptStack.Children.Add(_promptTitleText);
        promptStack.Children.Add(_promptMessageText);

        WrapPanel promptActions = new()
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        _promptAcceptButton = CreatePromptButton();
        _promptAcceptButton.Click += (_, _) => ResolvePrompt(UserInterventionPromptResponseKind.Accepted);
        _promptDismissButton = CreatePromptButton();
        _promptDismissButton.Click += (_, _) => ResolvePrompt(UserInterventionPromptResponseKind.Dismissed);
        _promptSnoozeButton = CreatePromptButton();
        _promptSnoozeButton.Click += (_, _) => ResolvePrompt(UserInterventionPromptResponseKind.Snoozed);
        promptActions.Children.Add(_promptAcceptButton);
        promptActions.Children.Add(_promptDismissButton);
        promptActions.Children.Add(_promptSnoozeButton);
        promptStack.Children.Add(promptActions);

        _promptSuppressButton = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromRgb(153, 167, 183)),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 6, 0, 0),
            FontSize = 11.2
        };
        _promptSuppressButton.Click += (_, _) => ResolvePrompt(UserInterventionPromptResponseKind.SuppressedByPreference);
        promptStack.Children.Add(_promptSuppressButton);
        _expandedStack.Children.Add(_promptCard);

        _menuButton = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(28, 35, 43)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            Cursor = Cursors.Hand,
            ToolTip = "Widget menu",
            Content = new TextBlock
            {
                Text = "\u2630",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _menuButton.Click += (_, _) => ToggleMenu();
        Grid.SetColumn(_menuButton, 1);
        bodyGrid.Children.Add(_menuButton);

        _menuPopup = new Popup
        {
            PlacementTarget = _menuButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade
        };
        _menuPopup.Opened += (_, _) =>
        {
            _menuOpen = true;
            _collapseTimer.Stop();
            Collapse();
        };
        _menuPopup.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!_collapsed)
            {
                return;
            }

            if (!IsPointerNearWidget())
            {
                Expand();
            }
            else
            {
                _reexpandTimer.Start();
            }
        };

        Border popupChrome = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 24, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 51, 63)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Color.FromRgb(6, 9, 12)
            }
        };
        StackPanel popupStack = new() { MinWidth = 208 };
        popupChrome.Child = popupStack;
        _menuPopup.Child = popupChrome;

        popupStack.Children.Add(CreateMenuSectionLabel("Navigate"));
        popupStack.Children.Add(CreateMenuButton("Open Now", UserDesktopWidgetAction.OpenNow));
        popupStack.Children.Add(CreateMenuButton("Open Actions", UserDesktopWidgetAction.OpenActions));
        popupStack.Children.Add(CreateMenuButton("Open Settings", UserDesktopWidgetAction.OpenSettings));
        popupStack.Children.Add(CreateMenuButton("Open Health", UserDesktopWidgetAction.OpenHealth));
        popupStack.Children.Add(CreateMenuSeparator());
        popupStack.Children.Add(CreateMenuSectionLabel("Quick actions"));
        _pauseFifteenButton = CreateMenuButton("Pause for 15m", UserDesktopWidgetAction.PauseFifteenMinutes);
        _pauseHourButton = CreateMenuButton("Pause for 1h", UserDesktopWidgetAction.PauseOneHour);
        _resumeButton = CreateMenuButton("Resume", UserDesktopWidgetAction.Resume);
        popupStack.Children.Add(_pauseFifteenButton);
        popupStack.Children.Add(_pauseHourButton);
        popupStack.Children.Add(_resumeButton);
        popupStack.Children.Add(CreateMenuButton("Recalibrate baseline", UserDesktopWidgetAction.Recalibrate));
        popupStack.Children.Add(CreateMenuSeparator());
        popupStack.Children.Add(CreateMenuButton("Reset position", UserDesktopWidgetAction.ResetPosition));

        _rootBorder.Child = shellGrid;
        Content = _rootBorder;
        SourceInitialized += (_, _) => InitializePlacement();
        SizeChanged += (_, _) => ApplyAnchorPosition();
    }

    public void ApplyState(UserDesktopWidgetState state)
    {
        _currentState = state;
        _titleText.Text = state.Title;
        _statusText.Text = state.StatusText;
        SetMetricText(_primaryMetricChip, _primaryMetricText, state.PrimaryMetricText);
        SetMetricText(_secondaryMetricChip, _secondaryMetricText, state.SecondaryMetricText);
        SetMetricText(_tertiaryMetricChip, _tertiaryMetricText, state.TertiaryMetricText);
        SetMetricText(_quaternaryMetricChip, _quaternaryMetricText, state.QuaternaryMetricText);
        _footerText.Text = state.FooterText;
        _footerText.Visibility = string.IsNullOrWhiteSpace(state.FooterText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _notificationsChip.Visibility = state.NotificationsSuppressed ? Visibility.Visible : Visibility.Collapsed;
        _brightnessChip.Visibility = state.BrightnessAdjusted ? Visibility.Visible : Visibility.Collapsed;
        _pauseFifteenButton.IsEnabled = state.CanPause;
        _pauseHourButton.IsEnabled = state.CanPause;
        _resumeButton.IsEnabled = state.CanResume;

        ApplyCriticalState(state.CriticalState);
        ApplyModeChrome(state.Mode);
    }

    public void PresentAttention(string title, string message)
    {
        _attentionTitleText.Text = string.IsNullOrWhiteSpace(title) ? "Ari" : title.Trim();
        _attentionMessageText.Text = string.IsNullOrWhiteSpace(message) ? "Ari has an update." : message.Trim();
        SetAttentionVisible(true);
        Expand();
        _attentionTimer.Stop();
        _attentionTimer.Start();
    }

    public void PresentPrompt(UserInterventionPrompt prompt)
    {
        _activePrompt = prompt;
        _promptTitleText.Text = string.IsNullOrWhiteSpace(prompt.Title) ? "Ari" : prompt.Title.Trim();
        _promptMessageText.Text = string.IsNullOrWhiteSpace(prompt.Message) ? "Ari has a suggestion." : prompt.Message.Trim();
        _promptAcceptButton.Content = prompt.AcceptLabel;
        _promptDismissButton.Content = prompt.DismissLabel;
        _promptSnoozeButton.Content = prompt.SnoozeLabel;
        _promptSuppressButton.Content = prompt.SuppressLabel;
        _promptCard.Visibility = Visibility.Visible;
        Expand();
        _collapseTimer.Stop();
        _promptTimer.Stop();
        _promptTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(prompt.DurationSeconds, 30, 300));
        _promptTimer.Start();
        UpdateWindowHeight();
    }

    public void ResetToDefaultPosition()
    {
        _placementStore.Clear();
        UserDesktopWidgetPlacement placement = GetDefaultPlacement();
        _expandedAnchorLeft = placement.Left;
        _expandedAnchorTop = placement.Top;
        _placementInitialized = true;
        ApplyAnchorPosition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        IntPtr extendedStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle);
        long updatedStyle = extendedStyle.ToInt64()
            | NativeMethods.WsExToolWindow
            | NativeMethods.WsExNoActivate;
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(updatedStyle));
    }

    private static Border CreateMetricChip(out TextBlock textBlock)
    {
        textBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            FontSize = 11.5,
            FontWeight = FontWeights.Medium
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(26, 33, 41)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 61, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 6),
            Child = textBlock
        };
    }

    private static void SetMetricText(Border chip, TextBlock textBlock, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            textBlock.Text = string.Empty;
            chip.Visibility = Visibility.Collapsed;
            return;
        }

        textBlock.Text = text.Trim();
        chip.Visibility = Visibility.Visible;
    }

    private static Border CreateStatusChip(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 30, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 72, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 6),
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(178, 192, 206)),
                FontSize = 11.2,
                FontWeight = FontWeights.Medium
            }
        };
    }

    private static Button CreatePromptButton()
    {
        return new Button
        {
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(Color.FromRgb(29, 38, 47)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 67, 82)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            MinWidth = 68
        };
    }

    private static TextBlock CreateMenuSectionLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Margin = new Thickness(4, 0, 4, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(138, 151, 166)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Border CreateMenuSeparator()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(40, 51, 63))
        };
    }

    private Button CreateMenuButton(string text, UserDesktopWidgetAction action)
    {
        Button button = new()
        {
            Content = text,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(26, 33, 41)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(47, 58, 71)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };
        button.Click += (_, _) =>
        {
            _menuPopup.IsOpen = false;
            _actionRequested(action);
        };

        return button;
    }

    private void ToggleMenu()
    {
        _menuPopup.IsOpen = !_menuPopup.IsOpen;
    }

    private void Collapse()
    {
        if (_collapsed || _activePrompt != null)
        {
            _reexpandTimer.Start();
            return;
        }

        _collapsed = true;
        _expandedStack.IsHitTestVisible = false;
        _expandedStack.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, TransitionDuration));
        AnimateWindowWidth(CollapsedWidth);
        _reexpandTimer.Start();
    }

    private void Expand()
    {
        if (!_collapsed)
        {
            return;
        }

        _collapsed = false;
        _expandedStack.IsHitTestVisible = true;
        _expandedStack.BeginAnimation(OpacityProperty, new DoubleAnimation(1d, TransitionDuration));
        AnimateWindowWidth(ExpandedWidth);
        _reexpandTimer.Stop();
    }

    private void SetAttentionVisible(bool visible)
    {
        _attentionCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowHeight();
    }

    private void AnimateWindowWidth(double targetWidth)
    {
        BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void AnimateWindowHeight(double targetHeight)
    {
        BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void BeginDragFromHeader(object sender, MouseButtonEventArgs e)
    {
        if (_collapsed || _menuOpen)
        {
            return;
        }

        _collapseTimer.Stop();
        try
        {
            _dragInProgress = true;
            DragMove();
            SaveCurrentPlacement();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _dragInProgress = false;
            if (!_menuOpen && IsPointerNearWidget())
            {
                ScheduleCollapse();
            }
        }
    }

    private void ScheduleCollapse()
    {
        if (_collapsed || _menuOpen || _dragInProgress || _activePrompt != null)
        {
            return;
        }

        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void HandlePointerLeave()
    {
        _collapseTimer.Stop();
        if (_menuOpen)
        {
            return;
        }

        if (_collapsed && !IsPointerNearWidget())
        {
            Expand();
        }
    }

    private void UpdateWindowHeight()
    {
        double targetHeight = BaseHeight;
        if (_attentionCard.Visibility == Visibility.Visible)
        {
            targetHeight += AttentionHeight;
        }

        if (_criticalCard.Visibility == Visibility.Visible)
        {
            targetHeight += CriticalHeight;
        }

        if (_promptCard.Visibility == Visibility.Visible)
        {
            targetHeight += PromptHeight;
        }

        AnimateWindowHeight(targetHeight);
    }

    private void InitializePlacement()
    {
        UserDesktopWidgetPlacement placement = _placementStore.Load() ?? GetDefaultPlacement();
        double currentHeight = ActualHeight > 0d ? ActualHeight : Height;
        UserDesktopWidgetPlacement normalized = NormalizePlacement(placement.Left, placement.Top, currentHeight);
        _expandedAnchorLeft = normalized.Left;
        _expandedAnchorTop = normalized.Top;
        _placementInitialized = true;
        ApplyAnchorPosition();
    }

    private void ApplyAnchorPosition()
    {
        if (!_placementInitialized)
        {
            return;
        }

        double currentHeight = ActualHeight > 0d ? ActualHeight : Height;
        UserDesktopWidgetPlacement normalized = NormalizePlacement(_expandedAnchorLeft, _expandedAnchorTop, currentHeight);
        _expandedAnchorLeft = normalized.Left;
        _expandedAnchorTop = normalized.Top;

        double currentWidth = ActualWidth > 0d ? ActualWidth : Width;
        Left = _collapsed
            ? _expandedAnchorLeft + (ExpandedWidth - currentWidth)
            : _expandedAnchorLeft;
        Top = _expandedAnchorTop;
    }

    private void SaveCurrentPlacement()
    {
        double currentHeight = ActualHeight > 0d ? ActualHeight : Height;
        double currentWidth = ActualWidth > 0d ? ActualWidth : Width;
        double expandedLeft = _collapsed
            ? Left - (ExpandedWidth - currentWidth)
            : Left;

        UserDesktopWidgetPlacement normalized = NormalizePlacement(expandedLeft, Top, currentHeight);
        _expandedAnchorLeft = normalized.Left;
        _expandedAnchorTop = normalized.Top;
        _placementInitialized = true;
        _placementStore.Save(normalized);
        ApplyAnchorPosition();
    }

    private UserDesktopWidgetPlacement GetDefaultPlacement()
    {
        Rect workArea = SystemParameters.WorkArea;
        double height = ActualHeight > 0d ? ActualHeight : Height;
        return NormalizePlacement(
            workArea.Right - ExpandedWidth - RightMargin,
            workArea.Top + TopMargin,
            height);
    }

    private static UserDesktopWidgetPlacement NormalizePlacement(double left, double top, double height)
    {
        Rect workArea = SystemParameters.WorkArea;
        double minLeft = workArea.Left + RightMargin;
        double maxLeft = workArea.Right - ExpandedWidth - RightMargin;
        double minTop = workArea.Top + TopMargin;
        double maxTop = workArea.Bottom - height - TopMargin;

        if (maxLeft < minLeft)
        {
            minLeft = workArea.Left;
            maxLeft = workArea.Right - ExpandedWidth;
        }

        if (maxTop < minTop)
        {
            minTop = workArea.Top;
            maxTop = workArea.Bottom - height;
        }

        return new UserDesktopWidgetPlacement(
            Left: Math.Min(Math.Max(left, minLeft), maxLeft),
            Top: Math.Min(Math.Max(top, minTop), maxTop));
    }

    private bool IsPointerNearWidget()
    {
        Point topLeft = PointToScreen(new Point(0, 0));
        double width = ActualWidth > 0d ? ActualWidth : Width;
        double height = ActualHeight > 0d ? ActualHeight : Height;

        Rect bounds = new(topLeft.X, topLeft.Y, width, height);
        bounds.Inflate(48d, 36d);

        System.Drawing.Point cursor = FormsCursor.Position;
        return bounds.Contains(new Point(cursor.X, cursor.Y));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmNcHitTest
            && ShouldUseTransparentHitTest(GetScreenPointFromLParam(lParam)))
        {
            handled = true;
            return new IntPtr(NativeMethods.HtTransparent);
        }

        return IntPtr.Zero;
    }

    private bool ShouldUseTransparentHitTest(Point screenPoint)
    {
        if (!_collapsed || _menuOpen || !_placementInitialized || _activePrompt != null)
        {
            return false;
        }

        try
        {
            Point localPoint = PointFromScreen(screenPoint);
            Rect menuButtonBounds = GetLocalBounds(_menuButton);
            if (menuButtonBounds.IsEmpty)
            {
                return false;
            }

            menuButtonBounds.Inflate(8d, 8d);
            return !menuButtonBounds.Contains(localPoint);
        }
        catch
        {
            return false;
        }
    }

    private Rect GetLocalBounds(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0d || element.ActualHeight <= 0d)
        {
            return Rect.Empty;
        }

        GeneralTransform transform = element.TransformToAncestor(this);
        return transform.TransformBounds(new Rect(0d, 0d, element.ActualWidth, element.ActualHeight));
    }

    private static Point GetScreenPointFromLParam(IntPtr lParam)
    {
        long value = lParam.ToInt64();
        int x = unchecked((short)(value & 0xffff));
        int y = unchecked((short)((value >> 16) & 0xffff));
        return new Point(x, y);
    }

    private void ApplyModeChrome(UserDesktopWidgetMode mode)
    {
        (Color Accent, Color Button) palette = mode switch
        {
            UserDesktopWidgetMode.DeepWork => (Color.FromRgb(53, 166, 122), Color.FromRgb(22, 66, 51)),
            UserDesktopWidgetMode.Overload => (Color.FromRgb(232, 97, 64), Color.FromRgb(86, 40, 29)),
            UserDesktopWidgetMode.Limited => (Color.FromRgb(236, 189, 86), Color.FromRgb(87, 67, 22)),
            UserDesktopWidgetMode.Paused => (Color.FromRgb(132, 152, 176), Color.FromRgb(45, 58, 71)),
            _ => (Color.FromRgb(61, 148, 255), Color.FromRgb(28, 53, 78))
        };

        _accentBar.Background = new SolidColorBrush(palette.Accent);
        _menuButton.Background = new SolidColorBrush(palette.Button);
    }

    private void ApplyCriticalState(UserDesktopWidgetCriticalState? criticalState)
    {
        if (criticalState == null)
        {
            _criticalCard.Visibility = Visibility.Collapsed;
            UpdateWindowHeight();
            return;
        }

        _criticalTitleText.Text = criticalState.Title;
        _criticalMessageText.Text = criticalState.Message;
        ConfigureCriticalButton(_criticalPrimaryButton, criticalState.PrimaryLabel, criticalState.PrimaryAction);
        ConfigureCriticalButton(_criticalSecondaryButton, criticalState.SecondaryLabel, criticalState.SecondaryAction);
        ConfigureCriticalButton(_criticalTertiaryButton, criticalState.TertiaryLabel, criticalState.TertiaryAction);
        _criticalCard.Visibility = Visibility.Visible;
        UpdateWindowHeight();
    }

    private void ConfigureCriticalButton(
        Button button,
        string? label,
        UserDesktopWidgetAction? action)
    {
        if (action == null || string.IsNullOrWhiteSpace(label))
        {
            button.Visibility = Visibility.Collapsed;
            button.Tag = null;
            button.Content = string.Empty;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Tag = action.Value;
        button.Content = label.Trim();
    }

    private void TriggerCriticalAction(object? tag)
    {
        if (tag is not UserDesktopWidgetAction action)
        {
            return;
        }

        _actionRequested(action);
    }

    private void ResolvePrompt(UserInterventionPromptResponseKind kind)
    {
        UserInterventionPrompt? prompt = _activePrompt;
        if (prompt == null)
        {
            return;
        }

        _activePrompt = null;
        _promptTimer.Stop();
        _promptCard.Visibility = Visibility.Collapsed;
        UpdateWindowHeight();
        if (!_menuOpen && IsPointerNearWidget())
        {
            ScheduleCollapse();
        }

        _promptResponseRequested(new UserInterventionPromptResponse(
            InterventionId: prompt.InterventionId,
            TipKey: prompt.TipKey,
            Kind: kind));
    }

    private static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const int WmNcHitTest = 0x0084;
        internal const int HtTransparent = -1;
        internal const long WsExToolWindow = 0x00000080L;
        internal const long WsExNoActivate = 0x08000000L;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
