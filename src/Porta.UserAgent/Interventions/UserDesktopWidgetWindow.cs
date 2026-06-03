namespace Porta.UserAgent.Interventions;

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    private const double ExpandedWidth = 200d;
    private const double CollapsedWidth = 24d;
    private const double BaseHeight = 22d;
    private const double CollapsedHeight = 24d;
    private const double TopMargin = 16d;
    private const double RightMargin = 16d;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan AttentionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StateTakeoverDuration = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ReexpandPollInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan ReexpandDelay = TimeSpan.FromMilliseconds(1200);

    private readonly Action<UserDesktopWidgetAction> _actionRequested;
    private readonly Action<UserInterventionPromptResponse> _promptResponseRequested;
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly DispatcherTimer _attentionTimer;
    private readonly DispatcherTimer _promptTimer;
    private readonly DispatcherTimer _stateTakeoverTimer;
    private readonly DispatcherTimer _reexpandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly Border _rootBorder;
    private readonly StackPanel _expandedStack;
    private readonly Grid _headerRow;
    private readonly Border _headerDragSurface;
    private readonly Grid _metricsPanel;
    private readonly StackPanel _summaryMetaPanel;
    private readonly Border _titleBadge;
    private readonly TextBlock _titleText;
    private readonly TextBlock _summaryStatusText;
    private readonly TextBlock _summaryFooterText;
    private readonly Border _primaryMetricChip;
    private readonly TextBlock _primaryMetricLabelText;
    private readonly TextBlock _primaryMetricText;
    private readonly Border _secondaryMetricChip;
    private readonly TextBlock _secondaryMetricLabelText;
    private readonly TextBlock _secondaryMetricText;
    private readonly Border _tertiaryMetricChip;
    private readonly TextBlock _tertiaryMetricLabelText;
    private readonly TextBlock _tertiaryMetricText;
    // State-driven display elements
    private readonly Border _stateDot;
    private readonly TextBlock _stateLabel;
    private readonly TextBlock _primaryValueDisplay;   // kept as field but not shown
    private readonly TextBlock _primaryLabelDisplay;   // kept as field but not shown
    private readonly Border _progressBarFill;
    private readonly Border _progressBarTrack;
    private double _progressFillPct;                   // 0..1, updated by ApplyStateMetrics
    private readonly TextBlock _stat1Label;
    private readonly TextBlock _stat1Value;
    private readonly TextBlock _stat2Label;
    private readonly TextBlock _stat2Value;
    private readonly TextBlock _stat3Label;
    private readonly TextBlock _stat3Value;
    private readonly Border _messageCard;
    private readonly TextBlock _messageTitleText;
    private readonly TextBlock _messageBodyText;
    private readonly TextBlock _messageFooterText;
    private readonly WrapPanel _messageActionsPanel;
    private readonly Button _messagePrimaryButton;
    private readonly Button _messageSecondaryButton;
    private readonly Button _messageTertiaryButton;
    private readonly Button _messageSuppressButton;
    private readonly Grid _bodyGrid;
    private readonly Border _collapsedPill;
    private readonly TextBlock _collapsedMenuIcon;
    private Point _collapsedDragStartPoint;
    private bool _collapsedDragStarted;
    private readonly Button _menuButton;
    private readonly Popup _menuPopup;
    private readonly Button _hideWidgetButton;
    private readonly Button _pauseFifteenButton;
    private readonly Button _pauseHourButton;
    private readonly Button _resumeButton;

    private HwndSource? _hwndSource;
    private bool _isLightTheme;
    private FileSystemWatcher? _appearanceWatcher;
    private bool _collapsed;
    private bool _menuOpen;
    private DateTimeOffset _menuClosedAt = DateTimeOffset.MinValue;
    private bool _dragInProgress;
    private bool _hiddenByUser;
    private bool _suspendAutoCollapseUntilPointerLeaves;
    private bool _placementInitialized;
    private double _expandedAnchorLeft;
    private double _expandedAnchorTop;
    private bool _allowClose;
    private int _windowLifecycleState = WindowLifecycleOpen;
    private int _surfaceVisibleForHeartbeat;
    private DateTimeOffset _reexpandNotBeforeUtc = DateTimeOffset.MinValue;
    private UserDesktopWidgetState? _currentState;
    private UserInterventionPrompt? _activePrompt;
    private WidgetAttentionMessage? _attentionMessage;
    private WidgetTakeoverMessage? _stateTakeoverMessage;
    private string? _lastStateTakeoverKey;

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

        _stateTakeoverTimer = new DispatcherTimer { Interval = StateTakeoverDuration };
        _stateTakeoverTimer.Tick += (_, _) =>
        {
            _stateTakeoverTimer.Stop();
            _stateTakeoverMessage = null;
            RefreshInlineMessage();
            UpdateWindowHeight();
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

            if (DateTimeOffset.UtcNow < _reexpandNotBeforeUtc)
            {
                return;
            }

            if (!IsPointerInsideWidget())
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
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x18)),  // #111118
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)), // #252535
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.22,
                Color = Color.FromRgb(0, 0, 0)
            }
        };
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");
        TextOptions.SetTextFormattingMode(_rootBorder, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_rootBorder, TextRenderingMode.ClearType);
        _rootBorder.PreviewMouseLeftButtonDown += BeginDragFromSurface;
        _rootBorder.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            _reexpandTimer.Stop();
            if (_collapsed)
            {
                return;
            }

            ScheduleCollapse();
        };
        _rootBorder.MouseLeave += (_, _) => HandlePointerLeave();
        // When collapsed: track mouse movement so we can distinguish a click (< 5px) from a drag (>= 5px).
        _rootBorder.PreviewMouseMove += (_, e) =>
        {
            if (!_collapsed || e.LeftButton != MouseButtonState.Pressed || _collapsedDragStarted)
            {
                return;
            }
            Point pos = e.GetPosition(this);
            double dx = pos.X - _collapsedDragStartPoint.X;
            double dy = pos.Y - _collapsedDragStartPoint.Y;
            if (dx * dx + dy * dy >= 25d)   // 5-pixel threshold
            {
                _collapsedDragStarted = true;
                BeginDragCore();
            }
        };

        Grid shellGrid = new();

        _bodyGrid = new Grid()
        {
            Margin = new Thickness(8, 2, 8, 5),   // 8px bottom gives text room; prevents last line being clipped by height animation
            MinWidth = 165
        };
        _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shellGrid.Children.Add(_bodyGrid);

        // ── Collapsed pill — shown when widget is collapsed ──────────────
        _collapsedMenuIcon = new TextBlock
        {
            Text = "☰",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0xC1, 0xD0))
        };
        _collapsedPill = new Border
        {
            Width = 24, Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            Child = _collapsedMenuIcon
        };
        shellGrid.Children.Add(_collapsedPill);
        // Pill click: open menu when mouse is released without having crossed the drag threshold.
        _collapsedPill.MouseLeftButtonUp += (_, _) =>
        {
            if (!_collapsedDragStarted)
            {
                ToggleMenu();
            }
        };

        _expandedStack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        Grid.SetColumn(_expandedStack, 0);
        _bodyGrid.Children.Add(_expandedStack);

        // Header layout: [drag+badge | metrics | menu]
        // Menu is on the RIGHT so it doesn't push the metric chips.
        // Metrics occupy the star column so all three chips always fill the available space.
        _headerRow = new Grid
        {
            Margin = new Thickness(0)
        };
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // col 0: drag + badge
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // col 1: metrics
        _headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });          // col 2: menu
        _expandedStack.Children.Add(_headerRow);

        _headerDragSurface = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed   // root border handles dragging; this caused the stray dash
        };
        _headerDragSurface.PreviewMouseLeftButtonDown += BeginDragFromHeader;
        Grid.SetColumn(_headerDragSurface, 0);
        _headerRow.Children.Add(_headerDragSurface);

        Grid dragContent = new();
        dragContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dragContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _headerDragSurface.Child = dragContent;

        Border dragHandle = new()
        {
            Width = 7,
            Height = 2,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(77, 88, 102)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0)
        };
        Grid.SetColumn(dragHandle, 0);
        dragContent.Children.Add(dragHandle);

        _titleText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 7.0,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _titleBadge = new Border
        {
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _titleText
        };
        Grid.SetColumn(_titleBadge, 1);
        dragContent.Children.Add(_titleBadge);

        // State-driven single-primary-metric layout.
        // _metricsPanel occupies the star column of _headerRow.
        _metricsPanel = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _metricsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_metricsPanel, 1);
        _headerRow.Children.Add(_metricsPanel);

        // ── State-driven compact display ──────────────────────────────
        // Row 1: [7px dot] [STATE · duration]  — single compact line, no large value below
        _stateDot = new Border
        {
            Width = 7, Height = 7,
            CornerRadius = new CornerRadius(999),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _stateLabel = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stateLabelRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 2)
        };
        stateLabelRow.Children.Add(_stateDot);
        stateLabelRow.Children.Add(_stateLabel);

        // Large value and label exist as fields but are NOT added to the layout
        _primaryValueDisplay = new TextBlock { Visibility = Visibility.Collapsed };
        _primaryLabelDisplay = new TextBlock { Visibility = Visibility.Collapsed };

        // Row 2: 2px progress bar (fills proportional to episode duration vs 2-hour max)
        _progressBarFill = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(2),
            Width = 0,
            Height = 2
        };
        // _progressBarTrack is stored as field so SizeChanged can update fill width
        // Cast to avoid "variable used before assignment" — assigned via the field initialiser pattern
        var progressBarTrackLocal = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 0, 3),
            ClipToBounds = true,
            Child = _progressBarFill
        };
        _progressBarTrack = progressBarTrackLocal;
        progressBarTrackLocal.SizeChanged += (_, _) => ApplyProgressBarWidth();

        // Row 3: 2-stat bottom row — WORK + PRESSURE only, no SESSION
        _stat1Label = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) };
        _stat1Value = new TextBlock { FontSize = 10, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)) };
        _stat2Label = new TextBlock { FontSize = 8, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)) };
        _stat2Value = new TextBlock { FontSize = 10, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)) };
        // stat3 fields exist for compat but are not shown
        _stat3Label = new TextBlock { Visibility = Visibility.Collapsed };
        _stat3Value = new TextBlock { Visibility = Visibility.Collapsed };

        var statsGrid = new Grid();
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var stat1Stack = new StackPanel(); stat1Stack.Children.Add(_stat1Label); stat1Stack.Children.Add(_stat1Value);
        var stat2Stack = new StackPanel(); stat2Stack.Children.Add(_stat2Label); stat2Stack.Children.Add(_stat2Value);
        Grid.SetColumn(stat1Stack, 0); Grid.SetColumn(stat2Stack, 1);
        statsGrid.Children.Add(stat1Stack); statsGrid.Children.Add(stat2Stack);

        var newMetricsStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 0)   // bodyGrid margin already provides top/bottom padding
        };
        newMetricsStack.Children.Add(stateLabelRow);
        newMetricsStack.Children.Add(progressBarTrackLocal);
        newMetricsStack.Children.Add(statsGrid);

        Grid.SetColumn(newMetricsStack, 0);
        _metricsPanel.Children.Add(newMetricsStack);

        // ── Old chips kept invisible for code-behind compatibility ──
        _primaryMetricChip = CreateMetricChip(out _primaryMetricLabelText, out _primaryMetricText);
        _secondaryMetricChip = CreateMetricChip(out _secondaryMetricLabelText, out _secondaryMetricText);
        _tertiaryMetricChip = CreateMetricChip(out _tertiaryMetricLabelText, out _tertiaryMetricText);
        var hiddenChips = new StackPanel { Visibility = Visibility.Collapsed };
        hiddenChips.Children.Add(_primaryMetricChip);
        hiddenChips.Children.Add(_secondaryMetricChip);
        hiddenChips.Children.Add(_tertiaryMetricChip);
        _metricsPanel.Children.Add(hiddenChips);

        _menuButton = new Button
        {
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(28, 35, 43)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            VerticalAlignment = VerticalAlignment.Center,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(9),
                hoverBackground: Color.FromRgb(36, 45, 55),
                hoverBorder: Color.FromRgb(67, 82, 99),
                pressedBackground: Color.FromRgb(22, 29, 36),
                pressedBorder: Color.FromRgb(79, 97, 118)),
            ToolTip = "Widget menu",
            Content = new TextBlock
            {
                Text = "\u2630",
                FontSize = 9.6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        _menuButton.AddHandler(
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler((_, _) => ToggleMenu()),
            handledEventsToo: true);
        Grid.SetColumn(_menuButton, 2);
        _headerRow.Children.Add(_menuButton);

        _summaryMetaPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 2, 0, 0)
        };
        _summaryStatusText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(180, 193, 208)),
            FontSize = 8.0,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = ExpandedWidth - 12d
        };
        _summaryFooterText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(130, 146, 163)),
            FontSize = 7.2,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 0),
            MaxWidth = ExpandedWidth - 12d
        };
        _summaryMetaPanel.Children.Add(_summaryStatusText);
        _summaryMetaPanel.Children.Add(_summaryFooterText);
        _expandedStack.Children.Add(_summaryMetaPanel);

        _messageCard = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 0),
            Visibility = Visibility.Collapsed
        };
        StackPanel messageStack = new();
        _messageCard.Child = messageStack;
        _messageTitleText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 10.2,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = ExpandedWidth - 12d   // prevent overflow; body/footer already have MaxWidth
        };
        _messageBodyText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(196, 205, 215)),
            FontSize = 9.8,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = ExpandedWidth - 16d
        };
        _messageFooterText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(153, 167, 183)),
            FontSize = 9.1,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = ExpandedWidth - 16d
        };
        messageStack.Children.Add(_messageTitleText);
        messageStack.Children.Add(_messageBodyText);
        messageStack.Children.Add(_messageFooterText);

        _messageActionsPanel = new WrapPanel
        {
            Margin = new Thickness(0, 4, 0, 0)
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
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatLinkButtonTemplate(),
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
            PopupAnimation = PopupAnimation.None,
            VerticalOffset = 6
        };
        _menuPopup.Opened += (_, _) =>
        {
            _menuOpen = true;
            _collapseTimer.Stop();
            _reexpandTimer.Stop();
            // Only expand when the widget is already expanded — when opened from the
            // collapsed pill the widget stays collapsed so the menu appears over the pill.
            if (!_collapsed)
            {
                Expand();
            }
        };
        _menuPopup.Closed += (_, _) =>
        {
            _menuOpen = false;
            _menuClosedAt = DateTimeOffset.UtcNow;
            if (_collapsed)
            {
                // Menu was shown over the collapsed pill — resume the reexpand poll
                // so the widget automatically pops back out after a moment.
                _reexpandTimer.Start();
            }
            else if (IsPointerInsideWidget())
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
        StackPanel popupStack = new() { MinWidth = 180 };
        popupChrome.Child = popupStack;
        _menuPopup.Child = popupChrome;

        popupStack.Children.Add(CreateMenuSectionLabel("Control Center"));
        popupStack.Children.Add(CreateMenuButton("Today", UserDesktopWidgetAction.OpenNow));
        popupStack.Children.Add(CreateMenuButton("History", UserDesktopWidgetAction.OpenHistory));
        popupStack.Children.Add(CreateMenuButton("Actions", UserDesktopWidgetAction.OpenActions));
        popupStack.Children.Add(CreateMenuButton("Tracking", UserDesktopWidgetAction.OpenTracking));
        popupStack.Children.Add(CreateMenuButton("Settings", UserDesktopWidgetAction.OpenSettings));
        popupStack.Children.Add(CreateMenuSeparator());
        popupStack.Children.Add(CreateMenuSectionLabel("Quick actions"));
        _pauseFifteenButton = CreateMenuButton("Pause 15m", UserDesktopWidgetAction.PauseFifteenMinutes);
        _pauseHourButton = CreateMenuButton("Pause 1h", UserDesktopWidgetAction.PauseOneHour);
        _resumeButton = CreateMenuButton("Resume", UserDesktopWidgetAction.Resume);
        popupStack.Children.Add(_pauseFifteenButton);
        popupStack.Children.Add(_pauseHourButton);
        popupStack.Children.Add(_resumeButton);
        popupStack.Children.Add(CreateMenuSeparator());
        popupStack.Children.Add(CreateMenuButton("Reset position", UserDesktopWidgetAction.ResetPosition));
        _hideWidgetButton = CreateMenuButton("Hide widget", UserDesktopWidgetAction.HideWidget);
        popupStack.Children.Add(_hideWidgetButton);

        _rootBorder.Child = shellGrid;
        Content = _rootBorder;
        SourceInitialized += (_, _) =>
        {
            InitializePlacement();
            RefreshSurfaceVisibilityHealth();
        };
        Loaded += (_, _) =>
        {
            RefreshSurfaceVisibilityHealth();
            _isLightTheme = AppearancePreferenceStore.LoadIsLightTheme();
            if (_isLightTheme)
            {
                ApplyTheme(true);
            }

            TryStartAppearanceWatcher();
        };
        IsVisibleChanged += (_, _) => RefreshSurfaceVisibilityHealth();
        SizeChanged += (_, _) =>
        {
            ApplyAnchorPosition();
            RefreshSurfaceVisibilityHealth();
        };
        LocationChanged += (_, _) => RefreshSurfaceVisibilityHealth();
    }

    public void ApplyState(UserDesktopWidgetState state)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _currentState = state;
        SetMetric(_primaryMetricChip, _primaryMetricLabelText, _primaryMetricText, state.PrimaryMetric);
        SetMetric(_secondaryMetricChip, _secondaryMetricLabelText, _secondaryMetricText, state.SecondaryMetric);
        SetMetric(_tertiaryMetricChip, _tertiaryMetricLabelText, _tertiaryMetricText, state.TertiaryMetric);
        ApplyStateMetrics(state);
        _pauseFifteenButton.IsEnabled = state.CanPause;
        _pauseHourButton.IsEnabled = state.CanPause;
        _resumeButton.IsEnabled = state.CanResume;
        UpdateStateTakeover(state);
        RefreshInlineMessage();
        ApplyModeChrome(state.Mode, state.ShowLimitedBadge);
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
        // Expand first so the window is visible, then render and measure.
        // If already expanded, Expand() returns early — UpdateWindowHeight must
        // be called unconditionally so the window grows to fit the message content.
        Expand();
        RefreshInlineMessage();
        UpdateWindowHeight();
        // Deferred re-measure: runs after WPF's normal layout pass so the height
        // animation always targets the fully settled text height.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateWindowHeight);
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
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateWindowHeight);
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

    /// <summary>
    /// Initialises the Win32 HWND (fires OnSourceInitialized hooks) and marks the widget
    /// as hidden-by-user without ever calling Show(). Call this instead of Show() when the
    /// hidden flag is already set at startup, so the window is never rendered visible and
    /// there is no one-frame flicker before HideWidget() can run.
    /// </summary>
    internal void StartHidden()
    {
        _hiddenByUser = true;
        new WindowInteropHelper(this).EnsureHandle();
        RefreshSurfaceVisibilityHealth();
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
        RefreshSurfaceVisibilityHealth();
    }

    public void ShowWidgetIfHidden()
    {
        if (!_hiddenByUser || !CanAcceptUiActions)
        {
            return;
        }

        _hiddenByUser = false;
        // EnsureVisibleForRecovery handles Show, Expand (if collapsed), position,
        // height, and topmost — calling UpdateWindowHeight/ApplyAnchorPosition here
        // first would start a competing collapsed-height animation before Expand runs.
        EnsureVisibleForRecovery();
    }

    public void AllowCloseForShutdown()
    {
        _allowClose = true;
    }

    internal bool CanAcceptUiActions => Volatile.Read(ref _windowLifecycleState) == WindowLifecycleOpen;

    internal bool IsClosingOrClosed => Volatile.Read(ref _windowLifecycleState) != WindowLifecycleOpen;

    internal bool IsSurfaceVisibleForHeartbeat => Volatile.Read(ref _surfaceVisibleForHeartbeat) == 1 && CanAcceptUiActions;

    internal void EnsureVisibleForRecovery()
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        if (_hiddenByUser)
        {
            _hiddenByUser = false;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (_collapsed)
        {
            Expand();
        }

        ApplyAnchorPosition();
        UpdateWindowHeight();
        ResetAutoCollapseAfterReveal();
        NudgeTopmostWithoutActivation();
        RefreshSurfaceVisibilityHealth();
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
        RefreshSurfaceVisibilityHealth();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SetWindowLifecycleState(WindowLifecycleClosing);
        RefreshSurfaceVisibilityHealth();
        if (!_allowClose)
        {
            e.Cancel = true;
            SetWindowLifecycleState(WindowLifecycleOpen);
            RefreshSurfaceVisibilityHealth();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        SetWindowLifecycleState(WindowLifecycleClosed);
        RefreshSurfaceVisibilityHealth();
        base.OnClosed(e);
    }

    private static Border CreateMetricChip(out TextBlock labelTextBlock, out TextBlock valueTextBlock)
    {
        // Vertical layout: label on top (small), value below (large).
        // No fixed width — each chip expands to fill its Grid column (1/3 of the metrics panel).
        labelTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(130, 148, 167)),
            FontSize = 6.2,
            FontWeight = FontWeights.Medium,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        valueTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(236, 241, 246)),
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        StackPanel stack = new()
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(labelTextBlock);
        stack.Children.Add(valueTextBlock);

        return new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 1, 2, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack
        };
    }

    private void ApplyStateMetrics(UserDesktopWidgetState state)
    {
        bool overloadMode   = state.Mode is UserDesktopWidgetMode.Overload;
        bool paused         = state.Mode is UserDesktopWidgetMode.Paused;
        bool deepWork       = state.Mode is UserDesktopWidgetMode.DeepWork;
        bool distractedMode = state.Mode is UserDesktopWidgetMode.Distraction;

        string stateName;
        string episodeDuration;   // shown after state name: "FOCUSED · 5m 35s"
        Color  stateColour;
        double episodeSeconds;    // used for the progress bar (duration-based fill)

        if (paused)
        {
            stateName       = "PAUSED";
            episodeDuration = string.Empty;
            stateColour     = Color.FromRgb(0x47, 0x55, 0x69);
            episodeSeconds  = 0d;
        }
        else if (overloadMode)
        {
            string pressureVal = state.TertiaryMetric.Value;
            double pct = TryParsePercent(pressureVal);
            stateColour     = pct >= 0.80 ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0xF5, 0x9E, 0x0B);
            stateName       = pct >= 0.80 ? "OVERLOADED" : "WATCH THIS";
            // Show pressure % as the "episode duration" slot — e.g. "WATCH THIS · 65%"
            episodeDuration = !string.Equals(pressureVal, "—", StringComparison.Ordinal) ? pressureVal : string.Empty;
            episodeSeconds  = 0d;
        }
        else if (distractedMode)
        {
            // Purple — same colour used for distraction in the history calendar
            stateColour     = Color.FromRgb(0xA8, 0x55, 0xF7);
            stateName       = "DISTRACTED";
            episodeDuration = string.Empty;
            episodeSeconds  = 0d;
        }
        else
        {
            string focusVal = state.SecondaryMetric.Value;
            // BuildLastFocusMetric returns Label="Away" when the user is idle.
            // We must NOT treat an away-duration value as a focus session —
            // both are non-"—" strings, so we check the label to distinguish them.
            bool isAwayMetric = string.Equals(state.SecondaryMetric.Label, "Away", StringComparison.OrdinalIgnoreCase);
            bool hasSession = !isAwayMetric
                              && !string.Equals(focusVal, "—", StringComparison.Ordinal)
                              && !string.IsNullOrWhiteSpace(focusVal);

            if (hasSession || deepWork)
            {
                stateColour     = Color.FromRgb(0x16, 0xC7, 0x84);
                stateName       = deepWork ? "DEEP WORK" : "FOCUSED";
                episodeDuration = hasSession ? focusVal : string.Empty;
                episodeSeconds  = ParseDurationSeconds(focusVal);
            }
            else
            {
                stateColour = Color.FromRgb(0x3B, 0x82, 0xF6);
                stateName   = "AWAY";
                // Secondary metric returns idle duration when away (e.g. "2m 15s")
                string awayVal = state.SecondaryMetric.Value;
                episodeDuration = !string.Equals(awayVal, "—", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(awayVal)
                    ? awayVal : string.Empty;
                episodeSeconds = ParseDurationSeconds(awayVal);
            }
        }

        // ── Top row: [dot] STATE · duration ───────────────────────
        var colourBrush = new SolidColorBrush(stateColour);
        _stateDot.Background = colourBrush;
        _stateLabel.Foreground = colourBrush;
        _stateLabel.Text = string.IsNullOrEmpty(episodeDuration)
            ? stateName
            : $"{stateName} · {episodeDuration}";

        // ── Progress bar: fills by episode duration vs 2-hour max ─
        const double maxSeconds = 2d * 60d * 60d;   // 2 hours = 100 %
        _progressFillPct = Math.Clamp(episodeSeconds / maxSeconds, 0d, 1d);
        _progressBarFill.Background = colourBrush;
        ApplyProgressBarWidth();

        // ── Bottom stats: WORK + PRESSURE only ────────────────────
        string workVal      = state.PrimaryMetric.Value;
        string pressureDisp = state.TertiaryMetric.Value;
        _stat1Label.Text = "WORK";
        _stat1Value.Text = string.IsNullOrWhiteSpace(workVal) ? "—" : workVal;
        // WORK stays neutral always — informational, no threshold signal
        Color neutralValueColor = _isLightTheme ? Color.FromRgb(0x1E, 0x29, 0x3B) : Color.FromRgb(0xE2, 0xE8, 0xF0);
        _stat1Value.Foreground = new SolidColorBrush(neutralValueColor);

        _stat2Label.Text = "PRESSURE";
        _stat2Value.Text = string.IsNullOrWhiteSpace(pressureDisp) ? "—" : pressureDisp;
        // PRESSURE coloured by level: neutral < 65% | amber 65–80% | red > 80%
        double pressurePct = TryParsePercent(pressureDisp);
        _stat2Value.Foreground = new SolidColorBrush(
            pressurePct >= 0.80d ? Color.FromRgb(0xEF, 0x44, 0x44) :
            pressurePct >= 0.65d ? Color.FromRgb(0xF5, 0x9E, 0x0B) :
            neutralValueColor);
    }

    /// <summary>Updates progress bar fill width from stored percentage whenever the track is sized.</summary>
    private void ApplyProgressBarWidth()
    {
        double trackWidth = _progressBarTrack.ActualWidth;
        if (trackWidth <= 0d)
        {
            return;
        }

        // Minimum visible fill = 3px so the bar is never invisible when there's data
        _progressBarFill.Width = _progressFillPct > 0d
            ? Math.Max(3d, trackWidth * _progressFillPct)
            : 0d;
    }

    /// <summary>Parses a human-readable duration ("5m 35s", "2h 10m", "45m") into seconds.</summary>
    private static double ParseDurationSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0d;

        double total = 0d;
        ReadOnlySpan<char> span = text.AsSpan().Trim();

        // Scan for tokens like "2h", "10m", "35s"
        int i = 0;
        while (i < span.Length)
        {
            // Skip spaces
            while (i < span.Length && span[i] == ' ') i++;
            if (i >= span.Length) break;

            // Read digits
            int numStart = i;
            while (i < span.Length && char.IsDigit(span[i])) i++;
            if (i == numStart) { i++; continue; } // skip non-digit

            if (!int.TryParse(span.Slice(numStart, i - numStart), out int num)) continue;

            // Read unit
            if (i < span.Length)
            {
                char unit = char.ToLowerInvariant(span[i]);
                i++;
                total += unit switch
                {
                    'h' => num * 3600d,
                    'm' => num * 60d,
                    's' => num,
                    _ => 0d
                };
            }
        }

        return total;
    }

    private static double TryParsePercent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0d;
        ReadOnlySpan<char> span = value.AsSpan().Trim();
        if (span.EndsWith("%".AsSpan(), StringComparison.Ordinal))
        {
            span = span.Slice(0, span.Length - 1);
        }

        return double.TryParse(span, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out double d)
            ? d / 100d
            : 0d;
    }

    private static void SetMetric(
        Border chip,
        TextBlock labelTextBlock,
        TextBlock valueTextBlock,
        UserDesktopWidgetMetric metric)
    {
        labelTextBlock.Text = NormalizeMetricLabelForCompactStrip(metric.Label);
        valueTextBlock.Text = string.IsNullOrWhiteSpace(metric.Value) ? "Updating..." : metric.Value.Trim();
        chip.Visibility = Visibility.Visible;
    }

    private static string NormalizeMetricLabelForCompactStrip(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        return label.Trim() switch
        {
            "Work today" => "Work",
            "Last focus session" => "Focus",
            "Pressure" => "Pressure",
            "Overload" => "Pressure",
            _ => label.Trim()
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
            return "Tracking limited";
        }

        return trimmed;
    }

    private static Button CreatePromptButton()
    {
        return new Button
        {
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Background = new SolidColorBrush(Color.FromRgb(29, 38, 47)),
            Foreground = new SolidColorBrush(Color.FromRgb(234, 238, 243)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(53, 67, 82)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(10),
                hoverBackground: Color.FromRgb(37, 47, 59),
                hoverBorder: Color.FromRgb(73, 92, 112),
                pressedBackground: Color.FromRgb(23, 31, 40),
                pressedBorder: Color.FromRgb(86, 108, 132)),
            MinWidth = 60,
            FontSize = 10.2
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
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(10),
                hoverBackground: Color.FromRgb(34, 42, 51),
                hoverBorder: Color.FromRgb(73, 88, 105),
                pressedBackground: Color.FromRgb(24, 31, 39),
                pressedBorder: Color.FromRgb(84, 101, 120))
        };
        button.Click += (_, _) =>
        {
            _menuPopup.IsOpen = false;
            // Stop the collapse timer so the widget does not shrink the instant
            // the user clicks to open the Control Center (or any other action).
            // Suspend until the pointer actually leaves — HandlePointerLeave resets this.
            _collapseTimer.Stop();
            _suspendAutoCollapseUntilPointerLeaves = true;
            _actionRequested(action);
        };

        return button;
    }

    private static ControlTemplate CreateFlatButtonTemplate(
        CornerRadius cornerRadius,
        Color hoverBackground,
        Color hoverBorder,
        Color pressedBackground,
        Color pressedBorder)
    {
        FrameworkElementFactory border = new(typeof(Border));
        border.SetValue(FrameworkElement.NameProperty, "Chrome");
        border.SetValue(Border.CornerRadiusProperty, cornerRadius);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        border.AppendChild(presenter);

        ControlTemplate template = new(typeof(Button))
        {
            VisualTree = border
        };

        Trigger hoverTrigger = new()
        {
            Property = IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(hoverBackground)));
        hoverTrigger.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(hoverBorder)));
        template.Triggers.Add(hoverTrigger);

        Trigger pressedTrigger = new()
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true
        };
        pressedTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(pressedBackground)));
        pressedTrigger.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(pressedBorder)));
        template.Triggers.Add(pressedTrigger);

        Trigger disabledTrigger = new()
        {
            Property = IsEnabledProperty,
            Value = false
        };
        disabledTrigger.Setters.Add(new Setter(OpacityProperty, 0.56d));
        template.Triggers.Add(disabledTrigger);

        return template;
    }

    private static ControlTemplate CreateFlatLinkButtonTemplate()
    {
        FrameworkElementFactory presenter = new(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        presenter.SetValue(TextElement.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));

        ControlTemplate template = new(typeof(Button))
        {
            VisualTree = presenter
        };

        Trigger hoverTrigger = new()
        {
            Property = IsMouseOverProperty,
            Value = true
        };
        hoverTrigger.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(214, 224, 236))));
        template.Triggers.Add(hoverTrigger);

        Trigger disabledTrigger = new()
        {
            Property = IsEnabledProperty,
            Value = false
        };
        disabledTrigger.Setters.Add(new Setter(OpacityProperty, 0.56d));
        template.Triggers.Add(disabledTrigger);

        return template;
    }

    private void ToggleMenu()
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        if (_menuPopup.IsOpen)
        {
            _menuPopup.IsOpen = false;
            return;
        }

        // Guard against the re-open race: WPF fires Closed (sets _menuClosedAt) before
        // the button Click fires, so clicking the menu button while the popup is open
        // would immediately reopen it without this check.
        if ((DateTimeOffset.UtcNow - _menuClosedAt).TotalMilliseconds < 300)
        {
            return;
        }

        if (_collapsed)
        {
            // Anchor the popup to the collapsed pill so it appears next to the small
            // circle — do NOT expand the widget, which would cause an ugly jump.
            _menuPopup.PlacementTarget = _collapsedPill;
            _menuPopup.Placement = PlacementMode.Left;
        }
        else
        {
            _menuPopup.PlacementTarget = _menuButton;
            _menuPopup.Placement = PlacementMode.Bottom;
            Expand();
        }

        _menuPopup.IsOpen = true;
    }

    private void Collapse()
    {
        if (!ShouldAutoCollapseSummary())
        {
            return;
        }

        _collapsed = true;
        _collapseTimer.Stop();
        _reexpandNotBeforeUtc = DateTimeOffset.UtcNow.Add(ReexpandDelay);
        _headerDragSurface.Visibility = Visibility.Collapsed;
        _titleBadge.Visibility = Visibility.Collapsed;
        _metricsPanel.Visibility = Visibility.Collapsed;
        _summaryMetaPanel.Visibility = Visibility.Collapsed;
        _expandedStack.Visibility = Visibility.Collapsed;
        _bodyGrid.Visibility = Visibility.Collapsed;
        _collapsedPill.Visibility = Visibility.Visible;
        AnimateWindowWidth(CollapsedWidth);
        UpdateWindowHeight();
        ApplyAnchorPosition();
        _reexpandTimer.Start();
    }

    private void Expand()
    {
        if (!_collapsed || !CanAcceptUiActions)
        {
            return;
        }

        _collapsed = false;
        // _headerDragSurface stays Collapsed — drag handle was the stray dash; root border handles dragging
        _collapsedPill.Visibility = Visibility.Collapsed;
        _bodyGrid.Visibility = Visibility.Visible;
        _expandedStack.Visibility = Visibility.Visible;
        _expandedStack.IsHitTestVisible = true;
        _expandedStack.BeginAnimation(OpacityProperty, null);
        _expandedStack.Opacity = 1d;
        RefreshInlineMessage();
        AnimateWindowWidth(ExpandedWidth);
        UpdateWindowHeight();
        ApplyAnchorPosition();
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
        RefreshSurfaceVisibilityHealth();
    }

    private void AnimateWindowHeight(double targetHeight)
    {
        double current = ActualHeight > 0d ? ActualHeight : Height;
        if (Math.Abs(targetHeight - current) < 0.5d)
        {
            return;
        }

        BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        RefreshSurfaceVisibilityHealth();
    }

    private void BeginDragFromHeader(object sender, MouseButtonEventArgs e)
    {
        if (_collapsed || _menuOpen)
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

    private void BeginDragFromSurface(object sender, MouseButtonEventArgs e)
    {
        if (_menuOpen)
        {
            return;
        }

        if (_collapsed)
        {
            // Record the press point for click-vs-drag detection in PreviewMouseMove.
            // Do NOT start a drag here and do NOT mark the event handled — the
            // pill's MouseLeftButtonUp handler needs to receive it for click detection.
            _collapsedDragStartPoint = e.GetPosition(this);
            _collapsedDragStarted = false;
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
        if (ShouldAutoCollapseSummary())
        {
            _collapseTimer.Start();
        }
    }

    private void HandlePointerLeave()
    {
        _collapseTimer.Stop();
        _suspendAutoCollapseUntilPointerLeaves = false;
        if (_collapsed && !_menuOpen)
        {
            _reexpandTimer.Start();
        }
    }

    private void ResetAutoCollapseAfterReveal()
    {
        _collapseTimer.Stop();
        _reexpandTimer.Stop();
        _suspendAutoCollapseUntilPointerLeaves = IsPointerInsideWidget();
    }

    private void UpdateWindowHeight()
    {
        if (_collapsed)
        {
            AnimateWindowHeight(CollapsedHeight);
            return;
        }

        // Measure against the logical expanded width — tall cap so message text never clips.
        _rootBorder.Measure(new Size(ExpandedWidth, 800d));
        double measured = _rootBorder.DesiredSize.Height;
        double targetHeight = Math.Ceiling(Math.Max(BaseHeight, measured));
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
        RefreshSurfaceVisibilityHealth();
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

        Point cursor = CursorInLogicalPixels();
        return bounds.Contains(cursor);
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
        Point cursor = CursorInLogicalPixels();
        return bounds.Contains(cursor);
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
        return false;
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

    private void ApplyModeChrome(UserDesktopWidgetMode mode, bool showLimitedBadge = false)
    {
        Color accent = mode switch
        {
            UserDesktopWidgetMode.DeepWork => Color.FromRgb(35, 82, 112),
            UserDesktopWidgetMode.Overload => Color.FromRgb(114, 77, 32),
            UserDesktopWidgetMode.Limited => Color.FromRgb(90, 74, 40),
            UserDesktopWidgetMode.Paused => Color.FromRgb(70, 76, 88),
            _ => showLimitedBadge ? Color.FromRgb(34, 32, 28) : Color.FromRgb(28, 35, 43)
        };
        Color border = mode switch
        {
            UserDesktopWidgetMode.DeepWork => Color.FromRgb(52, 101, 134),
            UserDesktopWidgetMode.Overload => Color.FromRgb(134, 97, 43),
            UserDesktopWidgetMode.Limited => Color.FromRgb(120, 97, 57),
            UserDesktopWidgetMode.Paused => Color.FromRgb(92, 101, 116),
            _ => showLimitedBadge ? Color.FromRgb(78, 68, 48) : Color.FromRgb(38, 47, 59)
        };
        Color badgeForeground = mode switch
        {
            UserDesktopWidgetMode.DeepWork => Color.FromRgb(210, 236, 251),
            UserDesktopWidgetMode.Overload => Color.FromRgb(248, 227, 190),
            UserDesktopWidgetMode.Limited => Color.FromRgb(244, 233, 190),
            UserDesktopWidgetMode.Paused => Color.FromRgb(228, 233, 240),
            _ => showLimitedBadge ? Color.FromRgb(210, 195, 160) : Color.FromRgb(154, 170, 187)
        };

        _menuButton.Background = new SolidColorBrush(accent);
        _rootBorder.BorderBrush = new SolidColorBrush(border);
        _titleBadge.Background = Brushes.Transparent;
        _titleBadge.BorderBrush = Brushes.Transparent;
        _titleText.Foreground = new SolidColorBrush(badgeForeground);
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

        if (_stateTakeoverMessage != null)
        {
            RenderInlineMessage(
                title: _stateTakeoverMessage.Title,
                body: _stateTakeoverMessage.Message,
                footer: _stateTakeoverMessage.Footer,
                severity: _stateTakeoverMessage.Severity,
                primaryAction: _stateTakeoverMessage.PrimaryAction,
                primaryLabel: _stateTakeoverMessage.PrimaryLabel,
                secondaryAction: _stateTakeoverMessage.SecondaryAction,
                secondaryLabel: _stateTakeoverMessage.SecondaryLabel,
                tertiaryAction: _stateTakeoverMessage.TertiaryAction,
                tertiaryLabel: _stateTakeoverMessage.TertiaryLabel,
                suppressLabel: _stateTakeoverMessage.SuppressLabel);
            return;
        }

        RenderSummarySurface();
    }

    private void UpdateStateTakeover(UserDesktopWidgetState state)
    {
        if (state.CriticalState == null)
        {
            _stateTakeoverTimer.Stop();
            _stateTakeoverMessage = null;
            _lastStateTakeoverKey = null;
            return;
        }

        string key = string.Join(
            "|",
            NormalizeCardTitle(state.CriticalState.Title, fallback: NormalizeWidgetTitle(state.Title)),
            state.CriticalState.Message,
            state.CriticalState.PrimaryLabel,
            state.CriticalState.SecondaryLabel,
            state.CriticalState.TertiaryLabel);

        if (string.Equals(_lastStateTakeoverKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _lastStateTakeoverKey = key;
        _stateTakeoverMessage = new WidgetTakeoverMessage(
            Title: NormalizeCardTitle(state.CriticalState.Title, fallback: NormalizeWidgetTitle(state.Title)),
            Message: state.CriticalState.Message,
            Footer: string.Empty,
            Severity: UserDesktopWidgetMessageSeverity.Critical,
            PrimaryAction: state.CriticalState.PrimaryAction,
            PrimaryLabel: state.CriticalState.PrimaryLabel,
            SecondaryAction: state.CriticalState.SecondaryAction,
            SecondaryLabel: state.CriticalState.SecondaryLabel,
            TertiaryAction: state.CriticalState.TertiaryAction,
            TertiaryLabel: state.CriticalState.TertiaryLabel);
        _stateTakeoverTimer.Stop();
        _stateTakeoverTimer.Start();
        Expand();
    }

    private void RenderSummarySurface()
    {
        UpdateHeaderCaption(_currentState);
        _metricsPanel.Visibility = Visibility.Visible;

        // Show status text when it carries information beyond "everything is normal".
        // The footer (confidence / quality) is intentionally hidden here to keep the
        // resting widget compact; it is available in the ControlCenter Today panel.
        string statusText = ResolveWidgetStatusText(_currentState?.StatusText, _currentState?.ShowLimitedBadge == true);
        _summaryStatusText.Text = statusText;
        _summaryStatusText.Visibility = string.IsNullOrWhiteSpace(statusText)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _summaryFooterText.Text = string.Empty;
        _summaryFooterText.Visibility = Visibility.Collapsed;
        _summaryMetaPanel.Visibility = Visibility.Collapsed; // always hidden — no status text in widget

        _messageCard.Visibility = Visibility.Collapsed;
        _messageTitleText.Text = string.Empty;
        _messageBodyText.Text = string.Empty;
        _messageFooterText.Text = string.Empty;
        _messageActionsPanel.Visibility = Visibility.Collapsed;
        _messageSuppressButton.Visibility = Visibility.Collapsed;
    }

    private static string ResolveWidgetStatusText(string? raw, bool showLimitedBadge)
    {
        // SPEC: widget shows ZERO status text — state row + bar + WORK/PRESSURE only.
        _ = raw; _ = showLimitedBadge;
        return string.Empty;

        // Dead code below retained for future reference only
        if (false && !string.IsNullOrWhiteSpace(raw)) { _ = showLimitedBadge; }

        string trimmed = raw.Trim();

        // Anything that is purely internal / routine → suppress
        if (trimmed.StartsWith("Updating", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Today is updating", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Active now", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Watching live", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Idle ", StringComparison.OrdinalIgnoreCase)
            || trimmed is "Overload is elevated." or "Deep work is active."
            || trimmed.StartsWith("Working locally", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Starting Ari", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Reloading", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Reconnecting", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        // Show only genuinely user-actionable or critical messages
        return trimmed;
    }

    private void UpdateHeaderCaption(UserDesktopWidgetState? state)
    {
        // Only show the mode badge for meaningful states (DeepWork, Overload, Paused).
        // Limited mode (action-required) gets no badge either — the critical card explains it.
        // Routine-degraded (ShowLimitedBadge) is communicated only via the amber border tint
        // so the badge area stays empty and the full metric strip is visible.
        string caption = state?.Mode switch
        {
            UserDesktopWidgetMode.DeepWork => "Focused",
            UserDesktopWidgetMode.Overload => "Overload",
            UserDesktopWidgetMode.Paused => "Paused",
            _ => string.Empty
        };

        _titleText.Text = caption;
        _titleText.Visibility = string.IsNullOrWhiteSpace(caption)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _titleBadge.Visibility = _titleText.Visibility;
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
        UpdateHeaderCaption(_currentState);

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
        _summaryMetaPanel.Visibility = Visibility.Collapsed;
        _metricsPanel.Visibility = Visibility.Collapsed;
        _messageCard.Visibility = Visibility.Visible;
    }

    private void ApplyMessageChrome(UserDesktopWidgetMessageSeverity severity)
    {
        (Color title, Color body, Color footer) = severity switch
        {
            UserDesktopWidgetMessageSeverity.Info => (
                Color.FromRgb(227, 239, 250),
                Color.FromRgb(197, 216, 233),
                Color.FromRgb(146, 180, 210)),
            UserDesktopWidgetMessageSeverity.Warning => (
                Color.FromRgb(248, 234, 206),
                Color.FromRgb(227, 206, 164),
                Color.FromRgb(201, 172, 117)),
            UserDesktopWidgetMessageSeverity.Critical => (
                Color.FromRgb(250, 232, 232),
                Color.FromRgb(234, 202, 202),
                Color.FromRgb(220, 164, 164)),
            _ => (
                Color.FromRgb(234, 238, 243),
                Color.FromRgb(196, 205, 215),
                Color.FromRgb(153, 167, 183))
        };

        _messageCard.Background = Brushes.Transparent;
        _messageCard.BorderBrush = Brushes.Transparent;
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

        // Same as CreateMenuButton: don't let the collapse timer fire immediately
        // after the user clicks a message-card action button.
        _collapseTimer.Stop();
        _suspendAutoCollapseUntilPointerLeaves = true;
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
        return _messageCard.Visibility == Visibility.Visible;
    }

    private bool ShouldAutoCollapseSummary()
    {
        return CanAcceptUiActions
            && _currentState != null
            && !_collapsed
            && !_menuOpen
            && !_dragInProgress
            && !_suspendAutoCollapseUntilPointerLeaves
            && _activePrompt == null
            && _attentionMessage == null
            && _stateTakeoverMessage == null
            && _currentState?.CriticalState == null
            && !HasBlockingSurfaceVisible();
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

    private void RefreshSurfaceVisibilityHealth()
    {
        bool visible =
            CanAcceptUiActions
            && !_hiddenByUser
            && Visibility == Visibility.Visible
            && IsVisible
            && Width > 0d
            && Height > 0d
            && _hwndSource is not null
            && !_hwndSource.IsDisposed;
        Interlocked.Exchange(ref _surfaceVisibleForHeartbeat, visible ? 1 : 0);
    }

    internal void NudgeTopmost()
    {
        if (_hwndSource is null || _hwndSource.IsDisposed)
        {
            return;
        }

        IntPtr hwnd = _hwndSource.Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Re-assert HWND_TOPMOST z-order only — SWP_NOMOVE | SWP_NOSIZE leaves
        // position and size untouched so WPF logical-unit values are never passed
        // to a physical-pixel API.
        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopMost,
            0, 0, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize);
    }


    private void NudgeTopmostWithoutActivation()
    {
        if (_hwndSource is null || _hwndSource.IsDisposed)
        {
            return;
        }

        IntPtr hwnd = _hwndSource.Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopMost,
            (int)Math.Round(Left),
            (int)Math.Round(Top),
            (int)Math.Round(ActualWidth > 0d ? ActualWidth : Width),
            (int)Math.Round(ActualHeight > 0d ? ActualHeight : Height),
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    /// <summary>
    /// Converts the cursor's physical screen-pixel position into WPF logical pixels,
    /// correcting for DPI scaling on high-DPI or mixed-DPI displays.
    /// Falls back to the raw physical coordinates when the PresentationSource is unavailable.
    /// </summary>
    private Point CursorInLogicalPixels()
    {
        System.Drawing.Point physical = FormsCursor.Position;
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            return new Point(physical.X, physical.Y);
        }

        Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
        return new Point(physical.X * fromDevice.M11, physical.Y * fromDevice.M22);
    }

    public void ApplyTheme(bool isLight)
    {
        if (!CanAcceptUiActions)
        {
            return;
        }

        _isLightTheme = isLight;

        _rootBorder.Background = new SolidColorBrush(isLight
            ? Color.FromRgb(0xF8, 0xFA, 0xFC)
            : Color.FromRgb(0x11, 0x11, 0x18));
        _rootBorder.BorderBrush = new SolidColorBrush(isLight
            ? Color.FromRgb(0xCB, 0xD5, 0xE1)
            : Color.FromRgb(0x25, 0x25, 0x35));
        _progressBarTrack.Background = new SolidColorBrush(isLight
            ? Color.FromRgb(0xE2, 0xE8, 0xF0)
            : Color.FromRgb(0x25, 0x25, 0x35));

        var labelBrush = new SolidColorBrush(isLight
            ? Color.FromRgb(0x47, 0x55, 0x69)
            : Color.FromRgb(0x94, 0xA3, 0xB8));
        var valueBrush = new SolidColorBrush(isLight
            ? Color.FromRgb(0x1E, 0x29, 0x3B)
            : Color.FromRgb(0xE2, 0xE8, 0xF0));

        _stat1Label.Foreground = labelBrush;
        _stat2Label.Foreground = labelBrush;
        _stat1Value.Foreground = valueBrush;
        _stat2Value.Foreground = valueBrush;
        _summaryStatusText.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x47, 0x55, 0x69)
            : Color.FromRgb(0xB4, 0xC1, 0xD0));
        _summaryFooterText.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x94, 0xA3, 0xB8)
            : Color.FromRgb(0x82, 0x92, 0xA3));
        _collapsedPill.Background = new SolidColorBrush(isLight
            ? Color.FromRgb(0xF8, 0xFA, 0xFC)
            : Color.FromRgb(0x11, 0x11, 0x18));
        _collapsedPill.BorderBrush = new SolidColorBrush(isLight
            ? Color.FromRgb(0xCB, 0xD5, 0xE1)
            : Color.FromRgb(0x25, 0x25, 0x35));
        _collapsedMenuIcon.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x47, 0x55, 0x69)
            : Color.FromRgb(0xB4, 0xC1, 0xD0));
    }

    private void TryStartAppearanceWatcher()
    {
        try
        {
            string dir = AppearancePreferenceStore.DirectoryPath;
            string fileName = Path.GetFileName(AppearancePreferenceStore.FilePath);
            if (!Directory.Exists(dir))
            {
                return;
            }

            _appearanceWatcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _appearanceWatcher.Changed += OnAppearanceFileChanged;
            _appearanceWatcher.Created += OnAppearanceFileChanged;
        }
        catch
        {
        }
    }

    private void OnAppearanceFileChanged(object sender, FileSystemEventArgs e)
    {
        bool isLight = AppearancePreferenceStore.LoadIsLightTheme();
        Dispatcher.BeginInvoke(() => ApplyTheme(isLight));
    }

    private sealed record WidgetAttentionMessage(string Title, string Message);

    private sealed record WidgetTakeoverMessage(
        string Title,
        string Message,
        string Footer,
        UserDesktopWidgetMessageSeverity Severity,
        UserDesktopWidgetAction? PrimaryAction = null,
        string PrimaryLabel = "",
        UserDesktopWidgetAction? SecondaryAction = null,
        string SecondaryLabel = "",
        UserDesktopWidgetAction? TertiaryAction = null,
        string TertiaryLabel = "",
        string SuppressLabel = "");

    private static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const int WmNcHitTest = 0x0084;
        internal const int HtTransparent = -1;
        internal const long WsExToolWindow = 0x00000080L;
        internal const long WsExNoActivate = 0x08000000L;
        internal static readonly IntPtr HwndTopMost = new(-1);
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpShowWindow = 0x0040;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
    }
}
