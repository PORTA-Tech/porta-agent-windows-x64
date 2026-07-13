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
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using FormsCursor = System.Windows.Forms.Cursor;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core;

internal sealed class UserDesktopWidgetWindow : Window
{
    private const int WindowLifecycleOpen = 0;
    private const int WindowLifecycleClosing = 1;
    private const int WindowLifecycleClosed = 2;
    private const double ExpandedWidth = 200d;
    private const double CollapsedWidth = 24d;
    private const double BaseHeight = 22d;
    // Resting height of the normal summary surface. Used before the first state
    // arrives so a freshly (re)started widget never renders as a 22px strip.
    private const double DefaultSummaryHeight = 64d;
    private const double CollapsedHeight = 24d;
    private const double TopMargin = 16d;
    private const double RightMargin = 16d;
    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan AttentionDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StateTakeoverDuration = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ReexpandPollInterval = TimeSpan.FromMilliseconds(180);
    // 900ms (was 420): the hover-dodge stays, but the old delay collapsed the widget before
    // a normal mouse travel could reach the small interior controls (+ button, menu).
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan ReexpandDelay = TimeSpan.FromMilliseconds(1200);

    private readonly Action<UserDesktopWidgetAction> _actionRequested;
    private readonly Action<UserDesktopWidgetLabelAnswer>? _labelAnswerRequested;
    private readonly Action<UserInterventionPromptResponse> _promptResponseRequested;
    private readonly UserDesktopWidgetPlacementStore _placementStore;
    private readonly DispatcherTimer _attentionTimer;
    private readonly DispatcherTimer _promptTimer;
    private readonly DispatcherTimer _stateTakeoverTimer;
    private readonly DispatcherTimer _reexpandTimer;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _menuDismissTimer;

    // ── Phase 2a: pulse line (EKG-style "alive" strip) ────────────────────────
    // Additive only: colour comes from the SAME state mapping the dot/label already
    // use; rhythm encodes the mode; frozen = zero motion ("not watching").
    private const int PulseSampleCount = 56;
    private const double PulseStripHeight = 10d;
    private static readonly TimeSpan PulseTickInterval = TimeSpan.FromMilliseconds(150);
    private readonly DispatcherTimer _pulseTimer;
    private readonly double[] _pulseSamples = new double[PulseSampleCount];
    private readonly PulseWaveformGenerator _pulseGenerator = new();
    private PulseRhythm _pulseRhythm = PulseRhythm.Regular;
    private double _pulseIntensity;          // slow, state-provided fallback only
    private double _pulseLiveIntensity;      // real-time input-driven intensity (primary)
    private uint _pulsePrevInputTick;
    private bool _pulseLiveInputBroken;      // GetLastInputInfo failed → fall back to state
    private bool _pulseFrozenRendered;
    private System.Windows.Shapes.Polyline _pulseLine = null!;
    private Canvas _pulseCanvas = null!;
    private Border _pulseStrip = null!;
    private DateTimeOffset _lastHeightHealUtc = DateTimeOffset.MinValue; // shrink-bug watchdog damping
    private bool _menuDismissButtonWasDown;
    private string _lastSizeLogSignature = string.Empty;
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
    private UserDesktopWidgetLabelPrompt? _labelPrompt;
    private string? _resolvedLabelPromptId;
    private UserDesktopWidgetSignupNudge? _signupNudge;
    private string? _resolvedSignupNudgeId;

    // ── Phase 2c: reminders ────────────────────────────────────────────────
    private readonly Action<UserDesktopWidgetReminderCommand>? _reminderCommandRequested;
    private UserDesktopWidgetReminderInfo? _nextReminder;
    private long _resolvedReminderId = -1;      // fired reminder already acted on locally
    private long _chimedReminderId = -1;        // fired reminder currently in the ring cycle
    private int _reminderRingCount;             // rings played for it (max 5)
    private DateTimeOffset _lastReminderRingUtc = DateTimeOffset.MinValue;
    private Border _reminderPillRow = null!;
    private TextBlock _reminderPillText = null!;
    private StackPanel _quickAddPanel = null!;
    private TextBox _quickAddBox = null!;
    private TextBlock _quickAddPreview = null!;
    private Button _quickAddSetButton = null!;
    // Workstream 2 — the time picker. All time logic lives in the pure compose state;
    // the window only renders snapshots and forwards taps.
    private readonly Porta.Agent.Common.ReminderComposeState _composeState = new();
    private readonly List<Button> _quickAddChips = new();
    private Button _quickAddClockToggle = null!;
    private TextBox _quickAddClockBox = null!;
    private bool _quickAddClockMode;
    // Workstream 4 — the "Today" timed-task section.
    private readonly Action<UserDesktopWidgetTaskCommand>? _taskCommandRequested;
    private IReadOnlyList<UserDesktopWidgetTaskInfo> _tasks = Array.Empty<UserDesktopWidgetTaskInfo>();
    private StackPanel _tasksSection = null!;
    private StackPanel _tasksRows = null!;
    private StackPanel _taskAddPanel = null!;
    private TextBox _taskTitleBox = null!;
    private TextBlock _taskAddPreview = null!;
    private readonly List<Button> _taskChips = new();
    private int _taskDurationMinutes = 30;
    private bool _taskAddOpen;
    private bool _quickAddOpen;

    public UserDesktopWidgetWindow(
        Action<UserDesktopWidgetAction> actionRequested,
        Action<UserInterventionPromptResponse> promptResponseRequested,
        UserDesktopWidgetPlacementStore placementStore,
        Action<UserDesktopWidgetLabelAnswer>? labelAnswerRequested = null,
        Action<UserDesktopWidgetReminderCommand>? reminderCommandRequested = null,
        Action<UserDesktopWidgetTaskCommand>? taskCommandRequested = null)
    {
        _actionRequested = actionRequested;
        _promptResponseRequested = promptResponseRequested;
        _placementStore = placementStore;
        _labelAnswerRequested = labelAnswerRequested;
        _reminderCommandRequested = reminderCommandRequested;
        _taskCommandRequested = taskCommandRequested;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = ExpandedWidth;
        // BaseHeight here keeps construction-time expectations (and tests) stable;
        // Loaded fires UpdateWindowHeight immediately, which holds DefaultSummaryHeight
        // until the first state arrives — so users never see a 22px strip.
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
            if (_menuOpen || !_collapsed || _dragInProgress)
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

        // Closes the menu on a click anywhere outside it. The widget is a
        // WS_EX_NOACTIVATE window, so WPF's StaysOpen=false light-dismiss never sees
        // clicks that land in other applications. This poll runs ONLY while the menu
        // is open (started in Opened, stopped in Closed) — zero cost otherwise.
        _menuDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _menuDismissTimer.Tick += (_, _) => DismissMenuOnOutsideClick();

        // Phase 2a — pulse line render tick. Cheap by design: all work is skipped while the
        // widget is collapsed, hidden, or frozen; otherwise it is one ~56-point Polyline
        // update at ~6.7 fps.
        _pulseTimer = new DispatcherTimer { Interval = PulseTickInterval };
        _pulseTimer.Tick += (_, _) => PulseTimerTick();
        _pulseTimer.Start();
        UserAgentFileLog.Info("pulse_strip_active build=2a-v3-live-input");

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
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stat1Stack = new StackPanel(); stat1Stack.Children.Add(_stat1Label); stat1Stack.Children.Add(_stat1Value);
        var stat2Stack = new StackPanel(); stat2Stack.Children.Add(_stat2Label); stat2Stack.Children.Add(_stat2Value);
        Grid.SetColumn(stat1Stack, 0); Grid.SetColumn(stat2Stack, 1);
        statsGrid.Children.Add(stat1Stack); statsGrid.Children.Add(stat2Stack);

        // ── Phase 2c: "+" (add reminder) — third column of the stats row ──
        var addReminderButton = new Button
        {
            // TextBlock content + centered alignments + a 1px optical lift: a raw "+" string
            // sits on the text baseline and reads visibly low inside the 16px circle.
            Content = new TextBlock
            {
                Text = "+",
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0)
            },
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 16, Height = 16,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            ToolTip = "Add reminder",
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(8),
                hoverBackground: Color.FromRgb(34, 42, 51),
                hoverBorder: Color.FromRgb(73, 88, 105),
                pressedBackground: Color.FromRgb(24, 31, 39),
                pressedBorder: Color.FromRgb(84, 101, 120))
        };
        addReminderButton.Click += (_, _) =>
        {
            _collapseTimer.Stop();
            _suspendAutoCollapseUntilPointerLeaves = true;
            ToggleReminderQuickAdd();
        };
        // Reaching for the + means the user wants to interact — stop the hover-dodge the
        // moment the pointer touches it (HandlePointerLeave re-arms the dodge on exit).
        addReminderButton.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            _suspendAutoCollapseUntilPointerLeaves = true;
        };
        Grid.SetColumn(addReminderButton, 2);
        statsGrid.Children.Add(addReminderButton);

        // ── Phase 2c: reminder pill — next upcoming reminder, countdown at T-60 ──
        _reminderPillText = new TextBlock
        {
            FontSize = 8.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        _reminderPillRow = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3E, 0x22)),
            BorderThickness = new Thickness(1),
            // Small radius, NOT a fully-rounded oval — ovals are explicitly banned from this
            // widget's design language.
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed,
            Child = _reminderPillText
        };

        // ── Phase 2c: quick-add — one line, natural time, live preview ──
        _quickAddBox = new TextBox
        {
            FontSize = 10,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xF3)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            Padding = new Thickness(4, 2, 4, 2)
        };
        _quickAddBox.TextChanged += (_, _) =>
        {
            _composeState.SetText(_quickAddBox.Text, DateTimeOffset.Now);
            RefreshQuickAddPreview();
        };
        _quickAddBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ConfirmReminderQuickAdd();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseReminderQuickAdd();
                e.Handled = true;
            }
        };
        // Clicking anywhere OUTSIDE the widget closes the quick-add and restores NOACTIVATE.
        // Deliberately deferred + re-checked: focus churns transiently while the menu popup
        // tears down right after "Add reminder", and closing on that churn made the panel
        // vanish the instant it opened.
        _quickAddBox.LostKeyboardFocus += (_, _) =>
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                if (_quickAddOpen && !IsKeyboardFocusWithin && !_quickAddBox.IsKeyboardFocused)
                {
                    CloseReminderQuickAdd();
                }
            });
        };
        _quickAddPreview = new TextBlock
        {
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin = new Thickness(1, 2, 0, 0)
        };

        // ── Workstream 2: time picker — chips, ±15m stepper, clock toggle ──
        // Typing a time still works; the picker guarantees a time can ALWAYS be chosen
        // explicitly, and confirm stays blocked until one is visible in the preview.
        var pickerRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 0)
        };
        string[] chipLabels = { "10m", "30m", "1h", "2h" };
        for (int i = 0; i < chipLabels.Length; i++)
        {
            int chipMinutes = Porta.Agent.Common.ReminderComposeState.ChipMinutes[i];
            Button chip = CreateQuickAddPickerButton(chipLabels[i]);
            chip.Click += (_, _) =>
            {
                _composeState.SelectChip(chipMinutes);
                RefreshQuickAddPreview();
            };
            _quickAddChips.Add(chip);
            pickerRow.Children.Add(chip);
        }

        Button stepDown = CreateQuickAddPickerButton("−15");
        stepDown.Margin = new Thickness(6, 0, 2, 0);
        stepDown.Click += (_, _) =>
        {
            _composeState.Step(-Porta.Agent.Common.ReminderComposeState.StepMinutesDelta, DateTimeOffset.Now);
            RefreshQuickAddPreview();
        };
        pickerRow.Children.Add(stepDown);
        Button stepUp = CreateQuickAddPickerButton("+15");
        stepUp.Click += (_, _) =>
        {
            _composeState.Step(Porta.Agent.Common.ReminderComposeState.StepMinutesDelta, DateTimeOffset.Now);
            RefreshQuickAddPreview();
        };
        pickerRow.Children.Add(stepUp);

        _quickAddClockToggle = CreateQuickAddPickerButton("🕐");
        _quickAddClockToggle.Margin = new Thickness(6, 0, 2, 0);
        _quickAddClockToggle.Click += (_, _) => ToggleQuickAddClockMode();
        pickerRow.Children.Add(_quickAddClockToggle);
        _quickAddClockBox = new TextBox
        {
            FontSize = 10,
            Width = 44,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xF3)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            Padding = new Thickness(4, 1, 4, 1),
            Visibility = Visibility.Collapsed
        };
        _quickAddClockBox.TextChanged += (_, _) =>
        {
            if (TryParseClockEntry(_quickAddClockBox.Text, out int hour, out int minute))
            {
                _composeState.SetExactTime(hour, minute);
            }

            RefreshQuickAddPreview();
        };
        _quickAddClockBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ConfirmReminderQuickAdd();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseReminderQuickAdd();
                e.Handled = true;
            }
        };
        pickerRow.Children.Add(_quickAddClockBox);

        // Explicit Set / Cancel buttons — users must never need to guess that Enter applies.
        var quickAddSet = new Button
        {
            Content = "Set",
            FontSize = 9,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 3, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x2B, 0x22)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x4D, 0x3B)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(4),
                hoverBackground: Color.FromRgb(0x1A, 0x3A, 0x2E),
                hoverBorder: Color.FromRgb(0x2A, 0x66, 0x4F),
                pressedBackground: Color.FromRgb(0x10, 0x24, 0x1D),
                pressedBorder: Color.FromRgb(0x1E, 0x4D, 0x3B))
        };
        quickAddSet.Click += (_, _) => ConfirmReminderQuickAdd();
        _quickAddSetButton = quickAddSet;
        var quickAddCancel = new Button
        {
            Content = "Cancel",
            FontSize = 9,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 3, 0, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(4),
                hoverBackground: Color.FromRgb(34, 42, 51),
                hoverBorder: Color.FromRgb(73, 88, 105),
                pressedBackground: Color.FromRgb(24, 31, 39),
                pressedBorder: Color.FromRgb(84, 101, 120))
        };
        quickAddCancel.Click += (_, _) => CloseReminderQuickAdd();
        var quickAddButtons = new StackPanel { Orientation = Orientation.Horizontal };
        quickAddButtons.Children.Add(quickAddSet);
        quickAddButtons.Children.Add(quickAddCancel);

        _quickAddPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _quickAddPanel.Children.Add(_quickAddBox);
        _quickAddPanel.Children.Add(pickerRow);
        _quickAddPanel.Children.Add(_quickAddPreview);
        _quickAddPanel.Children.Add(quickAddButtons);

        // ── Phase 2a: pulse strip — the EKG line, between the state row and the bar ──
        _pulseLine = new System.Windows.Shapes.Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        _pulseCanvas = new Canvas
        {
            Height = PulseStripHeight,
            ClipToBounds = true,
            Background = Brushes.Transparent
        };
        _pulseCanvas.Children.Add(_pulseLine);
        _pulseStrip = new Border
        {
            Height = PulseStripHeight,
            Margin = new Thickness(0, 1, 0, 2),
            Background = Brushes.Transparent,
            Child = _pulseCanvas
        };
        _pulseCanvas.SizeChanged += (_, _) => RenderPulseLine();

        var newMetricsStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 0, 0, 0)   // bodyGrid margin already provides top/bottom padding
        };
        // Phase 2a: the pulse line REPLACES the old 2px progress bar (design review: two
        // horizontal lines read as clutter). The bar's elements stay as fields so existing
        // code paths (ApplyProgressBarWidth etc.) still compile, but it is NOT in the layout.
        newMetricsStack.Children.Add(stateLabelRow);
        newMetricsStack.Children.Add(_pulseStrip);
        newMetricsStack.Children.Add(_reminderPillRow);
        newMetricsStack.Children.Add(statsGrid);
        newMetricsStack.Children.Add(_quickAddPanel);
        BuildTasksSection();
        newMetricsStack.Children.Add(_tasksSection);

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
        // Reaching for a card's buttons (Done / +10m / Yes / No) must never race the hover-
        // dodge: pointer over the card suspends auto-collapse until it leaves the widget.
        // Dragging and manual collapse stay available — nothing is pinned.
        _messageCard.MouseEnter += (_, _) =>
        {
            _collapseTimer.Stop();
            _suspendAutoCollapseUntilPointerLeaves = true;
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
            // Treat the opening click as "already down" so it can never insta-dismiss.
            _menuDismissButtonWasDown = true;
            _menuDismissTimer.Start();
            // Only expand when the widget is already expanded — when opened from the
            // collapsed pill the widget stays collapsed so the menu appears over the pill.
            if (!_collapsed)
            {
                Expand();
            }
        };
        _menuPopup.Closed += (_, _) =>
        {
            _menuDismissTimer.Stop();
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
        popupStack.Children.Add(CreateMenuButton("Add reminder", UserDesktopWidgetAction.ReminderOpenQuickAdd));
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
        // "until tomorrow" is the contract (Workstream 3) — the button must say it, so
        // hiding never reads as "gone forever" and the morning return never feels like a bug.
        _hideWidgetButton = CreateMenuButton("Hide until tomorrow", UserDesktopWidgetAction.HideWidget);
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
            UpdateWindowHeight();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateWindowHeight);
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
        UpdateLabelPrompt(state);
        UpdateSignupNudge(state);
        UpdateReminder(state);
        UpdateTasks(state);
        RefreshInlineMessage();
        RefreshReminderPill();
        ApplyModeChrome(state.Mode, state.ShowLimitedBadge);
        UpdateWindowHeight();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateWindowHeight);
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
        _pulseTimer.Stop();
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
        else if (state.Mode is UserDesktopWidgetMode.InMeeting)
        {
            // Phase 2b — teal: calm, engaged. Meeting time is measured, not excluded.
            stateColour     = Color.FromRgb(0x14, 0xB8, 0xA6);
            stateName       = "IN MEETING";
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

        // ── Phase 2a: pulse line — same colour as the state dot/label, rhythm from the
        // mode, intensity from the state. The render tick does the rest.
        _pulseLine.Stroke = colourBrush;
        _pulseIntensity = state.PulseIntensity;
        _pulseRhythm = ResolvePulseRhythm(state, stateName);
    }

    // Phase 2a — mode → rhythm signature. Regular for focus/deep work, arrhythmic for
    // distraction, fast+tremor for overload, near-flatline for away, frozen for
    // paused/limited/stale (a dead line means "not watching").
    private static PulseRhythm ResolvePulseRhythm(UserDesktopWidgetState state, string stateName)
    {
        if (state.PulseFrozen
            || state.Mode is UserDesktopWidgetMode.Paused or UserDesktopWidgetMode.Limited)
        {
            return PulseRhythm.Frozen;
        }

        if (state.Mode is UserDesktopWidgetMode.Overload)
        {
            return PulseRhythm.Tremor;
        }

        if (state.Mode is UserDesktopWidgetMode.Distraction)
        {
            return PulseRhythm.Irregular;
        }

        if (state.Mode is UserDesktopWidgetMode.InMeeting)
        {
            // Calm, slow, regular — engaged but hands-off. Low live intensity keeps it gentle.
            return PulseRhythm.Regular;
        }

        if (string.Equals(stateName, "AWAY", StringComparison.Ordinal))
        {
            return PulseRhythm.Flatline;
        }

        return PulseRhythm.Regular;
    }

    // Phase 2a — advance the waveform one tick and redraw. Skips ALL work when the strip
    // cannot be seen (collapsed / hidden / closing). Frozen renders one flat baseline and
    // snaps the dot to full opacity, then does nothing until the state thaws. Also honors
    // the Windows animation preference as the reduced-motion signal.
    private void PulseTimerTick()
    {
        // No state yet = nothing to show: the line stays still until Ari is actually
        // watching (first ApplyState), keeping "motion means live data" strictly true.
        if (!CanAcceptUiActions || _collapsed || !IsVisible || _currentState == null)
        {
            return;
        }

        // Shrink-bug watchdog: if the expanded window somehow ends up shorter than the
        // resting summary height (the historical oscillation bug), re-measure and recover
        // within ~a second. UpdateWindowHeight's clamp makes the re-measure safe.
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (ActualHeight > 0d
            && ActualHeight < DefaultSummaryHeight - 1d
            && (nowUtc - _lastHeightHealUtc) > TimeSpan.FromMilliseconds(900))
        {
            _lastHeightHealUtc = nowUtc;
            LogSizeEvent($"height_watchdog_heal actual={ActualHeight:F0}");
            UpdateWindowHeight();
        }

        // NOTE: deliberately NOT gated on SystemParameters.ClientAreaAnimation — on machines
        // with Windows visual effects trimmed (common on performance-tuned laptops) that flag
        // is false while every other widget animation still runs, and it silently turned the
        // pulse into a permanent flat line during testing.
        if (_pulseRhythm == PulseRhythm.Frozen)
        {
            if (!_pulseFrozenRendered)
            {
                Array.Clear(_pulseSamples, 0, _pulseSamples.Length);
                RenderPulseLine();
                _stateDot.Opacity = 1d;
                _pulseFrozenRendered = true;
            }

            return;
        }

        _pulseFrozenRendered = false;
        double sample = _pulseGenerator.Advance(PulseTickInterval.TotalSeconds, _pulseRhythm, ResolveLiveIntensity());
        Array.Copy(_pulseSamples, 1, _pulseSamples, 0, _pulseSamples.Length - 1);
        _pulseSamples[^1] = sample;
        RenderPulseLine();

        // The state dot breathes with the beat — opacity only, never layout.
        _stateDot.Opacity = 0.55d + 0.45d * Math.Clamp(sample, 0d, 1d);
    }

    // Phase 2a — REAL-TIME intensity, sampled every tick from the system's last-input time
    // (GetLastInputInfo). The analysis pipeline's smoothed signals lag by design (buckets +
    // smoothing + a 4s state refresh) which made the line feel dead; the pulse must react the
    // moment the user's hands move. Fast attack (~0.5s to full on sustained input), gentle
    // release (~2.5s back to calm). Falls back to the state-provided intensity if the API is
    // unavailable. Reads TIMING only — no keys, no content, same privacy class as the
    // existing idle tracking.
    private double ResolveLiveIntensity()
    {
        if (_pulseLiveInputBroken)
        {
            return _pulseIntensity;
        }

        try
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!NativeGetLastInputInfo(ref info))
            {
                _pulseLiveInputBroken = true;
                return _pulseIntensity;
            }

            bool inputSinceLastTick = info.dwTime != _pulsePrevInputTick;
            _pulsePrevInputTick = info.dwTime;
            double target = inputSinceLastTick ? 1d : 0d;
            double alpha = inputSinceLastTick ? 0.40d : 0.055d;
            _pulseLiveIntensity += (target - _pulseLiveIntensity) * alpha;
            return Math.Clamp(_pulseLiveIntensity, 0d, 1d);
        }
        catch (Exception)
        {
            _pulseLiveInputBroken = true;
            return _pulseIntensity;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", EntryPoint = "GetLastInputInfo")]
    private static extern bool NativeGetLastInputInfo(ref LASTINPUTINFO plii);

    // Phase 2a — draw the sample ring buffer as a polyline across the strip width.
    private void RenderPulseLine()
    {
        double width = _pulseCanvas.ActualWidth;
        if (width <= 0d)
        {
            width = Math.Max(60d, ExpandedWidth - 30d);
        }

        double midY = PulseStripHeight * 0.64d;
        double gain = PulseStripHeight * 0.50d;
        double stepX = width / (PulseSampleCount - 1);
        var points = new PointCollection(PulseSampleCount);
        for (int i = 0; i < PulseSampleCount; i++)
        {
            double y = midY - (_pulseSamples[i] * gain);
            points.Add(new Point(i * stepX, Math.Clamp(y, 0.6d, PulseStripHeight - 0.6d)));
        }

        points.Freeze();
        _pulseLine.Points = points;
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

            // Phase 2c — quick-add is window-local: never reaches the presenter.
            if (action == UserDesktopWidgetAction.ReminderOpenQuickAdd)
            {
                OpenReminderQuickAdd();
                return;
            }

            _actionRequested(action);
        };

        return button;
    }

    // Workstream 2 — small flat buttons for the quick-add time picker (chips, stepper,
    // clock toggle). Same design language as Set/Cancel, just tighter.
    private static Button CreateQuickAddPickerButton(string content)
    {
        return new Button
        {
            Content = content,
            FontSize = 9,
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 2, 0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Template = CreateFlatButtonTemplate(
                cornerRadius: new CornerRadius(4),
                hoverBackground: Color.FromRgb(34, 42, 51),
                hoverBorder: Color.FromRgb(73, 88, 105),
                pressedBackground: Color.FromRgb(24, 31, 39),
                pressedBorder: Color.FromRgb(84, 101, 120))
        };
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

        // ── Stop timers and mark menu as open BEFORE setting IsOpen = true ────────
        // The popup's Opened event fires asynchronously (on the next render pass).
        // Without this, the reexpand timer can tick in that gap, see _menuOpen=false
        // and _collapsed=true, call Expand(), flip _collapsed=false, and corrupt the
        // placement target.  Doing it here — synchronously, on the UI thread — means
        // any already-queued timer callback will see _menuOpen=true and bail out.
        _collapseTimer.Stop();
        _reexpandTimer.Stop();
        _menuOpen = true;

        if (_collapsed)
        {
            // Anchor the popup to the collapsed pill so it appears beside the small
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

    private void DismissMenuOnOutsideClick()
    {
        if (!_menuPopup.IsOpen)
        {
            _menuDismissTimer.Stop();
            return;
        }

        bool buttonDown =
            (NativeMethods.GetAsyncKeyState(NativeMethods.VkLButton) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRButton) & 0x8000) != 0;
        bool newPress = buttonDown && !_menuDismissButtonWasDown;
        _menuDismissButtonWasDown = buttonDown;
        if (!newPress)
        {
            return;
        }

        try
        {
            System.Drawing.Point physical = FormsCursor.Position;
            var cursor = new Point(physical.X, physical.Y);

            // PointToScreen returns physical device pixels — same space as Cursor.Position.
            if (_menuPopup.Child is not FrameworkElement chrome
                || !chrome.IsLoaded
                || chrome.ActualWidth <= 0d
                || chrome.ActualHeight <= 0d)
            {
                // Cannot establish popup bounds — never dismiss blind.
                return;
            }

            Point popupTopLeft = chrome.PointToScreen(new Point(0d, 0d));
            Point popupBottomRight = chrome.PointToScreen(new Point(chrome.ActualWidth, chrome.ActualHeight));
            if (new Rect(popupTopLeft, popupBottomRight).Contains(cursor))
            {
                return;
            }

            // Clicks on the widget itself keep their existing behaviors (menu button
            // toggle, pill click) — only clicks outside BOTH surfaces dismiss here.
            if (IsVisible && PresentationSource.FromVisual(this)?.CompositionTarget is not null)
            {
                Point windowTopLeft = PointToScreen(new Point(0d, 0d));
                Point windowBottomRight = PointToScreen(new Point(
                    ActualWidth > 0d ? ActualWidth : Width,
                    ActualHeight > 0d ? ActualHeight : Height));
                if (new Rect(windowTopLeft, windowBottomRight).Contains(cursor))
                {
                    return;
                }
            }

            _menuPopup.IsOpen = false;
        }
        catch
        {
            // Bounds computation failed — do nothing rather than dismiss incorrectly.
        }
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
        // Start content at opacity 0 and fade it in over the same duration as the
        // width animation.  This hides the menu-button "circle" that would otherwise
        // be visible while the window is still narrower than the layout width.
        _expandedStack.BeginAnimation(OpacityProperty, new DoubleAnimation(0d, 1d, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        RefreshInlineMessage();
        // Apply the correct horizontal position before the first animation frame so
        // the window does not stutter/jump as it grows.
        ApplyAnchorPosition();
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

    private void AnimateWindowWidth(double targetWidth, [CallerMemberName] string caller = "")
    {
        LogSizeEvent($"anim_width target={targetWidth:F0} caller={caller}");
        BeginAnimation(WidthProperty, new DoubleAnimation(targetWidth, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        RefreshSurfaceVisibilityHealth();
    }

    private void AnimateWindowHeight(double targetHeight, [CallerMemberName] string caller = "")
    {
        double current = ActualHeight > 0d ? ActualHeight : Height;
        if (Math.Abs(targetHeight - current) < 0.5d)
        {
            LogSizeEvent($"anim_height_skipped target={targetHeight:F0} caller={caller}");
            return;
        }

        LogSizeEvent($"anim_height target={targetHeight:F0} caller={caller}");
        BeginAnimation(HeightProperty, new DoubleAnimation(targetHeight, TransitionDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        RefreshSurfaceVisibilityHealth();
    }

    /// <summary>
    /// TEMP DIAGNOSTIC for the strip bug: records every size decision with full
    /// context. Disabled — on machines where the widget surface oscillates it floods the
    /// log tail every few seconds and buries the capture diagnostics we actually need to
    /// read remotely. Kept as a no-op so call sites still compile.
    /// </summary>
    private void LogSizeEvent(string detail)
    {
        _ = detail;
        return;
        try
        {
            string signature =
                $"{detail} aw={ActualWidth:F0} ah={ActualHeight:F0} w={Width:F0} h={Height:F0}"
                + $" collapsed={_collapsed} menu={_menuOpen} stateNull={_currentState == null}"
                + $" metrics={_metricsPanel.Visibility} stack={_expandedStack.Visibility}"
                + $" body={_bodyGrid.Visibility} msg={_messageCard.Visibility}";
            if (string.Equals(signature, _lastSizeLogSignature, StringComparison.Ordinal))
            {
                return;
            }

            _lastSizeLogSignature = signature;
            UserAgentFileLog.Info($"widget_size {signature}");
        }
        catch
        {
        }
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

        // Before the first state arrives the layout is empty — measuring it yields a
        // ~22px strip. Hold the default summary height instead; the first ApplyState
        // re-measures and corrects within one state push.
        if (_currentState == null)
        {
            AnimateWindowHeight(DefaultSummaryHeight);
            return;
        }

        // Measure against the logical expanded width — tall cap so message text never clips.
        _rootBorder.Measure(new Size(ExpandedWidth, 800d));
        double measured = _rootBorder.DesiredSize.Height;
        LogSizeEvent($"measure result={measured:F1}");
        double targetHeight = Math.Ceiling(Math.Max(BaseHeight, measured));

        // ── The "shrinks to a strip" bug, fixed as an INVARIANT ────────────────────────
        // For a long time a raced/stale measure (visibility flips + width/height animations
        // + timer-driven collapse/expand cycling) could return only the header row's height
        // while the full summary was visible, and the window then animated down to a ~22px
        // pill and STAYED there. Rather than keep chasing every race, enforce the invariant:
        // an EXPANDED widget with a state can never legitimately be shorter than the resting
        // summary height. Anything smaller is by definition a bad measurement — clamp it.
        // (Message cards measure TALLER than this, so they are unaffected. The pulse-tick
        // watchdog re-measures if window geometry and content ever disagree at runtime.)
        targetHeight = Math.Max(targetHeight, DefaultSummaryHeight);
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

        // A drag in progress owns Left/Top. Size animations fire SizeChanged dozens
        // of times while DragMove pumps the dispatcher — snapping back to the anchor
        // mid-drag made the widget impossible to move whenever anything was animating
        // (messages always animate). SaveCurrentPlacement re-syncs the anchor on drop.
        if (_dragInProgress)
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

        // TEMP DIAGNOSTIC for the strip bug: log every HWND-level size change in
        // PHYSICAL pixels. If a resize appears here with no matching anim_* log line,
        // something outside WPF (driver, DWM, another process, a rogue API call) is
        // resizing the window.
        if (msg == NativeMethods.WmWindowPosChanged)
        {
            try
            {
                NativeMethods.WindowPos pos = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
                if ((pos.flags & NativeMethods.SwpNoSize) == 0)
                {
                    LogSizeEvent($"hwnd_resize physical={pos.cx}x{pos.cy} flags=0x{pos.flags:X}");
                }
            }
            catch
            {
            }
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

        // Phase 2c — a FIRED reminder outranks sampling prompts: the user explicitly asked
        // for this moment. Same compact card surface as the label prompt.
        if (_nextReminder is { State: "fired" } fired && fired.Id != _resolvedReminderId)
        {
            RenderInlineMessage(
                title: string.Empty,
                body: $"⏰ {fired.Title}",
                footer: fired.SnoozeCount > 0 ? $"snoozed ×{fired.SnoozeCount}" : string.Empty,
                severity: UserDesktopWidgetMessageSeverity.Warning,
                primaryAction: UserDesktopWidgetAction.ReminderDone,
                primaryLabel: "Done",
                secondaryAction: UserDesktopWidgetAction.ReminderSnooze,
                secondaryLabel: "+10m");
            return;
        }

        if (_labelPrompt != null
            && !string.Equals(_labelPrompt.PromptId, _resolvedLabelPromptId, StringComparison.Ordinal))
        {
            // #5 size: no redundant "Ari" title line — the one-tap question stands alone above the
            // buttons, so the prompt collapses to the compact, notification-sized card the user wanted.
            RenderInlineMessage(
                title: string.Empty,
                body: _labelPrompt.Question,
                footer: string.Empty,
                severity: UserDesktopWidgetMessageSeverity.Info,
                primaryAction: UserDesktopWidgetAction.LabelAnswerPositive,
                primaryLabel: _labelPrompt.PositiveLabel,
                secondaryAction: UserDesktopWidgetAction.LabelAnswerNegative,
                secondaryLabel: _labelPrompt.NegativeLabel,
                tertiaryAction: UserDesktopWidgetAction.LabelDismiss,
                tertiaryLabel: "Not now");
            return;
        }

        if (_signupNudge != null
            && !string.Equals(_signupNudge.NudgeId, _resolvedSignupNudgeId, StringComparison.Ordinal))
        {
            RenderInlineMessage(
                title: string.Empty,
                body: _signupNudge.Body,
                footer: string.Empty,
                severity: UserDesktopWidgetMessageSeverity.Info,
                primaryAction: UserDesktopWidgetAction.SignupNudgeAccept,
                primaryLabel: _signupNudge.PrimaryLabel,
                secondaryAction: UserDesktopWidgetAction.SignupNudgeDismiss,
                secondaryLabel: _signupNudge.SecondaryLabel);
            return;
        }

        RenderSummarySurface();
    }

    private void UpdateLabelPrompt(UserDesktopWidgetState state)
    {
        _labelPrompt = state.LabelPrompt;
        if (_labelPrompt != null
            && !string.Equals(_labelPrompt.PromptId, _resolvedLabelPromptId, StringComparison.Ordinal))
        {
            Expand();
        }
    }

    private void UpdateSignupNudge(UserDesktopWidgetState state)
    {
        _signupNudge = state.SignupNudge;
        if (_signupNudge != null
            && !string.Equals(_signupNudge.NudgeId, _resolvedSignupNudgeId, StringComparison.Ordinal))
        {
            Expand();
        }
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

        if (action is UserDesktopWidgetAction.LabelAnswerPositive
            or UserDesktopWidgetAction.LabelAnswerNegative
            or UserDesktopWidgetAction.LabelDismiss)
        {
            ResolveLabelPrompt(action);
            return;
        }

        if (action is UserDesktopWidgetAction.SignupNudgeAccept
            or UserDesktopWidgetAction.SignupNudgeDismiss)
        {
            ResolveSignupNudge(action);
            return;
        }

        if (action is UserDesktopWidgetAction.ReminderDone
            or UserDesktopWidgetAction.ReminderSnooze)
        {
            ResolveReminder(action);
            return;
        }

        _actionRequested(action);
    }

    // ── Phase 2c: reminder behaviors ────────────────────────────────────────────

    private void UpdateReminder(UserDesktopWidgetState state)
    {
        _nextReminder = state.NextReminder;
        if (_nextReminder is { State: "fired" } fired && fired.Id != _resolvedReminderId)
        {
            Expand();

            // Ring like an ALARM, not a ping: audible immediately, and re-rings every 60s
            // while unacknowledged (max 5) so a user away from the screen still catches it.
            // Uses a synthesized tone (Beep API) — Windows 11 sound schemes silently mute
            // many system event sounds, which made SystemSounds unreliable here. Master
            // volume/mute still applies, so muted machines stay respectful.
            if (fired.Id != _chimedReminderId)
            {
                _chimedReminderId = fired.Id;
                _reminderRingCount = 0;
                _lastReminderRingUtc = DateTimeOffset.MinValue;
            }

            if (_reminderRingCount < 5
                && (DateTimeOffset.UtcNow - _lastReminderRingUtc).TotalSeconds >= 60d)
            {
                _reminderRingCount++;
                _lastReminderRingUtc = DateTimeOffset.UtcNow;
                PlayReminderRing();
            }
        }
    }

    private static void PlayReminderRing()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Two quick bursts of three rising beeps — unmistakably an alarm.
                for (int burst = 0; burst < 2; burst++)
                {
                    Console.Beep(880, 160);
                    Console.Beep(988, 160);
                    Console.Beep(1175, 220);
                    Thread.Sleep(180);
                }
            }
            catch (Exception)
            {
                try
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
                catch (Exception)
                {
                    // Sound is best-effort; the visual card is the source of truth.
                }
            }
        });
    }

    private void ResolveReminder(UserDesktopWidgetAction action)
    {
        UserDesktopWidgetReminderInfo? reminder = _nextReminder;
        if (reminder == null)
        {
            return;
        }

        // Suppress re-showing this reminder locally while the ack round-trips.
        _resolvedReminderId = reminder.Id;
        _reminderCommandRequested?.Invoke(new UserDesktopWidgetReminderCommand(
            Kind: action == UserDesktopWidgetAction.ReminderDone ? "done" : "snooze",
            Id: reminder.Id,
            Title: reminder.Title,
            DueAtUtc: reminder.DueAtUtc));
        RefreshInlineMessage();
        UpdateWindowHeight();
    }

    // Pill: show the NEXT pending reminder once it is inside the T-60 window. Refreshed on
    // every state push (~4s) — minute-level countdown needs nothing faster.
    private void RefreshReminderPill()
    {
        UserDesktopWidgetReminderInfo? reminder = _nextReminder;
        bool show = reminder is { State: "pending" }
            && !_quickAddOpen
            && (reminder.DueAtUtc - DateTimeOffset.UtcNow).TotalMinutes
                <= PortaExecutionConstants.Reminders.PillWindowMinutes;
        if (!show)
        {
            if (_reminderPillRow.Visibility != Visibility.Collapsed)
            {
                _reminderPillRow.Visibility = Visibility.Collapsed;
                UpdateWindowHeight();
            }

            return;
        }

        double minutesLeft = Math.Max(0d, (reminder!.DueAtUtc - DateTimeOffset.UtcNow).TotalMinutes);
        string countdown = minutesLeft < 1d
            ? "now"
            : minutesLeft >= 60d
                ? $"in {(int)(minutesLeft / 60d)}h {(int)(minutesLeft % 60d)}m"
                : $"in {(int)Math.Ceiling(minutesLeft)}m";
        _reminderPillText.Text = $"⏰ {reminder.Title} · {countdown}";
        if (_reminderPillRow.Visibility != Visibility.Visible)
        {
            _reminderPillRow.Visibility = Visibility.Visible;
            UpdateWindowHeight();
        }
    }

    private void ToggleReminderQuickAdd()
    {
        if (_quickAddOpen)
        {
            CloseReminderQuickAdd();
        }
        else
        {
            OpenReminderQuickAdd();
        }
    }

    private void OpenReminderQuickAdd()
    {
        Expand();
        _quickAddOpen = true;
        _composeState.Reset();
        _quickAddBox.Text = string.Empty;
        _quickAddClockMode = false;
        _quickAddClockBox.Text = string.Empty;
        _quickAddClockBox.Visibility = Visibility.Collapsed;
        RefreshQuickAddPreview();
        _quickAddPanel.Visibility = Visibility.Visible;
        RefreshReminderPill();
        UpdateWindowHeight();

        // The widget is WS_EX_NOACTIVATE (it must never steal focus in normal operation), but
        // the quick-add box needs real keyboard focus. Temporarily allow activation — and
        // because the opening click did NOT activate us (that's what NOACTIVATE means), a
        // plain Activate() is refused by the foreground rules; SetForegroundWindow works here
        // since our process just received the user's click. Focus is set after the activation
        // settles (Input priority), otherwise WPF grants it before the HWND is foreground.
        SetNoActivateStyle(enabled: false);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeSetForegroundWindow(hwnd);
        }

        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (_quickAddOpen)
            {
                Keyboard.Focus(_quickAddBox);
            }
        });
    }

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool NativeSetForegroundWindow(IntPtr hWnd);

    private void CloseReminderQuickAdd()
    {
        if (!_quickAddOpen)
        {
            return; // already closed (Enter/Esc path runs before the focus-loss event)
        }

        _quickAddOpen = false;
        _quickAddPanel.Visibility = Visibility.Collapsed;
        SetNoActivateStyle(enabled: true);
        RefreshReminderPill();
        UpdateWindowHeight();
    }

    private void ConfirmReminderQuickAdd()
    {
        // Workstream 2 invariant: a reminder is only ever scheduled with a time the user
        // has explicitly seen in the preview. No recognized time and no picker selection
        // → confirm is a no-op (the preview already shows "Pick a time" in warning state).
        Porta.Agent.Common.ReminderComposeSnapshot snap = _composeState.Snapshot(DateTimeOffset.Now);
        if (!snap.CanConfirm || snap.DueAt is not { } dueAt)
        {
            RefreshQuickAddPreview();
            return;
        }

        _reminderCommandRequested?.Invoke(new UserDesktopWidgetReminderCommand(
            Kind: "create",
            Id: 0,
            Title: snap.Title,
            DueAtUtc: dueAt.ToUniversalTime()));

        // Optimistic: show the pill IMMEDIATELY. Waiting for the next host poll read as
        // "nothing happened". The real row (with its id) replaces this on the next push;
        // if creation failed the poll clears it and the cap toast explains why.
        _nextReminder = new UserDesktopWidgetReminderInfo(
            Id: -1,
            Title: snap.Title,
            DueAtUtc: dueAt.ToUniversalTime(),
            State: "pending",
            SnoozeCount: 0);
        CloseReminderQuickAdd();
        RefreshReminderPill();
    }

    private void RefreshQuickAddPreview()
    {
        Porta.Agent.Common.ReminderComposeSnapshot snap = _composeState.Snapshot(DateTimeOffset.Now);

        // The preview is the single source of truth for what will be scheduled.
        _quickAddPreview.Text = snap.CanConfirm
            ? $"{snap.Title} — {snap.PreviewLabel}"
            : $"{snap.Title} — {snap.PreviewLabel} (chips below, or type \"in 20m\")";
        _quickAddPreview.Foreground = new SolidColorBrush(snap.CanConfirm
            ? Color.FromRgb(0x94, 0xA3, 0xB8)
            : Color.FromRgb(0xE0, 0xA8, 0x3A));

        _quickAddSetButton.IsEnabled = snap.CanConfirm;
        _quickAddSetButton.Opacity = snap.CanConfirm ? 1.0 : 0.45;

        for (int i = 0; i < _quickAddChips.Count; i++)
        {
            bool selected = snap.SelectedDurationMinutes == Porta.Agent.Common.ReminderComposeState.ChipMinutes[i];
            _quickAddChips[i].Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(0x16, 0xC7, 0x84)
                : Color.FromRgb(0x94, 0xA3, 0xB8));
            _quickAddChips[i].BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(0x1E, 0x4D, 0x3B)
                : Color.FromRgb(0x25, 0x25, 0x35));
        }
    }

    private void ToggleQuickAddClockMode()
    {
        _quickAddClockMode = !_quickAddClockMode;
        _quickAddClockBox.Visibility = _quickAddClockMode ? Visibility.Visible : Visibility.Collapsed;
        if (_quickAddClockMode)
        {
            if (_quickAddClockBox.Text.Length == 0)
            {
                // Seed with the next round quarter-hour so the user edits a valid value
                // instead of typing from scratch.
                DateTimeOffset seed = DateTimeOffset.Now.AddMinutes(15);
                int rounded = (seed.Minute / 15) * 15;
                _quickAddClockBox.Text = $"{seed.Hour:00}:{rounded:00}";
            }

            Keyboard.Focus(_quickAddClockBox);
            _quickAddClockBox.SelectAll();
        }

        RefreshQuickAddPreview();
        UpdateWindowHeight();
    }

    // ── Workstream 4: the "Today" timed-task section — plan vs. reality ─────────
    // Not a to-do app: rows show the plan (title + duration) against measured reality
    // (countdown + live off-task minutes from the 4s poll). Max 5 rows, order = creation.

    private void BuildTasksSection()
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var headerText = new TextBlock
        {
            Text = "TODAY",
            FontSize = 8,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 6, 0)
        };
        Button addToggle = CreateQuickAddPickerButton("+ task");
        addToggle.Click += (_, _) => ToggleTaskAdd();
        header.Children.Add(headerText);
        header.Children.Add(addToggle);

        _tasksRows = new StackPanel { Orientation = Orientation.Vertical };

        // Add-task compose: title + the SAME duration chips/stepper language as the
        // reminder picker (Workstream 2) — durations are never typed blind here either.
        _taskTitleBox = new TextBox
        {
            FontSize = 10,
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x14)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xEF, 0xF3)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x50)),
            BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
            Padding = new Thickness(4, 2, 4, 2)
        };
        _taskTitleBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ConfirmTaskAdd();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CloseTaskAdd();
                e.Handled = true;
            }
        };

        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        string[] chipLabels = { "10m", "30m", "1h", "2h" };
        for (int i = 0; i < chipLabels.Length; i++)
        {
            int chipMinutes = Porta.Agent.Common.ReminderComposeState.ChipMinutes[i];
            Button chip = CreateQuickAddPickerButton(chipLabels[i]);
            chip.Click += (_, _) =>
            {
                _taskDurationMinutes = chipMinutes;
                RefreshTaskAddPreview();
            };
            _taskChips.Add(chip);
            chipRow.Children.Add(chip);
        }

        Button stepDown = CreateQuickAddPickerButton("−15");
        stepDown.Margin = new Thickness(6, 0, 2, 0);
        stepDown.Click += (_, _) =>
        {
            _taskDurationMinutes = Math.Max(5, _taskDurationMinutes - 15);
            RefreshTaskAddPreview();
        };
        chipRow.Children.Add(stepDown);
        Button stepUp = CreateQuickAddPickerButton("+15");
        stepUp.Click += (_, _) =>
        {
            _taskDurationMinutes += 15;
            RefreshTaskAddPreview();
        };
        chipRow.Children.Add(stepUp);

        _taskAddPreview = new TextBlock
        {
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin = new Thickness(1, 2, 0, 0)
        };
        Button addButton = CreateQuickAddPickerButton("Add");
        addButton.Margin = new Thickness(0, 3, 2, 0);
        addButton.Click += (_, _) => ConfirmTaskAdd();
        Button cancelButton = CreateQuickAddPickerButton("Cancel");
        cancelButton.Margin = new Thickness(0, 3, 0, 0);
        cancelButton.Click += (_, _) => CloseTaskAdd();
        var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal };
        buttonsRow.Children.Add(addButton);
        buttonsRow.Children.Add(cancelButton);

        _taskAddPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _taskAddPanel.Children.Add(_taskTitleBox);
        _taskAddPanel.Children.Add(chipRow);
        _taskAddPanel.Children.Add(_taskAddPreview);
        _taskAddPanel.Children.Add(buttonsRow);

        _tasksSection = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0, 4, 0, 0)
        };
        _tasksSection.Children.Add(header);
        _tasksSection.Children.Add(_tasksRows);
        _tasksSection.Children.Add(_taskAddPanel);
    }

    private void UpdateTasks(UserDesktopWidgetState state)
    {
        _tasks = state.Tasks ?? Array.Empty<UserDesktopWidgetTaskInfo>();
        RenderTaskRows();
    }

    private void RenderTaskRows()
    {
        _tasksRows.Children.Clear();
        int shown = 0;
        foreach (UserDesktopWidgetTaskInfo task in _tasks)
        {
            if (shown >= 5)
            {
                break;
            }

            _tasksRows.Children.Add(BuildTaskRow(task));
            shown++;
        }

        // Collapsed by default when empty: the header (with "+ task") only appears once
        // the user has tasks OR opened the compose — an empty section must not add noise.
        _tasksSection.Visibility = shown > 0 || _taskAddOpen || _quickAddOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateWindowHeight();
    }

    private UIElement BuildTaskRow(UserDesktopWidgetTaskInfo task)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 1, 0, 1) };
        bool closed = task.State is "done" or "abandoned";
        var title = new TextBlock
        {
            FontSize = 9,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(closed
                ? Color.FromRgb(0x64, 0x74, 0x8B)
                : Color.FromRgb(0xED, 0xEF, 0xF3)),
            Text = task.State switch
            {
                "done" => $"✓ {task.Title}",
                "abandoned" => $"✕ {task.Title}",
                _ => task.Title
            }
        };
        var detail = new TextBlock
        {
            FontSize = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 4, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Text = BuildTaskDetailText(task)
        };
        row.Children.Add(title);
        row.Children.Add(detail);

        switch (task.State)
        {
            case "pending":
            {
                Button start = CreateQuickAddPickerButton("▶");
                start.Click += (_, _) => SendTaskCommand("start", task);
                row.Children.Add(start);
                break;
            }

            case "active":
            {
                Button done = CreateQuickAddPickerButton("✓");
                done.Click += (_, _) => SendTaskCommand("done", task);
                Button extend = CreateQuickAddPickerButton("+15");
                extend.Click += (_, _) => SendTaskCommand("extend", task);
                Button abandon = CreateQuickAddPickerButton("✕");
                abandon.Click += (_, _) => SendTaskCommand("abandon", task);
                row.Children.Add(done);
                row.Children.Add(extend);
                row.Children.Add(abandon);
                break;
            }
        }

        return row;
    }

    // The honest line. Active: countdown + live off-task. Done: plan vs. reality,
    // no judgment adjectives.
    private static string BuildTaskDetailText(UserDesktopWidgetTaskInfo task)
    {
        int budgetSeconds = task.PlannedSeconds + task.ExtendedSeconds;
        switch (task.State)
        {
            case "pending":
                return FormatTaskDuration(task.PlannedSeconds);
            case "active":
            {
                double remaining = budgetSeconds - task.ActiveSeconds;
                string clock = remaining >= 0
                    ? $"{FormatTaskDuration((int)Math.Ceiling(remaining))} left"
                    : $"{FormatTaskDuration((int)Math.Ceiling(-remaining))} over";
                int offtaskMinutes = (int)Math.Round(task.OfftaskSeconds / 60d);
                return offtaskMinutes > 0 ? $"{clock} · {offtaskMinutes}m off-task" : clock;
            }

            case "done":
            case "abandoned":
            {
                string line = $"planned {FormatTaskDuration(task.PlannedSeconds)} · took {FormatTaskDuration((int)Math.Round(task.ActiveSeconds))}";
                int offtaskMinutes = (int)Math.Round(task.OfftaskSeconds / 60d);
                return offtaskMinutes > 0 ? $"{line} · {offtaskMinutes}m off-task" : line;
            }

            default:
                return string.Empty;
        }
    }

    private static string FormatTaskDuration(int totalSeconds)
        => Porta.Agent.Common.ReminderTimeParser.FormatDuration(Math.Max(0, totalSeconds) / 60);

    private void SendTaskCommand(string kind, UserDesktopWidgetTaskInfo task)
    {
        _taskCommandRequested?.Invoke(new UserDesktopWidgetTaskCommand(
            Kind: kind,
            Id: task.Id,
            Title: task.Title,
            PlannedSeconds: task.PlannedSeconds));
    }

    private void ToggleTaskAdd()
    {
        if (_taskAddOpen)
        {
            CloseTaskAdd();
            return;
        }

        Expand();
        _taskAddOpen = true;
        _taskTitleBox.Text = string.Empty;
        _taskDurationMinutes = 30;
        RefreshTaskAddPreview();
        _taskAddPanel.Visibility = Visibility.Visible;
        _tasksSection.Visibility = Visibility.Visible;
        UpdateWindowHeight();

        // Same NOACTIVATE dance as the reminder quick-add: the box needs real focus.
        SetNoActivateStyle(enabled: false);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _ = NativeSetForegroundWindow(hwnd);
        }

        Activate();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (_taskAddOpen)
            {
                Keyboard.Focus(_taskTitleBox);
            }
        });
    }

    private void CloseTaskAdd()
    {
        if (!_taskAddOpen)
        {
            return;
        }

        _taskAddOpen = false;
        _taskAddPanel.Visibility = Visibility.Collapsed;
        SetNoActivateStyle(enabled: true);
        RenderTaskRows();
    }

    private void ConfirmTaskAdd()
    {
        string title = _taskTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            RefreshTaskAddPreview();
            return;
        }

        _taskCommandRequested?.Invoke(new UserDesktopWidgetTaskCommand(
            Kind: "create",
            Id: 0,
            Title: title,
            PlannedSeconds: _taskDurationMinutes * 60));
        CloseTaskAdd();
    }

    private void RefreshTaskAddPreview()
    {
        string title = _taskTitleBox.Text.Trim();
        _taskAddPreview.Text = title.Length == 0
            ? $"Type a task · {Porta.Agent.Common.ReminderTimeParser.FormatDuration(_taskDurationMinutes)} planned"
            : $"{title} · {Porta.Agent.Common.ReminderTimeParser.FormatDuration(_taskDurationMinutes)} planned";
        for (int i = 0; i < _taskChips.Count; i++)
        {
            bool selected = _taskDurationMinutes == Porta.Agent.Common.ReminderComposeState.ChipMinutes[i];
            _taskChips[i].Foreground = new SolidColorBrush(selected
                ? Color.FromRgb(0x16, 0xC7, 0x84)
                : Color.FromRgb(0x94, 0xA3, 0xB8));
            _taskChips[i].BorderBrush = new SolidColorBrush(selected
                ? Color.FromRgb(0x1E, 0x4D, 0x3B)
                : Color.FromRgb(0x25, 0x25, 0x35));
        }
    }

    // Strict HH:mm (24h). The parser's looser grammars stay in the typed line; this box is
    // the explicit exact-time affordance, so ambiguity is not accepted here.
    private static bool TryParseClockEntry(string? raw, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        string text = (raw ?? string.Empty).Trim();
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        return int.TryParse(text[..colon], out hour)
            && int.TryParse(text[(colon + 1)..], out minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59;
    }

    private void SetNoActivateStyle(bool enabled)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
        style = enabled
            ? style | NativeMethods.WsExNoActivate
            : style & ~NativeMethods.WsExNoActivate;
        _ = NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, new IntPtr(style));
    }

    private void ResolveSignupNudge(UserDesktopWidgetAction action)
    {
        UserDesktopWidgetSignupNudge? nudge = _signupNudge;
        if (nudge == null)
        {
            return;
        }

        // Suppress re-showing this exact nudge while the host still holds it (until its backstop).
        _resolvedSignupNudgeId = nudge.NudgeId;
        if (action == UserDesktopWidgetAction.SignupNudgeAccept)
        {
            // Open the Control Center at the sign-in screen (where linking actually happens).
            _actionRequested(UserDesktopWidgetAction.OpenAccount);
        }

        RefreshInlineMessage();
        UpdateWindowHeight();
    }

    private void ResolveLabelPrompt(UserDesktopWidgetAction action)
    {
        UserDesktopWidgetLabelPrompt? prompt = _labelPrompt;
        if (prompt == null)
        {
            return;
        }

        // Suppress re-showing this exact prompt while the host still holds it (until its backstop).
        _resolvedLabelPromptId = prompt.PromptId;
        if (action != UserDesktopWidgetAction.LabelDismiss)
        {
            string answer = action == UserDesktopWidgetAction.LabelAnswerPositive
                ? prompt.PositiveAnswer
                : prompt.NegativeAnswer;
            _labelAnswerRequested?.Invoke(new UserDesktopWidgetLabelAnswer(
                prompt.Target, prompt.PromptKind, answer, IsDismiss: false));
        }

        RefreshInlineMessage();
        UpdateWindowHeight();
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

    private bool ShouldAutoCollapseSummary()
    {
        // Only surfaces that REQUIRE a user response may pin the widget open:
        // active prompts (timed answer buttons) and critical action-required cards.
        // Informational messages (attention toasts, state takeovers) stay collapsible
        // and movable — Expand() re-renders them via RefreshInlineMessage, so nothing
        // is lost if the user collapses mid-message.
        return CanAcceptUiActions
            && _currentState != null
            && !_collapsed
            && !_menuOpen
            && !_dragInProgress
            && !_suspendAutoCollapseUntilPointerLeaves
            && _activePrompt == null
            && _currentState?.CriticalState == null
            && !IsLabelPromptActive()
            && !IsSignupNudgeActive()
            // Phase 2c: typing a reminder pins the widget open. A FIRED reminder deliberately
            // does NOT pin — it stays until acted on anyway, and pinning froze all widget
            // movement, which reads as "the notification blocks everything".
            && !_quickAddOpen;
    }

    // Phase 2c — a fired reminder needs an answer (Done / +10m), so it pins the widget open
    // exactly like label prompts and critical cards.
    private bool IsFiredReminderActive()
        => _nextReminder is { State: "fired" } fired && fired.Id != _resolvedReminderId;

    // A one-tap label question (focus/distraction/overload) needs an answer, so — like active prompts
    // and critical cards — it must pin the widget open until the user taps or it resolves. Without this
    // the auto-collapse fires while the question is up: the pill closes as you reach for Yes/No, and the
    // collapse/re-expand cycling also prevents the window height from settling to the content.
    private bool IsLabelPromptActive()
        => _labelPrompt != null
            && !string.Equals(_labelPrompt.PromptId, _resolvedLabelPromptId, StringComparison.Ordinal);

    // The signup nudge has tap targets ("Sign in" / "Not now"), so it pins the widget open until the
    // user resolves it or the host's display window clears it — same reasoning as the label prompt.
    private bool IsSignupNudgeActive()
        => _signupNudge != null
            && !string.Equals(_signupNudge.NudgeId, _resolvedSignupNudgeId, StringComparison.Ordinal);

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

        // Z-order + visibility ONLY. SWP_NOMOVE | SWP_NOSIZE are mandatory here:
        // Left/Top/ActualWidth/ActualHeight are WPF logical units and must never be
        // passed to a physical-pixel API — doing so shrank the widget 20-33% on
        // 125-150% DPI displays every time recovery ran (wake, unlock, hiccups).
        _ = NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HwndTopMost,
            0, 0, 0, 0,
            NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow);
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
        // Message text — must be dark in light mode, light in dark mode
        _messageTitleText.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x1E, 0x29, 0x3B)
            : Color.FromRgb(0xEA, 0xEE, 0xF3));
        _messageBodyText.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x37, 0x41, 0x51)
            : Color.FromRgb(0xC4, 0xCD, 0xD7));
        _messageFooterText.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x64, 0x74, 0x8B)
            : Color.FromRgb(0x99, 0xA7, 0xB7));
        _menuButton.Foreground = new SolidColorBrush(isLight
            ? Color.FromRgb(0x1E, 0x29, 0x3B)
            : Color.FromRgb(0xEA, 0xEE, 0xF3));
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
        internal const int WmWindowPosChanged = 0x0047;
        internal const int HtTransparent = -1;
        internal const long WsExToolWindow = 0x00000080L;
        internal const long WsExNoActivate = 0x08000000L;
        internal static readonly IntPtr HwndTopMost = new(-1);
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpShowWindow = 0x0040;
        internal const int VkLButton = 0x01;
        internal const int VkRButton = 0x02;

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

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowPos
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }
    }
}
