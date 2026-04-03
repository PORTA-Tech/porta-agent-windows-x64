namespace Porta.UserAgent.Interventions;

using System.ComponentModel;
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
    private const int WindowLifecycleOpen = 0;
    private const int WindowLifecycleClosing = 1;
    private const int WindowLifecycleClosed = 2;
    private const double ExpandedWidth = 392d;
    private const double CollapsedWidth = 42d;
    private const double BaseHeight = 72d;
    private const double CollapsedHeight = 42d;
    private const double TopMargin = 16d;
    private const double RightMargin = 16d;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan AttentionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinimumCriticalSpotlightDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumCriticalSpotlightDuration = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReexpandPollInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(220);

    private readonly Action<UserDesktopWidgetAction> _actionRequested;
    private readonly Action<UserInterventionPromptResponse> _promptResponseRequested;
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly DispatcherTimer _attentionTimer;
    private readonly DispatcherTimer _criticalSpotlightTimer;
    private readonly DispatcherTimer _promptTimer;
    private readonly DispatcherTimer _reexpandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly Border _rootBorder;
    private readonly StackPanel _expandedStack;
    private readonly Grid _summaryRow;
    private readonly UniformGrid _metricsPanel;
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
    private readonly Button _hideWidgetButton;
    private readonly Button _pauseFifteenButton;
    private readonly Button _pauseHourButton;
    private readonly Button _resumeButton;

    private HwndSource? _hwndSource;
    private bool _collapsed;
    private bool _menuOpen;
    private bool _dragInProgress;
    private bool _hiddenByUser;
    private bool _placementInitialized;
    private double _expandedAnchorLeft;
    private double _expandedAnchorTop;
    private bool _allowClose;
    private bool _criticalSpotlightVisible;
    private string? _criticalSpotlightKey;
    private int _windowLifecycleState = WindowLifecycleOpen;
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

        _criticalSpotlightTimer = new DispatcherTimer();

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
            if (_menuOpen || _collapsed || _dragInProgress || !IsPointerInsideWidget())
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
        _rootBorder.PreviewMouseLeftButtonDown += BeginDragFromSurface;
        _rootBorder.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            _reexpandTimer.Stop();
            Expand();
        };
        _rootBorder.MouseLeave += (_, _) => HandlePointerLeave();

        Grid shellGrid = new();

        Grid bodyGrid = new()
        {
            Margin = new Thickness(10, 7, 8, 7)
        };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shellGrid.Children.Add(bodyGrid);

        _expandedStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 8, 0)
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
            FontSize = 13,
            Visibility = Visibility.Collapsed
        };
        headerStack.Children.Add(_titleText);

        _statusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(163, 172, 184)),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 0),
            MaxWidth = ExpandedWidth - 34d,
            Visibility = Visibility.Collapsed
        };
        headerStack.Children.Add(_statusText);

        _summaryRow = new Grid
        {
            Margin = new Thickness(0, 4, 0, 0)
        };
        _summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _summaryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _expandedStack.Children.Add(_summaryRow);

        _metricsPanel = new UniformGrid
        {
            Columns = 3
        };
        Grid.SetColumn(_metricsPanel, 0);
        _summaryRow.Children.Add(_metricsPanel);

        _primaryMetricChip = CreateMetricChip(out _primaryMetricText);
        _secondaryMetricChip = CreateMetricChip(out _secondaryMetricText);
        _tertiaryMetricChip = CreateMetricChip(out _tertiaryMetricText);
        _quaternaryMetricChip = CreateMetricChip(out _quaternaryMetricText);
        _quaternaryMetricChip.Visibility = Visibility.Collapsed;
        _notificationsChip = CreateStatusChip("Notifications muted");
        _brightnessChip = CreateStatusChip("Brightness reduced");

        _metricsPanel.Children.Add(_primaryMetricChip);
        _metricsPanel.Children.Add(_secondaryMetricChip);
        _metricsPanel.Children.Add(_tertiaryMetricChip);

        _footerText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(125, 137, 152)),
            FontSize = 10.8,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        _expandedStack.Children.Add(_footerText);

        _attentionCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(21, 28, 36)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 61, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
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
            MaxWidth = ExpandedWidth - 38d
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
            Margin = new Thickness(0, 8, 0, 0),
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
            MaxWidth = ExpandedWidth - 38d
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
        _criticalSpotlightTimer.Tick += (_, _) =>
        {
            _criticalSpotlightTimer.Stop();
            _criticalSpotlightVisible = false;
            _criticalCard.Visibility = Visibility.Collapsed;
            UpdatePrimarySurfaceVisibility();
            UpdateWindowHeight();
        };

        _promptCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 31, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(55, 70, 88)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 10, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
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
            MaxWidth = ExpandedWidth - 38d
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
            Width = 28,
            Height = 28,
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
        _summaryRow.Children.Add(_menuButton);

        _menuPopup = new Popup
        {
            PlacementTarget = _menuButton,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = 10
        };
        _menuPopup.Opened += (_, _) =>
        {
            _menuOpen = true;
            _collapseTimer.Stop();
            _reexpandTimer.Stop();
            Expand();
        };
        _menuPopup.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (HasBlockingSurfaceVisible())
            {
                return;
            }

            if (IsPointerInsideWidget())
            {
                ScheduleCollapse();
            }
        };

        Border popupChrome = new()
        {
            Background = new SolidColorBrush(Color.FromRgb(27, 31, 37)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 70, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(8),
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.24,
                Color = Color.FromRgb(6, 9, 12)
            }
        };
        StackPanel popupStack = new() { MinWidth = 188 };
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
        _hideWidgetButton = CreateMenuButton("Hide widget", UserDesktopWidgetAction.HideWidget);
        popupStack.Children.Add(_hideWidgetButton);

        _rootBorder.Child = shellGrid;
        Content = _rootBorder;
        SourceInitialized += (_, _) => InitializePlacement();
        SizeChanged += (_, _) => ApplyAnchorPosition();
    }

    public void ApplyState(UserDesktopWidgetState state)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _currentState = state;
        string normalizedTitle = NormalizeWidgetTitle(state.Title);
        _titleText.Text = normalizedTitle;
        _statusText.Text = state.StatusText;
        SetMetricText(_primaryMetricChip, _primaryMetricText, state.PrimaryMetricText);
        SetMetricText(_secondaryMetricChip, _secondaryMetricText, state.SecondaryMetricText);
        SetMetricText(_tertiaryMetricChip, _tertiaryMetricText, state.TertiaryMetricText);
        _footerText.Text = state.FooterText;
        _notificationsChip.Visibility = state.NotificationsSuppressed ? Visibility.Visible : Visibility.Collapsed;
        _brightnessChip.Visibility = state.BrightnessAdjusted ? Visibility.Visible : Visibility.Collapsed;
        _pauseFifteenButton.IsEnabled = state.CanPause;
        _pauseHourButton.IsEnabled = state.CanPause;
        _resumeButton.IsEnabled = state.CanResume;

        ApplyCriticalState(state.CriticalState);
        UpdatePrimarySurfaceVisibility();
        ApplyModeChrome(state.Mode);
        UpdateWindowHeight();
    }

    public void PresentAttention(string title, string message)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        string normalizedTitle = NormalizeCardTitle(title, fallback: string.Empty);
        _attentionTitleText.Text = normalizedTitle;
        _attentionTitleText.Visibility = string.IsNullOrWhiteSpace(normalizedTitle)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _attentionMessageText.Text = string.IsNullOrWhiteSpace(message) ? "There is an update." : message.Trim();
        SetAttentionVisible(true);
        Expand();
        _attentionTimer.Stop();
        _attentionTimer.Start();
    }

    public void PresentPrompt(UserInterventionPrompt prompt)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _activePrompt = prompt;
        string normalizedTitle = NormalizeCardTitle(prompt.Title, fallback: string.Empty);
        _promptTitleText.Text = normalizedTitle;
        _promptTitleText.Visibility = string.IsNullOrWhiteSpace(normalizedTitle)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _promptMessageText.Text = string.IsNullOrWhiteSpace(prompt.Message) ? "There is a suggestion." : prompt.Message.Trim();
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

    public void HideWidget()
    {
        if (_hiddenByUser || !CanAcceptUiActions)
        {
            return;
        }

        _hiddenByUser = true;
        _menuPopup.IsOpen = false;
        Hide();
    }

    public void ShowWidgetIfHidden()
    {
        if (!_hiddenByUser || !CanAcceptUiActions)
        {
            return;
        }

        _hiddenByUser = false;
        Show();
        ApplyAnchorPosition();
        UpdateWindowHeight();
    }

    public void AllowCloseForShutdown()
    {
        _allowClose = true;
    }

    internal bool CanAcceptUiActions => Volatile.Read(ref _windowLifecycleState) == WindowLifecycleOpen;

    internal bool IsClosingOrClosed => Volatile.Read(ref _windowLifecycleState) != WindowLifecycleOpen;

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

    protected override void OnClosing(CancelEventArgs e)
    {
        SetWindowLifecycleState(WindowLifecycleClosing);
        if (!_allowClose)
        {
            e.Cancel = true;
            SetWindowLifecycleState(WindowLifecycleOpen);
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SetWindowLifecycleState(WindowLifecycleClosed);
        base.OnClosed(e);
    }

    private static Border CreateMetricChip(out TextBlock textBlock)
    {
        textBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextAlignment = TextAlignment.Left
        };

        return new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
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
                FontSize = 10.8,
                FontWeight = FontWeights.Medium
            }
        };
    }

    private static string NormalizeWidgetTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        string trimmed = title.Trim();
        return trimmed.Equals("Ari", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed;
    }

    private static string NormalizeCardTitle(string? title, string fallback)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return fallback;
        }

        string trimmed = title.Trim();
        if (trimmed.Equals("Ari", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        if (trimmed.Equals("Ari limited", StringComparison.OrdinalIgnoreCase))
        {
            return "Attention";
        }

        return trimmed;
    }

    private static Button CreatePromptButton()
    {
        return new Button
        {
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 6),
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
            Margin = new Thickness(4, 2, 4, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(138, 151, 166)),
            FontSize = 10.8,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
    }

    private static Border CreateMenuSeparator()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 6),
            Background = new SolidColorBrush(Color.FromRgb(40, 51, 63))
        };
    }

    private Button CreateMenuButton(string text, UserDesktopWidgetAction action)
    {
        SolidColorBrush hoverBackground = new(Color.FromRgb(29, 35, 42));
        Button button = new()
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap
            },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        button.MouseEnter += (_, _) =>
        {
            button.Background = hoverBackground;
        };
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brushes.Transparent;
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
        if (!CanAcceptUiActions)
        {
            return;
        }

        _menuPopup.IsOpen = !_menuPopup.IsOpen;
    }

    private void Collapse()
    {
        return;
    }

    private void Expand()
    {
        if (!_collapsed || !CanAcceptUiActions)
        {
            return;
        }

        _collapsed = false;
        _expandedStack.Visibility = Visibility.Visible;
        _expandedStack.IsHitTestVisible = true;
        _expandedStack.BeginAnimation(OpacityProperty, null);
        _expandedStack.Opacity = 1d;
        AnimateWindowWidth(ExpandedWidth);
        UpdateWindowHeight();
        _reexpandTimer.Stop();
    }

    private void SetAttentionVisible(bool visible)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _attentionCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
        {
            _collapseTimer.Stop();
            Expand();
        }

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

        BeginDragCore();
    }

    private void BeginDragFromSurface(object sender, MouseButtonEventArgs e)
    {
        if (_collapsed || _menuOpen || HasBlockingSurfaceVisible())
        {
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsInteractiveElement(source))
        {
            return;
        }

        BeginDragCore();
        e.Handled = true;
    }

    private void BeginDragCore()
    {
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
        _collapseTimer.Stop();
    }

    private void HandlePointerLeave()
    {
        _collapseTimer.Stop();
    }

    private void UpdateWindowHeight()
    {
        if (_collapsed)
        {
            AnimateWindowHeight(CollapsedHeight);
            return;
        }

        _rootBorder.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        double targetHeight = Math.Max(BaseHeight, _rootBorder.DesiredSize.Height);
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
        double width = _collapsed ? ExpandedWidth : (ActualWidth > 0d ? ActualWidth : Width);
        double height = ActualHeight > 0d ? ActualHeight : Height;
        double left = _collapsed
            ? _expandedAnchorLeft
            : Left;
        double top = _collapsed
            ? _expandedAnchorTop
            : Top;

        Rect bounds = new(left, top, width, height);
        bounds.Inflate(28d, 22d);

        System.Drawing.Point cursor = FormsCursor.Position;
        return bounds.Contains(new Point(cursor.X, cursor.Y));
    }

    private bool IsPointerInsideWidget()
    {
        if (!_placementInitialized)
        {
            return false;
        }

        double width = ActualWidth > 0d ? ActualWidth : Width;
        double height = ActualHeight > 0d ? ActualHeight : Height;
        Rect bounds = new(Left, Top, width, height);
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
        if (!_collapsed || _menuOpen || !_placementInitialized || HasBlockingSurfaceVisible())
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
        _ = mode;
        _menuButton.Background = new SolidColorBrush(Color.FromRgb(28, 35, 43));
    }

    private void ApplyCriticalState(UserDesktopWidgetCriticalState? criticalState)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        if (criticalState == null)
        {
            _criticalSpotlightTimer.Stop();
            _criticalSpotlightVisible = false;
            _criticalSpotlightKey = null;
            _criticalCard.Visibility = Visibility.Collapsed;
            UpdateWindowHeight();
            return;
        }

        string criticalSpotlightKey = BuildCriticalSpotlightKey(criticalState);
        if (!string.Equals(_criticalSpotlightKey, criticalSpotlightKey, StringComparison.Ordinal))
        {
            _criticalSpotlightKey = criticalSpotlightKey;
            _criticalSpotlightVisible = true;
            _criticalSpotlightTimer.Stop();
            _criticalSpotlightTimer.Interval = ComputeCriticalSpotlightDuration(criticalState);
            _criticalSpotlightTimer.Start();
        }

        string normalizedTitle = NormalizeCardTitle(criticalState.Title, fallback: "Attention");
        _criticalTitleText.Text = normalizedTitle;
        _criticalTitleText.Visibility = string.IsNullOrWhiteSpace(normalizedTitle)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _criticalMessageText.Text = criticalState.Message;
        ConfigureCriticalButton(_criticalPrimaryButton, criticalState.PrimaryLabel, criticalState.PrimaryAction);
        ConfigureCriticalButton(_criticalSecondaryButton, criticalState.SecondaryLabel, criticalState.SecondaryAction);
        ConfigureCriticalButton(_criticalTertiaryButton, criticalState.TertiaryLabel, criticalState.TertiaryAction);
        _criticalCard.Visibility = _criticalSpotlightVisible ? Visibility.Visible : Visibility.Collapsed;
        _collapseTimer.Stop();
        Expand();
        UpdateWindowHeight();
    }

    private void UpdatePrimarySurfaceVisibility()
    {
        UserDesktopWidgetState? state = _currentState;
        if (state == null)
        {
            return;
        }

        bool suppressForCriticalSpotlight = _criticalSpotlightVisible;
        bool suppressDuplicateStatus = state.CriticalState != null;
        string normalizedTitle = NormalizeWidgetTitle(state.Title);

        _titleText.Visibility = !suppressForCriticalSpotlight
            && !suppressDuplicateStatus
            && !string.IsNullOrWhiteSpace(normalizedTitle)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _statusText.Visibility = !suppressForCriticalSpotlight
            && !suppressDuplicateStatus
            && !string.IsNullOrWhiteSpace(state.StatusText)
            ? Visibility.Visible
            : Visibility.Collapsed;

        bool summaryMetricsVisible = !suppressForCriticalSpotlight
            && HasSummaryMetrics(state);
        _metricsPanel.Visibility = summaryMetricsVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
        _summaryRow.Visibility = summaryMetricsVisible || _menuButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        _footerText.Visibility = !suppressForCriticalSpotlight
            && !string.IsNullOrWhiteSpace(state.FooterText)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool HasSummaryMetrics(UserDesktopWidgetState state)
    {
        return !string.IsNullOrWhiteSpace(state.PrimaryMetricText)
            || !string.IsNullOrWhiteSpace(state.SecondaryMetricText)
            || !string.IsNullOrWhiteSpace(state.TertiaryMetricText)
            || !string.IsNullOrWhiteSpace(state.QuaternaryMetricText);
    }

    private static string BuildCriticalSpotlightKey(UserDesktopWidgetCriticalState criticalState)
    {
        return string.Join(
            "|",
            criticalState.Title,
            criticalState.Message,
            criticalState.PrimaryLabel,
            criticalState.SecondaryLabel,
            criticalState.TertiaryLabel);
    }

    private static TimeSpan ComputeCriticalSpotlightDuration(UserDesktopWidgetCriticalState criticalState)
    {
        int messageLength = (criticalState.Title?.Length ?? 0) + (criticalState.Message?.Length ?? 0);
        double seconds = 4.5d + Math.Ceiling(messageLength / 55d);
        return TimeSpan.FromSeconds(Math.Clamp(
            seconds,
            MinimumCriticalSpotlightDuration.TotalSeconds,
            MaximumCriticalSpotlightDuration.TotalSeconds));
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
        if (IsClosingOrClosed)
        {
            return;
        }

        UserInterventionPrompt? prompt = _activePrompt;
        if (prompt == null)
        {
            return;
        }

        _activePrompt = null;
        _promptTimer.Stop();
        _promptCard.Visibility = Visibility.Collapsed;
        UpdateWindowHeight();

        _promptResponseRequested(new UserInterventionPromptResponse(
            InterventionId: prompt.InterventionId,
            TipKey: prompt.TipKey,
            Kind: kind));
    }

    private bool HasBlockingSurfaceVisible()
    {
        return _activePrompt != null
            || _attentionCard.Visibility == Visibility.Visible
            || _criticalCard.Visibility == Visibility.Visible;
    }

    private static bool IsInteractiveElement(DependencyObject child)
    {
        return FindAncestor<Button>(child) != null
            || FindAncestor<Popup>(child) != null
            || FindAncestor<TextBox>(child) != null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
            {
                return match;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private void SetWindowLifecycleState(int state)
    {
        Interlocked.Exchange(ref _windowLifecycleState, state);
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
