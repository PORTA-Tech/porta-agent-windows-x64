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
    private const double ExpandedWidth = 218d;
    private const double CollapsedWidth = 42d;
    private const double BaseHeight = 156d;
    private const double CollapsedHeight = 42d;
    private const double TopMargin = 16d;
    private const double RightMargin = 16d;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan AttentionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReexpandPollInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(220);

    private readonly Action<UserDesktopWidgetAction> _actionRequested;
    private readonly Action<UserInterventionPromptResponse> _promptResponseRequested;
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly DispatcherTimer _attentionTimer;
    private readonly DispatcherTimer _promptTimer;
    private readonly DispatcherTimer _reexpandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly Border _rootBorder;
    private readonly StackPanel _expandedStack;
    private readonly Grid _headerRow;
    private readonly StackPanel _metricsPanel;
    private readonly TextBlock _titleText;
    private readonly Border _primaryMetricChip;
    private readonly TextBlock _primaryMetricLabelText;
    private readonly TextBlock _primaryMetricText;
    private readonly Border _secondaryMetricChip;
    private readonly TextBlock _secondaryMetricLabelText;
    private readonly TextBlock _secondaryMetricText;
    private readonly Border _tertiaryMetricChip;
    private readonly TextBlock _tertiaryMetricLabelText;
    private readonly TextBlock _tertiaryMetricText;
    private readonly Border _messageCard;
    private readonly TextBlock _messageTitleText;
    private readonly TextBlock _messageBodyText;
    private readonly TextBlock _messageFooterText;
    private readonly WrapPanel _messageActionsPanel;
    private readonly Button _messagePrimaryButton;
    private readonly Button _messageSecondaryButton;
    private readonly Button _messageTertiaryButton;
    private readonly Button _messageSuppressButton;
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
    private int _windowLifecycleState = WindowLifecycleOpen;
    private UserDesktopWidgetState? _currentState;
    private UserInterventionPrompt? _activePrompt;
    private WidgetAttentionMessage? _attentionMessage;

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
            Margin = new Thickness(10, 8, 10, 8)
        };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shellGrid.Children.Add(bodyGrid);

        _expandedStack = new StackPanel
        {
            Orientation = Orientation.Vertical
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

        _headerRow = new Grid();
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerDragSurface.Child = _headerRow;

        _titleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_titleText, 0);
        _headerRow.Children.Add(_titleText);

        _menuButton = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(28, 35, 43)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            Cursor = Cursors.Hand,
            ToolTip = "Widget menu",
            Content = new TextBlock
            {
                Text = "\u2630",
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _menuButton.Click += (_, _) => ToggleMenu();
        Grid.SetColumn(_menuButton, 1);
        _headerRow.Children.Add(_menuButton);

        _metricsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _expandedStack.Children.Add(_metricsPanel);

        _primaryMetricChip = CreateMetricChip(out _primaryMetricLabelText, out _primaryMetricText);
        _secondaryMetricChip = CreateMetricChip(out _secondaryMetricLabelText, out _secondaryMetricText);
        _tertiaryMetricChip = CreateMetricChip(out _tertiaryMetricLabelText, out _tertiaryMetricText);
        _metricsPanel.Children.Add(_primaryMetricChip);
        _metricsPanel.Children.Add(_secondaryMetricChip);
        _metricsPanel.Children.Add(_tertiaryMetricChip);

        _messageCard = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 9, 10, 8),
            Margin = new Thickness(0, 8, 0, 0)
        };
        StackPanel messageStack = new();
        _messageCard.Child = messageStack;
        _messageTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        };
        _messageBodyText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(196, 205, 215)),
            FontSize = 11.2,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 42d
        };
        _messageFooterText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(153, 167, 183)),
            FontSize = 10.4,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = ExpandedWidth - 42d
        };
        messageStack.Children.Add(_messageTitleText);
        messageStack.Children.Add(_messageBodyText);
        messageStack.Children.Add(_messageFooterText);

        _messageActionsPanel = new WrapPanel
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        _messagePrimaryButton = CreatePromptButton();
        _messagePrimaryButton.Click += OnPrimaryMessageAction;
        _messageSecondaryButton = CreatePromptButton();
        _messageSecondaryButton.Click += OnSecondaryMessageAction;
        _messageTertiaryButton = CreatePromptButton();
        _messageTertiaryButton.Click += OnTertiaryMessageAction;
        _messageActionsPanel.Children.Add(_messagePrimaryButton);
        _messageActionsPanel.Children.Add(_messageSecondaryButton);
        _messageActionsPanel.Children.Add(_messageTertiaryButton);
        messageStack.Children.Add(_messageActionsPanel);

        _messageSuppressButton = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Color.FromRgb(153, 167, 183)),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        _messageSuppressButton.Click += (_, _) => ResolvePrompt(UserInterventionPromptResponseKind.SuppressedByPreference);
        messageStack.Children.Add(_messageSuppressButton);
        _expandedStack.Children.Add(_messageCard);

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

        popupStack.Children.Add(CreateMenuSectionLabel("Control Center"));
        popupStack.Children.Add(CreateMenuButton("Today", UserDesktopWidgetAction.OpenNow));
        popupStack.Children.Add(CreateMenuButton("History", UserDesktopWidgetAction.OpenHistory));
        popupStack.Children.Add(CreateMenuButton("Actions", UserDesktopWidgetAction.OpenActions));
        popupStack.Children.Add(CreateMenuButton("Tracking", UserDesktopWidgetAction.OpenTracking));
        popupStack.Children.Add(CreateMenuButton("Settings", UserDesktopWidgetAction.OpenSettings));
        popupStack.Children.Add(CreateMenuButton("System", UserDesktopWidgetAction.OpenHealth));
        popupStack.Children.Add(CreateMenuSeparator());
        popupStack.Children.Add(CreateMenuSectionLabel("Quick actions"));
        _pauseFifteenButton = CreateMenuButton("Pause 15m", UserDesktopWidgetAction.PauseFifteenMinutes);
        _pauseHourButton = CreateMenuButton("Pause 1h", UserDesktopWidgetAction.PauseOneHour);
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
        _titleText.Text = NormalizeWidgetTitle(state.Title);
        SetMetric(_primaryMetricChip, _primaryMetricLabelText, _primaryMetricText, state.PrimaryMetric);
        SetMetric(_secondaryMetricChip, _secondaryMetricLabelText, _secondaryMetricText, state.SecondaryMetric);
        SetMetric(_tertiaryMetricChip, _tertiaryMetricLabelText, _tertiaryMetricText, state.TertiaryMetric);
        _pauseFifteenButton.IsEnabled = state.CanPause;
        _pauseHourButton.IsEnabled = state.CanPause;
        _resumeButton.IsEnabled = state.CanResume;

        RefreshInlineMessage();
        ApplyModeChrome(state.Mode);
        UpdateWindowHeight();
    }

    public void PresentAttention(string title, string message)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _attentionMessage = new WidgetAttentionMessage(
            Title: NormalizeCardTitle(title, fallback: string.Empty),
            Message: string.IsNullOrWhiteSpace(message) ? "There is an update." : message.Trim());
        RefreshInlineMessage();
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
        RefreshInlineMessage();
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

    private static Border CreateMetricChip(out TextBlock labelTextBlock, out TextBlock valueTextBlock)
    {
        labelTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(133, 146, 161)),
            FontSize = 10.2,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.Wrap
        };

        valueTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(236, 241, 246)),
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 110d
        };

        Grid contentGrid = new();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(labelTextBlock, 0);
        Grid.SetColumn(valueTextBlock, 1);
        contentGrid.Children.Add(labelTextBlock);
        contentGrid.Children.Add(valueTextBlock);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(40, 49, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 8, 9, 8),
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = contentGrid
        };
    }

    private static void SetMetric(
        Border chip,
        TextBlock labelTextBlock,
        TextBlock valueTextBlock,
        UserDesktopWidgetMetric metric)
    {
        labelTextBlock.Text = string.IsNullOrWhiteSpace(metric.Label) ? string.Empty : metric.Label.Trim();
        valueTextBlock.Text = string.IsNullOrWhiteSpace(metric.Value) ? "n/a" : metric.Value.Trim();
        chip.Visibility = Visibility.Visible;
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
            return "Tracking limited";
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

        _attentionMessage = visible ? _attentionMessage : null;
        if (visible)
        {
            _collapseTimer.Stop();
            Expand();
        }

        RefreshInlineMessage();
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
        Color accent = mode switch
        {
            UserDesktopWidgetMode.DeepWork => Color.FromRgb(35, 82, 112),
            UserDesktopWidgetMode.Overload => Color.FromRgb(114, 77, 32),
            UserDesktopWidgetMode.Limited => Color.FromRgb(90, 74, 40),
            UserDesktopWidgetMode.Paused => Color.FromRgb(70, 76, 88),
            _ => Color.FromRgb(28, 35, 43)
        };
        _menuButton.Background = new SolidColorBrush(accent);
    }

    private void RefreshInlineMessage()
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        if (_activePrompt != null)
        {
            RenderPromptMessage(_activePrompt);
            return;
        }

        if (_attentionMessage != null)
        {
            RenderInlineMessage(
                title: _attentionMessage.Title,
                body: _attentionMessage.Message,
                footer: string.Empty,
                severity: UserDesktopWidgetMessageSeverity.Info);
            return;
        }

        UserDesktopWidgetState? state = _currentState;
        if (state == null)
        {
            return;
        }

        if (state.CriticalState != null)
        {
            RenderInlineMessage(
                title: NormalizeCardTitle(state.CriticalState.Title, fallback: NormalizeWidgetTitle(state.Title)),
                body: state.CriticalState.Message,
                footer: string.Empty,
                severity: UserDesktopWidgetMessageSeverity.Critical,
                primaryAction: state.CriticalState.PrimaryAction,
                primaryLabel: state.CriticalState.PrimaryLabel,
                secondaryAction: state.CriticalState.SecondaryAction,
                secondaryLabel: state.CriticalState.SecondaryLabel,
                tertiaryAction: state.CriticalState.TertiaryAction,
                tertiaryLabel: state.CriticalState.TertiaryLabel);
            return;
        }

        string normalizedTitle = NormalizeCardTitle(state.Title, fallback: "Today");
        string body = string.IsNullOrWhiteSpace(state.StatusText)
            ? "Today is updating normally."
            : state.StatusText.Trim();
        string footer = string.IsNullOrWhiteSpace(state.FooterText)
            ? string.Empty
            : state.FooterText.Trim();

        RenderInlineMessage(
            title: normalizedTitle,
            body: body,
            footer: footer,
            severity: state.MessageSeverity);
    }

    private void RenderPromptMessage(UserInterventionPrompt prompt)
    {
        RenderInlineMessage(
            title: NormalizeCardTitle(prompt.Title, fallback: "Suggestion"),
            body: string.IsNullOrWhiteSpace(prompt.Message) ? "There is a suggestion." : prompt.Message.Trim(),
            footer: string.Empty,
            severity: UserDesktopWidgetMessageSeverity.Info,
            suppressLabel: prompt.SuppressLabel);

        ConfigurePromptButton(_messagePrimaryButton, prompt.AcceptLabel);
        ConfigurePromptButton(_messageSecondaryButton, prompt.DismissLabel);
        ConfigurePromptButton(_messageTertiaryButton, prompt.SnoozeLabel);
        _messageActionsPanel.Visibility = Visibility.Visible;
    }

    private void RenderInlineMessage(
        string title,
        string body,
        string footer,
        UserDesktopWidgetMessageSeverity severity,
        UserDesktopWidgetAction? primaryAction = null,
        string primaryLabel = "",
        UserDesktopWidgetAction? secondaryAction = null,
        string secondaryLabel = "",
        UserDesktopWidgetAction? tertiaryAction = null,
        string tertiaryLabel = "",
        string suppressLabel = "")
    {
        ApplyMessageChrome(severity);

        _messageTitleText.Text = title;
        _messageTitleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        _messageBodyText.Text = string.IsNullOrWhiteSpace(body) ? "Today is updating normally." : body;
        _messageBodyText.Visibility = Visibility.Visible;
        _messageFooterText.Text = footer;
        _messageFooterText.Visibility = string.IsNullOrWhiteSpace(footer) ? Visibility.Collapsed : Visibility.Visible;

        ConfigureMessageButton(_messagePrimaryButton, primaryLabel, primaryAction);
        ConfigureMessageButton(_messageSecondaryButton, secondaryLabel, secondaryAction);
        ConfigureMessageButton(_messageTertiaryButton, tertiaryLabel, tertiaryAction);
        bool hasActions = _messagePrimaryButton.Visibility == Visibility.Visible
            || _messageSecondaryButton.Visibility == Visibility.Visible
            || _messageTertiaryButton.Visibility == Visibility.Visible;
        _messageActionsPanel.Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        _messageSuppressButton.Content = suppressLabel;
        _messageSuppressButton.Visibility = string.IsNullOrWhiteSpace(suppressLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _messageCard.Visibility = Visibility.Visible;
    }

    private void ApplyMessageChrome(UserDesktopWidgetMessageSeverity severity)
    {
        (Color background, Color border, Color title, Color body, Color footer) = severity switch
        {
            UserDesktopWidgetMessageSeverity.Info => (
                Color.FromRgb(19, 32, 44),
                Color.FromRgb(48, 75, 101),
                Color.FromRgb(227, 239, 250),
                Color.FromRgb(197, 216, 233),
                Color.FromRgb(146, 180, 210)),
            UserDesktopWidgetMessageSeverity.Warning => (
                Color.FromRgb(39, 31, 20),
                Color.FromRgb(97, 72, 33),
                Color.FromRgb(248, 234, 206),
                Color.FromRgb(227, 206, 164),
                Color.FromRgb(201, 172, 117)),
            UserDesktopWidgetMessageSeverity.Critical => (
                Color.FromRgb(44, 23, 23),
                Color.FromRgb(119, 58, 58),
                Color.FromRgb(250, 232, 232),
                Color.FromRgb(234, 202, 202),
                Color.FromRgb(220, 164, 164)),
            _ => (
                Color.FromRgb(20, 25, 31),
                Color.FromRgb(40, 49, 61),
                Color.FromRgb(234, 238, 243),
                Color.FromRgb(196, 205, 215),
                Color.FromRgb(153, 167, 183))
        };

        _messageCard.Background = new SolidColorBrush(background);
        _messageCard.BorderBrush = new SolidColorBrush(border);
        _messageTitleText.Foreground = new SolidColorBrush(title);
        _messageBodyText.Foreground = new SolidColorBrush(body);
        _messageFooterText.Foreground = new SolidColorBrush(footer);
    }

    private void ConfigureMessageButton(
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

    private static void ConfigurePromptButton(Button button, string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            button.Visibility = Visibility.Collapsed;
            button.Tag = null;
            button.Content = string.Empty;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Tag = null;
        button.Content = label.Trim();
    }

    private void OnPrimaryMessageAction(object? sender, RoutedEventArgs e)
    {
        if (_activePrompt != null)
        {
            ResolvePrompt(UserInterventionPromptResponseKind.Accepted);
            return;
        }

        TriggerMessageAction(_messagePrimaryButton.Tag);
    }

    private void OnSecondaryMessageAction(object? sender, RoutedEventArgs e)
    {
        if (_activePrompt != null)
        {
            ResolvePrompt(UserInterventionPromptResponseKind.Dismissed);
            return;
        }

        TriggerMessageAction(_messageSecondaryButton.Tag);
    }

    private void OnTertiaryMessageAction(object? sender, RoutedEventArgs e)
    {
        if (_activePrompt != null)
        {
            ResolvePrompt(UserInterventionPromptResponseKind.Snoozed);
            return;
        }

        TriggerMessageAction(_messageTertiaryButton.Tag);
    }

    private void TriggerMessageAction(object? tag)
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
        RefreshInlineMessage();
        UpdateWindowHeight();

        _promptResponseRequested(new UserInterventionPromptResponse(
            InterventionId: prompt.InterventionId,
            TipKey: prompt.TipKey,
            Kind: kind));
    }

    private bool HasBlockingSurfaceVisible()
    {
        return false;
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

    private sealed record WidgetAttentionMessage(string Title, string Message);

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
