using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;
using ShapePath = System.Windows.Shapes.Path;
using Porta.Agent.Common;
using Porta.Agent.LocalApi;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

public partial class MainWindow : Window
{
    private const string AgentServiceName = "AriAgent";
    private const string NotAvailableText = "Not available";
    private const string FillingInText = "Updating";
    private const string WaitingForServiceDataText = "Waiting for service data";
    private const string CalibrationStageCold = "cold";
    private const string CalibrationStageProvisional = "provisional";
    private const string CalibrationStageCalibrated = "calibrated";
    private const string TrustTierLow = "low";
    private const string TrustTierMedium = "medium";
    private const string TrustTierHigh = "high";
    private const double UiCalibrationQualityFloor = 0.55d;
    private const double UiDecisionOverloadThreshold = 0.72d;
    private const double UiBreakFocusThresholdSeconds = 1800d;
    private static readonly TimeSpan SlowRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManualRefreshThrottle = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan AllTimeHistoryRefreshInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TopMetricAnimationDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan TopMetricAnimationFrameInterval = TimeSpan.FromMilliseconds(16);
    private const string EventCursorFileName = "control-center.events.cursor";
    private const string RecoveryAckFileName = "control-center.recovery.ack";
    private const string OnboardingAckFileName = "control-center.onboarding.ack";
    private const double WheelScrollPixelsPerLine = 22d;
    private const double StartupWindowHorizontalMargin = 36d;
    private const double StartupWindowTopMargin = 24d;
    private const double StartupWindowBottomMargin = 28d;
    private const double StartupWindowTargetWidth = 1280d;
    private const double StartupWindowTargetHeight = 820d;
    private const double ActionCategoryBarWidth = 170d;
    private const double HistoryHeatmapHeaderHeight = 8d;
    private const double HistoryHeatmapFooterHeight = 28d;
    private const double HistoryHeatmapCellWidth = 116d;
    private const double HistoryHeatmapCellHeight = 30d;
    private const double HistoryHeatmapCanvasPadding = 12d;

    private readonly LocalApiClient _apiClient = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly string _uiVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? NotAvailableText;
    private LocalApiStatusResponse? _latestStatus;
    private LocalApiCurrentSnapshotResponse? _lastSnapshot;
    private LocalApiLiveSessionStateResponse? _latestLiveSession;
    private IReadOnlyList<LocalApiEventResponse> _recentEvents = [];
    private IReadOnlyList<LocalApiEventResponse> _allRecentEvents = [];
    private LocalApiTimelineResponse? _todayTimeline;
    private LocalApiTimelineResponse? _hourTimeline;
    private LocalApiTimelineResponse? _fourHourTimeline;
    private LocalApiTimelineResponse? _allTimeTimeline;
    private LocalApiTimelineResponse? _weekTimeline;      // 7 days, 300s buckets — good episode resolution
    private LocalApiTimelineResponse? _twoWeekTimeline;   // 14 days, 300s buckets — same as week for consistent episode counts
    private LocalApiSettingsResponse? _effectiveSettings;
    private LocalApiRetentionResponse? _retention;
    private bool _suppressSettingsEvents;
    private LocalApiEventResponse? _selectedIntervention;
    private DateTimeOffset _lastSlowRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAllTimeHistoryRefreshUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _manualRefreshPending;
    private long _lastSeenEventId;
    private long _dismissedRecoveryEventId;
    private long _activeRecoveryEventId;
    private DateTimeOffset _lastManualRefreshUtc = DateTimeOffset.MinValue;
    private LocalApiAriContextResponse? _ariContext;
    private DateTimeOffset _lastAriContextRefreshUtc = DateTimeOffset.MinValue;
    private HistoryRange _historyRange = HistoryRange.Today;
    private HistoryPeriod _historyPeriod = HistoryPeriod.Day;
    private IReadOnlyList<LocalApiEventResponse> _lastInterventionEvents = Array.Empty<LocalApiEventResponse>();
    private bool _historyExplainMode;
    private ActionsView _actionsView = ActionsView.Suggestions;
    private readonly DispatcherTimer _settingsAutoSaveTimer;
    private string _settingsAutoSaveReason = "settings_auto";
    private DateTimeOffset _settingsDirtyUntilUtc = DateTimeOffset.MinValue;
    private bool _settingsSaveInFlight;
    private DateTimeOffset? _selectedHistorySnapshotTimestampUtc;
    private long? _selectedHistoryInterventionId;
    private string? _selectedHistoryCellKey;
    private readonly Dictionary<string, DispatcherTimer> _topMetricAnimationTimers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _topMetricAnimationLastValues = new(StringComparer.Ordinal);
    private UiSection _activeSection = UiSection.Overview;
    private bool _showOnboardingOverlay;
    private PortaVisualState _latestVisualState = VisualStateAdapter.Build(
        status: null,
        snapshot: null,
        liveSession: null,
        recentEvents: [],
        fallbackExplanation: string.Empty,
        nowUtc: DateTimeOffset.UtcNow);
    private HistoryFocusHeatmap? _latestHistoryHeatmap;
    private bool _isLightTheme;

    private enum UiSection
    {
        Overview,
        Timeline,
        // Phase 3.1 (2026-08-18) — the five main pages are Today, Tasks, Projects, Reports,
        // Settings. Interventions and Inputs are no longer sections of their own: they are
        // sub-sections of Settings, because what Ari may do and what Ari watches are configuration.
        Tasks,
        Projects,
        Reports,
        States,
        Account,
        Settings,
        Diagnostics
    }

    private enum HistoryRange
    {
        Last1Hour,
        Last4Hours,
        Today,
        AllTime
    }

    private enum HistoryPeriod { Day, Week, TwoWeeks }

    private enum ActionsView
    {
        Suggestions,
        Changes
    }

    private enum ActionCategory
    {
        Breaks,
        Focus,
        Notifications,
        Protection,
        Other
    }

    private enum FlowStepState
    {
        Ready,
        Warning,
        Blocked
    }

    public MainWindow()
    {
        InitializeComponent();
        ApplyStartupBounds();
        InitializeAccountSurface();
        InitializeUnifiedScrollBehavior();
        UpdateWindowChromeButtons();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        _lastSeenEventId = LoadLastSeenEventId();
        _dismissedRecoveryEventId = LoadRecoveryAckEventId();
        _showOnboardingOverlay = !LoadOnboardingAcknowledged();
        UiVersionText.Text = _uiVersion;
        InitializePauseUntilControls();
        PopulateOnboardingSurface(
            ProbeServiceStatus(),
            IsTrayRunning(),
            apiReachable: false,
            status: null,
            statusFresh: false,
            statusHttpCode: null);
        _settingsAutoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsAutoSaveTimer.Tick += async (_, _) =>
        {
            _settingsAutoSaveTimer.Stop();
            await SaveSettingsAsync(_settingsAutoSaveReason).ConfigureAwait(true);
        };

        SidebarDateText.Text = DateTime.Now.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
        _isLightTheme = AppearancePreferenceStore.LoadIsLightTheme();
        AppearanceLightModeToggle.IsChecked = _isLightTheme;
        DesktopWidgetVisibleToggle.IsChecked = DesktopWidgetPreferenceStore.LoadIsWidgetEnabled();
        BrowserTitleReadingToggle.IsChecked = Porta.Agent.Common.Privacy.PrivacySettingsStore.ReadBrowserTitlesEnabled();
        if (_isLightTheme)
        {
            ApplyTheme(true);
        }

        ShowSection(UiSection.Overview);
        UpdateHistoryControls();
        StateChanged += (_, _) => UpdateWindowChromeButtons();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => await RefreshDiagnosticsAsync().ConfigureAwait(true);
        Loaded += async (_, _) =>
        {
            // The design preview harness renders this window with sample data and no agent. Polling
            // would overwrite the sample mid-render, and the shared API client is disposed between
            // states. Same seam, and same reason, as UserCheckInCardWindow.SuppressForegroundAcquisition.
            if (PreviewMode)
            {
                return;
            }

            await RefreshDiagnosticsAsync(manual: true).ConfigureAwait(true);
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _settingsAutoSaveTimer.Stop();
            foreach (DispatcherTimer timer in _topMetricAnimationTimers.Values)
            {
                timer.Stop();
            }
            _controlPlaneAccountClient.Dispose();
            _apiClient.Dispose();
            _refreshGate.Dispose();
        };
    }

    private void ApplyStartupBounds()
    {
        Rect workArea = SystemParameters.WorkArea;
        double availableWidth = Math.Max(MinWidth, workArea.Width - (StartupWindowHorizontalMargin * 2d));
        double availableHeight = Math.Max(MinHeight, workArea.Height - StartupWindowTopMargin - StartupWindowBottomMargin);

        Width = Math.Min(StartupWindowTargetWidth, availableWidth);
        Height = Math.Min(StartupWindowTargetHeight, availableHeight);

        double centeredLeft = workArea.Left + Math.Max(0d, (workArea.Width - Width) / 2d);
        double minLeft = workArea.Left + StartupWindowHorizontalMargin;
        double maxLeft = workArea.Right - StartupWindowHorizontalMargin - Width;

        Left = Math.Max(minLeft, centeredLeft);
        if (Left > maxLeft)
        {
            Left = Math.Max(workArea.Left, maxLeft);
        }

        Top = workArea.Top + StartupWindowTopMargin;
        if (Top + Height > workArea.Bottom - StartupWindowBottomMargin)
        {
            Top = Math.Max(workArea.Top, workArea.Bottom - StartupWindowBottomMargin - Height);
        }

        WindowState = WindowState.Normal;
    }

    internal void HandleExternalActivation(ControlCenterActivationTarget target = ControlCenterActivationTarget.Default)
    {
        if (TryResolveActivationSection(target, out UiSection section))
        {
            ShowSection(section);
        }

        if (!IsWindowPlacementVisible())
        {
            ApplyStartupBounds();
        }
        else if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static bool TryResolveActivationSection(ControlCenterActivationTarget target, out UiSection section)
    {
        switch (target)
        {
            case ControlCenterActivationTarget.Now:
                section = UiSection.Overview;
                return true;
            case ControlCenterActivationTarget.History:
                section = UiSection.Timeline;
                return true;
            case ControlCenterActivationTarget.Actions:
                // Phase 3.1 — Actions and Tracking are sub-sections of Settings now. An older
                // widget build can still ask for them by name, so both resolve to Settings rather
                // than being dropped, and the sub-section is opened once the page is shown.
                section = UiSection.Settings;
                return true;
            case ControlCenterActivationTarget.Tracking:
                section = UiSection.Settings;
                return true;
            case ControlCenterActivationTarget.Settings:
                section = UiSection.Settings;
                return true;
            case ControlCenterActivationTarget.Health:
                section = UiSection.Diagnostics;
                return true;
            case ControlCenterActivationTarget.Account:
                section = UiSection.Account;
                return true;
            default:
                section = UiSection.Overview;
                return false;
        }
    }

    private bool IsWindowPlacementVisible()
    {
        double width = Math.Max(MinWidth, double.IsNaN(ActualWidth) || ActualWidth <= 0d ? Width : ActualWidth);
        double height = Math.Max(MinHeight, double.IsNaN(ActualHeight) || ActualHeight <= 0d ? Height : ActualHeight);

        if (double.IsNaN(Left) || double.IsNaN(Top) || width <= 0d || height <= 0d)
        {
            return false;
        }

        Rect windowRect = new(Left, Top, width, height);
        Rect virtualDesktop = new(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        return windowRect.IntersectsWith(virtualDesktop);
    }

    private void TitleBarRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) != null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void TitleBarMinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void TitleBarMaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void TitleBarCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void ToggleWindowState()
    {
        if (ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip))
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }

        UpdateWindowChromeButtons();
    }

    private void UpdateWindowChromeButtons()
    {
        if (TitleBarMaximizeGlyph == null)
        {
            return;
        }

        TitleBarMaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private async Task RefreshDiagnosticsAsync(bool manual = false)
    {
        if (manual)
        {
            Interlocked.Exchange(ref _manualRefreshPending, 1);
            UpdatedValue.Text = "Last refresh: manual refresh requested...";
        }

        bool gateAcquired;
        if (manual)
        {
            gateAcquired = await _refreshGate.WaitAsync(0).ConfigureAwait(true);
            if (!gateAcquired)
            {
                ControlCenterFileLog.Info("manual_refresh_queued");
                UpdatedValue.Text = "Last refresh: manual refresh queued...";
                await _refreshGate.WaitAsync().ConfigureAwait(true);
                _refreshGate.Release();
                return;
            }
        }
        else
        {
            // Prevent timer refreshes from piling up while keeping manual refresh deterministic.
            gateAcquired = await _refreshGate.WaitAsync(0).ConfigureAwait(true);
            if (!gateAcquired)
            {
                return;
            }
        }

        try
        {
            if (manual)
            {
                _lastSlowRefreshUtc = DateTimeOffset.MinValue;
                RefreshButton.IsEnabled = false;
                RefreshButton.Content = "Refreshing...";
            }
            do
            {
                Interlocked.Exchange(ref _manualRefreshPending, 0);
                await RefreshDiagnosticsCoreAsync(forceSlowRefresh: manual).ConfigureAwait(true);

                if (manual && IsSnapshotStale(_latestStatus, _lastSnapshot, DateTimeOffset.UtcNow))
                {
                    ControlCenterFileLog.Info("manual_refresh_snapshot_stale_retry");
                    await Task.Delay(TimeSpan.FromMilliseconds(900)).ConfigureAwait(true);
                    await RefreshDiagnosticsCoreAsync(forceSlowRefresh: true).ConfigureAwait(true);
                }
            }
            while (Interlocked.CompareExchange(ref _manualRefreshPending, 0, 1) == 1);
        }
        catch (Exception ex)
        {
            ControlCenterFileLog.Error("refresh_failed", ex);
        }
        finally
        {
            if (manual)
            {
                RefreshButton.Content = "Refresh";
                RefreshButton.IsEnabled = true;
            }
            _refreshGate.Release();
        }
    }

    private async Task RefreshDiagnosticsCoreAsync(bool forceSlowRefresh = false)
    {
        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        LocalApiRuntimeSettings? runtimeSettings = LocalApiRuntimeSettingsStore.Load();
        ServiceProbeResult serviceProbe = ProbeServiceStatus();
        LocalApiStatusResponse? previousStatus = _latestStatus;
        LocalApiCurrentSnapshotResponse? previousSnapshot = _lastSnapshot;
        LocalApiLiveSessionStateResponse? previousLiveSession = _latestLiveSession;

        LocalApiStatusResponse? fetchedStatus = await _apiClient.GetStatusAsync().ConfigureAwait(true);
        int? statusHttpCode = _apiClient.LastStatusHttpCode;
        bool statusFresh = fetchedStatus != null && IsStatusFresh(fetchedStatus);

        LocalApiCurrentSnapshotResponse? fetchedSnapshot = serviceProbe.Running
            ? await _apiClient.GetCurrentSnapshotAsync().ConfigureAwait(true)
            : null;
        LocalApiLiveSessionStateResponse? fetchedLiveSession = serviceProbe.Running
            ? await _apiClient.GetLiveSessionStateAsync().ConfigureAwait(true)
            : null;
        int? snapshotHttpCode = _apiClient.LastSnapshotHttpCode;
        bool snapshotFresh = fetchedSnapshot != null && IsSnapshotFresh(fetchedSnapshot);
        bool healthReachable = false;
        if (!statusHttpCode.HasValue && !snapshotHttpCode.HasValue && serviceProbe.Running)
        {
            healthReachable = await _apiClient.GetHealthAsync().ConfigureAwait(true) != null;
        }

        _latestStatus = fetchedStatus ?? previousStatus;
        _lastSnapshot = fetchedSnapshot ?? previousSnapshot;
        _latestLiveSession = fetchedLiveSession ?? previousLiveSession;

        // Refresh Ari context once every 30 minutes
        if ((DateTimeOffset.UtcNow - _lastAriContextRefreshUtc).TotalMinutes >= 30)
        {
            LocalApiAriContextResponse? freshAriContext = await _apiClient.GetAriContextAsync().ConfigureAwait(true);
            if (freshAriContext is not null)
            {
                _ariContext = freshAriContext;
                _lastAriContextRefreshUtc = DateTimeOffset.UtcNow;
            }
        }

        if (fetchedStatus == null && _latestStatus != null)
        {
            statusFresh = false;
        }

        if (fetchedSnapshot == null && _lastSnapshot != null)
        {
            snapshotFresh = false;
        }

        bool apiReachable = statusHttpCode.HasValue || snapshotHttpCode.HasValue || healthReachable;
        bool dataEndpointsResponsive = fetchedStatus != null || fetchedSnapshot != null;
        bool statusAvailable = _latestStatus != null;
        LocalApiInputsSummaryResponse? inputSummary = dataEndpointsResponsive
            ? await _apiClient.GetInputsSummaryAsync(60).ConfigureAwait(true)
            : null;
        if (dataEndpointsResponsive)
        {
            if (_allRecentEvents.Count == 0)
            {
                IReadOnlyList<LocalApiEventResponse> baselineEvents =
                    await _apiClient.GetRecentEventsAsync(50).ConfigureAwait(true);
                _allRecentEvents = baselineEvents
                    .OrderByDescending(static evt => evt.Id)
                    .ToList();
                if (baselineEvents.Count > 0)
                {
                    UpdateLastSeenEventId(Math.Max(_lastSeenEventId, baselineEvents.Max(static evt => evt.Id)));
                }
            }

            IReadOnlyList<LocalApiEventResponse> newEvents =
                await _apiClient.GetEventsSinceIdAsync(_lastSeenEventId, 200).ConfigureAwait(true);
            if (newEvents.Count > 0)
            {
                _allRecentEvents = MergeRecentEvents(_allRecentEvents, newEvents, maxEvents: 250);
                UpdateLastSeenEventId(Math.Max(_lastSeenEventId, newEvents.Max(static evt => evt.Id)));
            }
        }

        _recentEvents = _allRecentEvents.Take(5).ToArray();
        await RefreshAccountSurfaceAsync().ConfigureAwait(true);
        NowTruth nowTruth = BuildNowTruth(serviceProbe, runtimeSettings, _latestStatus, _lastSnapshot, statusFresh, snapshotFresh, statusHttpCode, apiReachable);
        _latestVisualState = VisualStateAdapter.Build(
            _latestStatus,
            nowTruth.Snapshot,
            _latestLiveSession,
            _allRecentEvents,
            nowTruth.Explanation,
            DateTimeOffset.UtcNow);

        bool needsSlowRefresh = (statusAvailable || _lastSnapshot != null)
            && (forceSlowRefresh
                || (DateTimeOffset.UtcNow - _lastSlowRefreshUtc) >= SlowRefreshInterval);
        if (needsSlowRefresh)
        {
            DateTimeOffset fromUtc = LocalTimelineRangeResolver.ResolveLocalDayStartUtc(DateTimeOffset.UtcNow);
            _todayTimeline = PreferFreshTimeline(
                _todayTimeline,
                await _apiClient.GetTimelineAsync(fromUtc, DateTimeOffset.UtcNow, 60).ConfigureAwait(true));
            _hourTimeline = PreferFreshTimeline(
                _hourTimeline,
                await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 5).ConfigureAwait(true));
            _fourHourTimeline = PreferFreshTimeline(
                _fourHourTimeline,
                await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow, 15).ConfigureAwait(true));
            var freshRetention = await _apiClient.GetDataRetentionAsync().ConfigureAwait(true);
            // Keep any previously loaded retention so history doesn't go blank if the
            // API call is temporarily slow or returns null (e.g. service warming up).
            if (freshRetention != null)
            {
                _retention = freshRetention;
            }
            if (ShouldRefreshAllTimeTimeline(forceSlowRefresh))
            {
                _allTimeTimeline = PreferFreshTimeline(
                    _allTimeTimeline,
                    await GetAllTimeTimelineAsync(DateTimeOffset.UtcNow).ConfigureAwait(true));
                _lastAllTimeHistoryRefreshUtc = DateTimeOffset.UtcNow;

                // Week/2-week timelines with finer bucket resolution so Away/Distraction episodes are detected.
                // 300s (5-min) for both week and 2-week — same resolution so episode counts are directly comparable across views.
                _weekTimeline = PreferFreshTimeline(
                    _weekTimeline,
                    await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, 300).ConfigureAwait(true));
                _twoWeekTimeline = PreferFreshTimeline(
                    _twoWeekTimeline,
                    await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddDays(-14), DateTimeOffset.UtcNow, 300).ConfigureAwait(true));
            }

            _effectiveSettings = await _apiClient.GetEffectiveSettingsAsync().ConfigureAwait(true);
            _lastSlowRefreshUtc = DateTimeOffset.UtcNow;
        }

        bool trayRunning = IsTrayRunning();
        ServiceRunningValue.Text = serviceProbe.Running
            ? "Yes"
            : serviceProbe.Exists
                ? $"No ({serviceProbe.StatusText})"
                : "No (Not Installed)";
        TrayRunningValue.Text = trayRunning ? "Yes" : "No";
        ApiReachableValue.Text = apiReachable ? "Yes" : "No";
        ApiPortValue.Text = endpoint?.Port.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
        UpdateDebugFooter(nowTruth, statusFresh);
        PopulateOnboardingSurface(serviceProbe, trayRunning, apiReachable, _latestStatus, statusFresh, statusHttpCode);

        if (_latestStatus == null)
        {
            ConsentActiveValue.Text = NotAvailableText;
            BackendConnectedValue.Text = NotAvailableText;
            FocusStrengthValue.Text = NotAvailableText;
            OverloadValue.Text = NotAvailableText;
            LastActionValue.Text = NotAvailableText;
            LastEventValue.Text = NotAvailableText;
            PolicyVersionValue.Text = NotAvailableText;
            SchemaVersionValue.Text = NotAvailableText;
            AgentVersionValue.Text = NotAvailableText;
            ConsentVersionValue.Text = NotAvailableText;
            ConsentTimestampValue.Text = NotAvailableText;
            ConsentHashValue.Text = NotAvailableText;
            LastErrorValue.Text = BuildOfflineErrorMessage(serviceProbe, endpoint, runtimeSettings, statusHttpCode, apiReachable);
            SnapshotConfidenceValue.Text = NotAvailableText;
            OverloadActiveValue.Text = NotAvailableText;
            SustainedAttentionValue.Text = NotAvailableText;
            IdleFragmentationValue.Text = NotAvailableText;
            QuickPauseResumeButton.Content = "Pause";
            QuickPauseResumeButton.IsEnabled = false;
            ConsentOverlay.Visibility = Visibility.Collapsed;
            UpdateTopBar(serviceProbe, runtimeSettings, null, nowTruth, _latestVisualState);
            UpdateCaptureHealthCards(null, _latestLiveSession, statusFresh: false);
            UpdateSessionTruthCards(null, _latestLiveSession, statusFresh: false);
            UpdateInputTruthCards(null, _latestLiveSession, statusFresh: false);
            UpdateStateTruthCards(null, null, statusFresh: false, snapshotFresh: false);
            UpdateActionTruthCards([], null, null, statusFresh: false);
            PopulateOverview(nowTruth, _latestLiveSession, null, [], previousSnapshot);
            PopulateInputs(null, null, null, null, null);
            PopulateInterventions([], null);
            PopulateSettings(null);
            PopulateStates(null, _latestLiveSession);
            PopulateTimeline(null);
            PopulateWhyDrawer(nowTruth, [], _latestVisualState);
            UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            UpdateEngineHealthCards(serviceProbe, apiReachable, snapshot: null, statusFresh: false, snapshotFresh: false, statusHttpCode: statusHttpCode);
            return;
        }

        bool consentMissing = !_latestStatus.ConsentActive;

        ConsentActiveValue.Text = _latestStatus.ConsentActive ? "Yes" : "No";
        BackendConnectedValue.Text = _latestStatus.BackendConnected ? "Yes" : "No";
        LocalApiCurrentSnapshotResponse? truthSnapshot = nowTruth.Snapshot;
        if (truthSnapshot != null)
        {
            FocusStrengthValue.Text = truthSnapshot.FocusStrength.ToString("F2", CultureInfo.InvariantCulture);
            OverloadValue.Text = ResolvePressureLevel(truthSnapshot).ToString("F2", CultureInfo.InvariantCulture);
            LastActionValue.Text = inputSummary != null
                ? $"{truthSnapshot.LastAction} | inputSamples={inputSummary.SampleCount}"
                : truthSnapshot.LastAction;
        }
        else
        {
            FocusStrengthValue.Text = NotAvailableText;
            OverloadValue.Text = NotAvailableText;
            LastActionValue.Text = NotAvailableText;
        }

        LastEventValue.Text = _latestStatus.LastEventTimestampUtc?.ToString("O") ?? NotAvailableText;
        PolicyVersionValue.Text = _latestStatus.PolicyVersion;
        SchemaVersionValue.Text = _latestStatus.SchemaVersion;
        AgentVersionValue.Text = _latestStatus.AgentVersion;
        ConsentVersionValue.Text = _latestStatus.ConsentVersion;
        ConsentTimestampValue.Text = _latestStatus.ConsentTimestampUtc?.ToString("O") ?? NotAvailableText;
        ConsentHashValue.Text = _latestStatus.ConsentDisclosureHash;
        LastErrorValue.Text = string.IsNullOrWhiteSpace(nowTruth.Explanation)
            ? BuildOnlineErrorMessage(_latestStatus, truthSnapshot, serviceProbe, statusFresh)
            : nowTruth.Explanation;
        SnapshotConfidenceValue.Text = truthSnapshot != null
            ? truthSnapshot.Confidence.ToString("F2", CultureInfo.InvariantCulture)
            : NotAvailableText;
        OverloadActiveValue.Text = truthSnapshot != null
            ? (truthSnapshot.OverloadActive ? "Yes" : "No")
            : NotAvailableText;
        SustainedAttentionValue.Text = truthSnapshot != null
            ? truthSnapshot.SustainedAttentionDurationSec.ToString("F0", CultureInfo.InvariantCulture)
            : NotAvailableText;
        IdleFragmentationValue.Text = truthSnapshot != null
            ? truthSnapshot.IdleFragmentationIndex.ToString("F2", CultureInfo.InvariantCulture)
            : NotAvailableText;
        QuickPauseResumeButton.Content = _latestStatus.Paused ? "Resume" : "Pause";
        QuickPauseResumeButton.IsEnabled = true;
        ConsentOverlay.Visibility = consentMissing ? Visibility.Visible : Visibility.Collapsed;
        UpdateTopBar(serviceProbe, runtimeSettings, _latestStatus, nowTruth, _latestVisualState);
        UpdateCaptureHealthCards(_latestStatus, _latestLiveSession, statusFresh);
        UpdateSessionTruthCards(_latestStatus, _latestLiveSession, statusFresh);
        UpdateInputTruthCards(_latestStatus, _latestLiveSession, statusFresh);
        UpdateStateTruthCards(_latestStatus, truthSnapshot, statusFresh, snapshotFresh);
        UpdateActionTruthCards(_allRecentEvents, _latestStatus, truthSnapshot, statusFresh);

        PopulateOverview(nowTruth, _latestLiveSession, inputSummary, _recentEvents, previousSnapshot);
        PopulateInputs(inputSummary, _retention, truthSnapshot, _latestStatus, _effectiveSettings);
        PopulateInterventions(_allRecentEvents, _effectiveSettings);
        PopulateSettings(_effectiveSettings);
        PopulateStates(truthSnapshot, _latestLiveSession);
        PopulateTimeline(GetSelectedHistoryTimeline());
        PopulateWhyDrawer(nowTruth, _allRecentEvents, _latestVisualState);
        UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        UpdateEngineHealthCards(serviceProbe, apiReachable, truthSnapshot, statusFresh, snapshotFresh, statusHttpCode);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if ((nowUtc - _lastManualRefreshUtc) < ManualRefreshThrottle)
        {
            return;
        }

        _lastManualRefreshUtc = nowUtc;
        ControlCenterFileLog.Info("manual_refresh_clicked");
        await RefreshDiagnosticsAsync(manual: true).ConfigureAwait(true);
        bool snapshotFresh = !IsSnapshotStale(_latestStatus, _lastSnapshot, DateTimeOffset.UtcNow);
        ControlCenterFileLog.Info(
            $"manual_refresh_completed api_reachable={_apiClient.LastStatusHttpCode.HasValue.ToString().ToLowerInvariant()} snapshot_fresh={snapshotFresh.ToString().ToLowerInvariant()}");
    }

    private void VersionsButton_Click(object sender, RoutedEventArgs e)
    {
        VersionsPopup.IsOpen = !VersionsPopup.IsOpen;
    }

    private void WhyDrawerToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetWhyDrawerVisible(WhyDrawerPanel.Visibility != Visibility.Visible);
    }

    private void WhyDrawerCloseButton_Click(object sender, RoutedEventArgs e)
    {
        SetWhyDrawerVisible(false);
    }

    private void WhyDrawerBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SetWhyDrawerVisible(false);
        e.Handled = true;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || WhyDrawerPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        SetWhyDrawerVisible(false);
        e.Handled = true;
    }

    private void OverviewPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ensure outer ScrollViewer receives wheel events even when inner elements consume them
        if (!e.Handled)
        {
            OverviewPanel.ScrollToVerticalOffset(OverviewPanel.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void NowDetailCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement card)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (source != null
            && (FindAncestor<Expander>(source) != null
                || FindAncestor<Button>(source) != null
                || FindAncestor<CheckBox>(source) != null))
        {
            return;
        }

        ActivateNowDetailCard(card.Tag as string);
        e.Handled = true;
    }

    private void NowRecoveryDismissButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRecoveryEventId > 0)
        {
            _dismissedRecoveryEventId = _activeRecoveryEventId;
            SaveRecoveryAckEventId(_dismissedRecoveryEventId);
        }

        _activeRecoveryEventId = 0;
        NowRecoveryBannerBorder.Visibility = Visibility.Collapsed;
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Overview);
    }

    private void TimelineNavButton_Click(object sender, RoutedEventArgs e)
    {
        // Default to Day view, today's data — never show an empty page
        _historyPeriod = HistoryPeriod.Day;
        _historyRange = HistoryRange.Today;
        ShowSection(UiSection.Timeline);
        PopulateTimeline(_todayTimeline ?? GetSelectedHistoryTimeline());
    }

    private void HistoryTabDay_Click(object sender, MouseButtonEventArgs e)
    {
        _historyPeriod = HistoryPeriod.Day;
        _historyRange = HistoryRange.Today;
        UpdateHistoryPeriodTabs();
        PopulateTimeline(_todayTimeline ?? GetSelectedHistoryTimeline());
    }

    private void HistoryTabWeek_Click(object sender, MouseButtonEventArgs e)
    {
        _historyPeriod = HistoryPeriod.Week;
        _historyRange = HistoryRange.AllTime;
        UpdateHistoryPeriodTabs();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void HistoryTabTwoWeeks_Click(object sender, MouseButtonEventArgs e)
    {
        _historyPeriod = HistoryPeriod.TwoWeeks;
        _historyRange = HistoryRange.AllTime;
        UpdateHistoryPeriodTabs();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void UpdateHistoryPeriodTabs()
    {
        ApplyHistoryPeriodTab(HistoryTabDayText, HistoryTabDayLine, _historyPeriod == HistoryPeriod.Day);
        ApplyHistoryPeriodTab(HistoryTabWeekText, HistoryTabWeekLine, _historyPeriod == HistoryPeriod.Week);
        ApplyHistoryPeriodTab(HistoryTabTwoWeeksText, HistoryTabTwoWeeksLine, _historyPeriod == HistoryPeriod.TwoWeeks);
    }

    // ── Actions page period tabs ─────────────────────────────────────────────

    /// <summary>
    /// Re-renders the Actions page from today's live polling cache.
    /// Period tabs have been removed — the page always shows today's data.
    /// </summary>
    private void RepopulateActionsNewUI()
    {
        if (_lastInterventionEvents.Count == 0) return;
        PopulateActionsNewUI(_lastInterventionEvents
            .OrderByDescending(e => e.TimestampUtc).ToList());
    }



    private static void ApplyHistoryPeriodTab(TextBlock text, Border underline, bool active)
    {
        // No box/background — just text colour + 2px underline
        text.Foreground = new SolidColorBrush(active
            ? Color.FromRgb(0xA5, 0xB4, 0xFC)
            : Color.FromRgb(0x47, 0x55, 0x69));
        text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium;
        underline.Background = new SolidColorBrush(active
            ? Color.FromRgb(0x63, 0x66, 0xF1)
            : Colors.Transparent);
    }

    // ── Tracking page accordion ──────────────────────────────────────────────

    private void TrackingNav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border navBorder && navBorder.Tag is string tag)
            SetTrackingSection(tag);
    }

    private string? _openTrackingSection = null;

    private void SetTrackingSection(string tag)
    {
        var sections = new (string Tag, Border Nav, Border Content, TextBlock Chevron)[]
        {
            ("Observes",      TrackingNavObserves,      TrackingContentObserves,      TrackingChevronObserves),
            ("NeverCollects", TrackingNavNeverCollects, TrackingContentNeverCollects, TrackingChevronNeverCollects),
            ("Quality",       TrackingNavQuality,       TrackingContentQuality,       TrackingChevronQuality),
            ("Privacy",       TrackingNavPrivacy,       TrackingContentPrivacy,       TrackingChevronPrivacy),
            ("Data",          TrackingNavData,          TrackingContentData,          TrackingChevronData),
        };

        // Clicking the open section closes it; clicking a closed one opens it
        _openTrackingSection = (_openTrackingSection == tag) ? null : tag;

        foreach (var (sTag, nav, content, chevron) in sections)
        {
            bool isOpen = sTag == _openTrackingSection;
            content.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            nav.CornerRadius = isOpen ? new CornerRadius(10, 10, 0, 0) : new CornerRadius(10);
            chevron.Text = isOpen ? "−" : "+";
            chevron.Foreground = new SolidColorBrush(isOpen
                ? Color.FromRgb(0x63, 0x66, 0xF1)
                : Color.FromRgb(0x47, 0x55, 0x69));
        }
    }

    private void ResetTrackingSections()
    {
        _openTrackingSection = null;
        foreach (var (nav, content, chevron) in new (Border Nav, Border Content, TextBlock Chevron)[]
        {
            (TrackingNavObserves,      TrackingContentObserves,      TrackingChevronObserves),
            (TrackingNavNeverCollects, TrackingContentNeverCollects, TrackingChevronNeverCollects),
            (TrackingNavQuality,       TrackingContentQuality,       TrackingChevronQuality),
            (TrackingNavPrivacy,       TrackingContentPrivacy,       TrackingChevronPrivacy),
            (TrackingNavData,          TrackingContentData,          TrackingChevronData),
        })
        {
            content.Visibility = Visibility.Collapsed;
            nav.CornerRadius = new CornerRadius(10);
            chevron.Text = "+";
            chevron.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
        }
    }

    // Legacy overload kept so old callsites compile (tabs now use Border+TextBlock pair)
    private static void ApplyHistoryPeriodTab(Border tab, TextBlock text, bool active)
    {
        tab.Background = Brushes.Transparent;
        tab.BorderBrush = Brushes.Transparent;
        text.Foreground = new SolidColorBrush(active
            ? Color.FromRgb(0xA5, 0xB4, 0xFC)
            : Color.FromRgb(0x47, 0x55, 0x69));
    }

    private void TimelinePanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled)
        {
            TimelinePanel.ScrollToVerticalOffset(TimelinePanel.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void HistoryRange1hButton_Click(object sender, RoutedEventArgs e)
    {
        _historyRange = HistoryRange.Last1Hour;
        UpdateHistoryControls();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void HistoryRange4hButton_Click(object sender, RoutedEventArgs e)
    {
        _historyRange = HistoryRange.Last4Hours;
        UpdateHistoryControls();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void HistoryRangeTodayButton_Click(object sender, RoutedEventArgs e)
    {
        _historyRange = HistoryRange.Today;
        UpdateHistoryControls();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void HistoryRangeAllTimeButton_Click(object sender, RoutedEventArgs e)
    {
        _historyRange = HistoryRange.AllTime;
        UpdateHistoryControls();
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void HistoryExplainModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _historyExplainMode = HistoryExplainModeCheckBox.IsChecked == true;
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void TasksNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Tasks);
    }

    private void ProjectsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Projects);
        // Read when the page is opened, never on a timer: the project list changes when a person
        // decides it does. Not under the preview harness, where a live read would land mid-render
        // and replace the sample data with whatever this machine happens to hold.
        if (!PreviewMode)
        {
            _ = RefreshProjectsAsync();
        }
    }

    private void ReportsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Reports);
        if (!PreviewMode)
        {
            _ = OpenReportsPageAsync();
        }
    }

    private void ActionsSuggestionsTabButton_Click(object sender, RoutedEventArgs e)
    {
        _actionsView = ActionsView.Suggestions;
        UpdateActionsTabControls();
    }

    private void ActionsChangesTabButton_Click(object sender, RoutedEventArgs e)
    {
        _actionsView = ActionsView.Changes;
        UpdateActionsTabControls();
    }

    private void StatesNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSystemAdvancedSection();
    }

    private void SystemAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSystemAdvancedSection();
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Settings);
        ShowSettingsSection("Intensity");
    }

    private void SettingsSubNav_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string section)
            ShowSettingsSection(section);
    }

    private void ShowSettingsSection(string section)
    {
        SettingsSectionIntensity.Visibility   = Visibility.Collapsed;
        SettingsSectionPrivacy.Visibility     = Visibility.Collapsed;
        SettingsSectionCollection.Visibility  = Visibility.Collapsed;
        SettingsSectionCalibration.Visibility = Visibility.Collapsed;
        SettingsSectionFeatures.Visibility    = Visibility.Collapsed;
        SettingsSectionAppearance.Visibility  = Visibility.Collapsed;
        SettingsSectionActions.Visibility     = Visibility.Collapsed;
        SettingsSectionTracking.Visibility    = Visibility.Collapsed;

        var navItems = new[]
        {
            (SettingsNavIntensity,   SettingsNavIntensityText),
            (SettingsNavPrivacy,     SettingsNavPrivacyText),
            (SettingsNavCollection,  SettingsNavCollectionText),
            (SettingsNavCalibration, SettingsNavCalibrationText),
            (SettingsNavFeatures,    SettingsNavFeaturesText),
            (SettingsNavActions,     SettingsNavActionsText),
            (SettingsNavTracking,    SettingsNavTrackingText),
            (SettingsNavAppearance,  SettingsNavAppearanceText),
        };
        var inactiveColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        var inactiveBg    = new SolidColorBrush(Colors.Transparent);
        foreach (var (border, label) in navItems)
        {
            border.Background = inactiveBg;
            label.Foreground  = inactiveColor;
            label.FontWeight  = FontWeights.Normal;
        }

        var activeColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
        var activeBg    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#17171F"));
        Border activeNav; TextBlock activeLabel; UIElement activeSection;
        switch (section)
        {
            case "Privacy":     activeNav = SettingsNavPrivacy;     activeLabel = SettingsNavPrivacyText;     activeSection = SettingsSectionPrivacy;     break;
            case "Collection":  activeNav = SettingsNavCollection;  activeLabel = SettingsNavCollectionText;  activeSection = SettingsSectionCollection;  break;
            case "Calibration": activeNav = SettingsNavCalibration; activeLabel = SettingsNavCalibrationText; activeSection = SettingsSectionCalibration; break;
            case "Features":    activeNav = SettingsNavFeatures;    activeLabel = SettingsNavFeaturesText;    activeSection = SettingsSectionFeatures;    break;
            case "Appearance":  activeNav = SettingsNavAppearance;  activeLabel = SettingsNavAppearanceText;  activeSection = SettingsSectionAppearance;  break;
            case "Actions":
                activeNav = SettingsNavActions; activeLabel = SettingsNavActionsText; activeSection = SettingsSectionActions;
                UpdateActionsTabControls();
                break;
            case "Tracking":
                activeNav = SettingsNavTracking; activeLabel = SettingsNavTrackingText; activeSection = SettingsSectionTracking;
                ResetTrackingSections();
                break;
            default:            activeNav = SettingsNavIntensity;   activeLabel = SettingsNavIntensityText;   activeSection = SettingsSectionIntensity;   break;
        }
        activeNav.Background   = activeBg;
        activeLabel.Foreground = activeColor;
        activeLabel.FontWeight = FontWeights.Medium;
        activeSection.Visibility = Visibility.Visible;
    }

    private void AppearanceLightModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isLightTheme = AppearanceLightModeToggle.IsChecked == true;
        AppearancePreferenceStore.Save(_isLightTheme);
        ApplyTheme(_isLightTheme);
    }

    private void BrowserTitleReadingToggle_Click(object sender, RoutedEventArgs e)
    {
        Porta.Agent.Common.Privacy.PrivacySettingsStore.Save(
            new Porta.Agent.Common.Privacy.PrivacySettings(BrowserTitleReadingToggle.IsChecked == true));
    }

    // Workstream 3 — the ONLY permanent widget hide. The widget's own hide button hides
    // until tomorrow; this toggle writes/clears the disabled flag the user agent's
    // visibility store checks first. Takes effect within one maintenance tick (~2s).
    private void DesktopWidgetVisibleToggle_Click(object sender, RoutedEventArgs e)
    {
        DesktopWidgetPreferenceStore.SaveWidgetEnabled(DesktopWidgetVisibleToggle.IsChecked == true);
    }

    private void ApplyTheme(bool isLight)
    {
        SetBrushColor("PortaTextPrimaryBrush",     isLight ? "#0F172A" : "#F1F5F9");
        SetBrushColor("PortaTextSecondaryBrush",   isLight ? "#374151" : "#94A3B8");
        SetBrushColor("PortaTextMutedBrush",       isLight ? "#64748B" : "#475569");
        SetBrushColor("PortaTextBodyBrush",        isLight ? "#1E293B" : "#CBD5E1");
        SetBrushColor("PortaSurfaceBaseBrush",     isLight ? "#F1F5F9" : "#1C1C2E");
        SetBrushColor("PortaSurfaceElevatedBrush", isLight ? "#FFFFFF"  : "#1B1F25");
        SetBrushColor("PortaSurfaceHoverBrush",    isLight ? "#E8ECF0" : "#1D232A");
        SetBrushColor("PortaBorderBrush",          isLight ? "#CBD5E1" : "#252535");
        SetBrushColor("PortaBorderHoverBrush",     isLight ? "#94A3B8" : "#3A3A50");
        SetBrushColor("PortaTitleBarBrush",        isLight ? "#FFFFFF"  : "#080809");
        SetBrushColor("PortaContentHeaderBrush",   isLight ? "#F5F7FA" : "#0D0D12");
        SetBrushColor("PortaCardBrush",            isLight ? "#FFFFFF"  : "#0F0F18");
        SetBrushColor("PortaCardBorderBrush",      isLight ? "#E2E8F0" : "#252535");
        SetBrushColor("PortaBubbleBrush",          isLight ? "#F1F5F9" : "#111119");
        SetBrushColor("PortaDividerBrush",         isLight ? "#E8ECF2" : "#1C1C28");
        SetBrushColor("PortaNavHoverBrush",        isLight ? "#E8EDF3" : "#0F0F1A");
        SetBrushColor("PortaNavActiveBrush",       isLight ? "#DDE3EC" : "#17171F");
        SetBrushColor("PortaButtonBrush",          isLight ? "#E8EDF3" : "#1A1F26");
        SetBrushColor("PortaButtonPressedBrush",   isLight ? "#D4DAE3" : "#161B22");
    }

    private void SetBrushColor(string key, string hexColor)
    {
        var color = (Color)ColorConverter.ConvertFromString(hexColor);
        if (Resources[key] is SolidColorBrush brush)
        {
            if (!brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                Resources[key] = new SolidColorBrush(color);
            }
        }
        else
        {
            Resources[key] = new SolidColorBrush(color);
        }
    }

    private void DiagnosticsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Diagnostics);
    }

    private void ActivateNowDetailCard(string? cardKey)
    {
        switch (cardKey)
        {
            case "today-work":
            case "last-focus":
                OpenNowFocusDetail();
                break;
            case "today-overload":
                OpenNowOverloadDetail();
                break;
            case "current-status":
                ShowSection(UiSection.Settings);
                ShowSettingsSection("Actions");
                break;
            case "overload":
                OpenNowOverloadDetail();
                break;
            case "focus":
                OpenNowFocusDetail();
                break;
            case "calibration":
                OpenNowCalibrationDetail();
                break;
            case "data-quality":
                OpenNowDataQualityDetail();
                break;
        }
    }

    private void OpenNowOverloadDetail()
    {
        ShowSection(UiSection.Overview);
        SetWhyDrawerVisible(true);
        ScrollIntoViewDeferred(WhyDrawerPanel);
    }

    private void OpenNowFocusDetail()
    {
        _historyRange = HistoryRange.Today;
        ShowSection(UiSection.Timeline);
        UpdateHistoryControls();
        PopulateTimeline(GetSelectedHistoryTimeline());
        ScrollIntoViewDeferred(HistoryCoordinateScrollViewer);
    }

    private void OpenNowCalibrationDetail()
    {
        ShowSection(UiSection.Diagnostics);
        ScrollIntoViewDeferred(CalibrationStatusPill);
    }

    private void OpenNowDataQualityDetail()
    {
        ShowSection(UiSection.Diagnostics);
        ScrollIntoViewDeferred(SignalsStatusPill);
    }

    private void ShowSystemAdvancedSection()
    {
        ShowSection(UiSection.Diagnostics);
        ScrollIntoViewDeferred(SystemAdvancedSection);
    }

    private void SetWhyDrawerVisible(bool show)
    {
        WhyDrawerBackdrop.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WhyDrawerPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        WhyDrawerToggleButton.Content = show ? "Hide Why" : "Why";
    }

    private void ScrollIntoViewDeferred(FrameworkElement element)
    {
        Dispatcher.BeginInvoke(
            () => element.BringIntoView(),
            DispatcherPriority.Background);
    }

    private async void QuickPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_latestStatus is null)
        {
            return;
        }

        if (_latestStatus?.Paused == true)
        {
            await _apiClient.ResumeAsync().ConfigureAwait(true);
        }
        else
        {
            await _apiClient.PauseAsync("control_center_quick_pause").ConfigureAwait(true);
        }

        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void AgreeConsentButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.GrantConsentAsync().ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void RevokeConsentButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.RevokeConsentAsync().ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.PauseAsync("control_center_pause").ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.ResumeAsync().ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void ExportDataButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportBundleAsync().ConfigureAwait(true);
    }

    private async void ExportBundleButton_Click(object sender, RoutedEventArgs e)
    {
        await ExportBundleAsync().ConfigureAwait(true);
    }

    private async Task ExportBundleAsync()
    {
        LocalApiExportResponse? response = await _apiClient.ExportDataAsync().ConfigureAwait(true);
        if (response?.Success != true || string.IsNullOrWhiteSpace(response.BundlePath))
        {
            ControlCenterFileLog.Error("export_bundle_failed", null);
            MessageBox.Show(
                "Export failed. Ensure the service is running and try again.",
                "PORTA Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        string? folder = Path.GetDirectoryName(response.BundlePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }

        ControlCenterFileLog.Info($"export_bundle_created path={response.BundlePath}");

        MessageBox.Show(
            $"Export created:\n{response.BundlePath}",
            "PORTA Control Center",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void RestartServiceButton_Click(object sender, RoutedEventArgs e)
    {
        RestartResult result = await Task.Run(RestartServiceInternal).ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);

        MessageBox.Show(
            result.Message,
            "PORTA Control Center",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CopyDiagnosticsSummaryButton_Click(object sender, RoutedEventArgs e)
    {
        string summary = BuildDiagnosticsSummary();
        Clipboard.SetText(summary);
        MessageBox.Show(
            "Diagnostics summary copied to clipboard.",
            "PORTA Control Center",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void DeleteLocalDataButton_Click(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirmation = MessageBox.Show(
            "Delete all local tracking data? This removes stored activity history and resets consent.",
            "Delete Local Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        // Try via API first; if that fails, delete files directly.
        bool success = await _apiClient.DeleteLocalDataAsync().ConfigureAwait(true);
        if (!success)
        {
            ControlCenterFileLog.Info("delete_local_data_api_unavailable_falling_back_to_direct");
            success = await DeleteLocalDataDirectAsync().ConfigureAwait(true);
        }

        if (!success)
        {
            ControlCenterFileLog.Error("delete_local_data_failed", null);
            MessageBox.Show(
                "Delete failed. Try again from the Tracking page after restarting the app.",
                "Ari",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ControlCenterFileLog.Info("delete_local_data_success");
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
        MessageBox.Show(
            "Local data deleted. Ari will start fresh on the next activity.",
            "Ari",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static async Task<bool> DeleteLocalDataDirectAsync()
    {
        // Direct-file fallback: stop service, delete DB files, restart.
        return await Task.Run(() =>
        {
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "PortaAgent");

                // Stop the service so it releases the DB file lock.
                using var sc = new System.ServiceProcess.ServiceController("AriAgent");
                bool wasRunning = sc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                if (wasRunning)
                {
                    sc.Stop();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }

                foreach (string pattern in new[] { "core-state.db*", "core-state.legacy.db*" })
                {
                    foreach (string file in Directory.GetFiles(dataDir, pattern))
                    {
                        File.Delete(file);
                    }
                }

                if (wasRunning)
                {
                    sc.Start();
                    sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                }

                return true;
            }
            catch
            {
                return false;
            }
        }).ConfigureAwait(false);
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortaAgent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static bool IsTrayRunning()
    {
        return Process.GetProcessesByName("AriTray").Length > 0
            || Process.GetProcessesByName("Porta.Tray").Length > 0
            || Process.GetProcessesByName("Porta.UserAgent").Length > 0;
    }

    private void ShowSection(UiSection section)
    {
        _activeSection = section;
        bool showSystem = section == UiSection.Diagnostics || section == UiSection.States;
        OverviewPanel.Visibility = section == UiSection.Overview ? Visibility.Visible : Visibility.Collapsed;
        TimelinePanel.Visibility = section == UiSection.Timeline ? Visibility.Visible : Visibility.Collapsed;
        TasksPanel.Visibility = section == UiSection.Tasks ? Visibility.Visible : Visibility.Collapsed;
        ProjectsPanel.Visibility = section == UiSection.Projects ? Visibility.Visible : Visibility.Collapsed;
        ReportsPanel.Visibility = section == UiSection.Reports ? Visibility.Visible : Visibility.Collapsed;
        StatesPanel.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = section == UiSection.Account ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == UiSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = showSystem ? Visibility.Visible : Visibility.Collapsed;

        SectionTitleText.Text = section switch
        {
            UiSection.Overview => "Today",
            UiSection.Timeline => "History",
            UiSection.Tasks => "Tasks",
            UiSection.Projects => "Projects",
            UiSection.Reports => "Reports",
            UiSection.States => "System",
            UiSection.Account => "Account",
            UiSection.Settings => "Settings",
            UiSection.Diagnostics => "System",
            _ => "Today"
        };

        ApplyNavState(OverviewNavButton, section == UiSection.Overview);
        ApplyNavState(TimelineNavButton, section == UiSection.Timeline);
        ApplyNavState(TasksNavButton, section == UiSection.Tasks);
        ApplyNavState(ProjectsNavButton, section == UiSection.Projects);
        ApplyNavState(ReportsNavButton, section == UiSection.Reports);
        ApplyNavState(StatesNavButton, false);
        ApplyNavState(AccountNavButton, section == UiSection.Account);
        ApplyNavState(SettingsNavButton, section == UiSection.Settings);
        ApplyNavState(DiagnosticsNavButton, showSystem);

        if (section != UiSection.Overview && WhyDrawerPanel.Visibility == Visibility.Visible)
        {
            SetWhyDrawerVisible(false);
        }


    }

    private void UpdateActionsTabControls()
    {
        if (ActionsSuggestionsTabButton == null
            || ActionsChangesTabButton == null
            || SuggestionsTabContent == null
            || ChangesTabContent == null)
        {
            return;
        }

        bool showSuggestions = _actionsView == ActionsView.Suggestions;
        SuggestionsTabContent.Visibility = showSuggestions ? Visibility.Visible : Visibility.Collapsed;
        ChangesTabContent.Visibility = showSuggestions ? Visibility.Collapsed : Visibility.Visible;

        ApplyHistoryButtonState(ActionsSuggestionsTabButton, showSuggestions);
        ApplyHistoryButtonState(ActionsChangesTabButton, !showSuggestions);
    }

    private static void ApplyNavState(Button button, bool selected)
    {
        button.Tag = selected ? "active" : null;
    }

    private void UpdateHeaderStatusChip(string runState, string confidenceLabel)
    {
        bool running = string.Equals(runState, "Running", StringComparison.Ordinal);
        HeaderStatusChipText.Text = $"● {runState} · {confidenceLabel}";
        HeaderStatusChipText.Foreground = new SolidColorBrush(
            running ? Color.FromRgb(0x16, 0xC7, 0x84) : Color.FromRgb(0x64, 0x74, 0x8B));
        HeaderStatusChipBorder.Background = new SolidColorBrush(
            running ? Color.FromArgb(0x1E, 0x16, 0xC7, 0x84) : Color.FromArgb(0x14, 0x47, 0x55, 0x69));
        HeaderStatusChipBorder.BorderBrush = new SolidColorBrush(
            running ? Color.FromArgb(0x33, 0x16, 0xC7, 0x84) : Color.FromArgb(0x1E, 0x47, 0x55, 0x69));
    }

    private LocalApiTimelineResponse? GetSelectedHistoryTimeline()
    {
        // For Week/2-week periods, use the higher-resolution dedicated timelines so
        // short episodes (Away, Distraction < 30 min) are detected correctly.
        if (_historyPeriod == HistoryPeriod.Week)
            return _weekTimeline ?? _allTimeTimeline;
        if (_historyPeriod == HistoryPeriod.TwoWeeks)
            return _twoWeekTimeline ?? _allTimeTimeline;

        return _historyRange switch
        {
            HistoryRange.Last1Hour => _hourTimeline,
            HistoryRange.Last4Hours => _fourHourTimeline,
            HistoryRange.Today => _todayTimeline,
            HistoryRange.AllTime => _allTimeTimeline,
            _ => _hourTimeline
        };
    }

    private bool ShouldRefreshAllTimeTimeline(bool forceSlowRefresh)
    {
        int retentionCheck = _retention?.RetentionDays ?? 90;
        if (retentionCheck <= 0)
        {
            return false;
        }

        if (_allTimeTimeline == null)
        {
            return true;
        }

        if (_historyRange == HistoryRange.AllTime)
        {
            return true;
        }

        return forceSlowRefresh
            && (DateTimeOffset.UtcNow - _lastAllTimeHistoryRefreshUtc) >= AllTimeHistoryRefreshInterval;
    }

    private async Task<LocalApiTimelineResponse?> GetAllTimeTimelineAsync(DateTimeOffset nowUtc)
    {
        // Fall back to 90 days when _retention hasn't loaded yet so history doesn't stay blank.
        int retentionDays = Math.Max(1, _retention?.RetentionDays ?? 90);
        DateTimeOffset fromUtc = nowUtc.AddDays(-retentionDays);
        int bucketSeconds = ResolveAllTimeBucketSeconds(retentionDays);
        return await _apiClient.GetTimelineAsync(fromUtc, nowUtc, bucketSeconds).ConfigureAwait(true);
    }

    private static int ResolveAllTimeBucketSeconds(int retentionDays)
    {
        return retentionDays switch
        {
            <= 2 => 300,
            <= 7 => 900,
            <= 30 => 1800,
            _ => 3600
        };
    }

    private void UpdateHistoryControls()
    {
        if (HistoryExplainModeCheckBox != null)
        {
            HistoryExplainModeCheckBox.IsChecked = _historyExplainMode;
        }

        if (HistoryAdvancedMetricsPanel != null)
        {
            HistoryAdvancedMetricsPanel.Visibility = _historyExplainMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (HistorySelectionExplainPanel != null)
        {
            HistorySelectionExplainPanel.Visibility = _historyExplainMode
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        ApplyHistoryButtonState(HistoryRange1hButton, _historyRange == HistoryRange.Last1Hour);
        ApplyHistoryButtonState(HistoryRange4hButton, _historyRange == HistoryRange.Last4Hours);
        ApplyHistoryButtonState(HistoryRangeTodayButton, _historyRange == HistoryRange.Today);
        ApplyHistoryButtonState(HistoryRangeAllTimeButton, _historyRange == HistoryRange.AllTime);
    }

    private static void ApplyHistoryButtonState(Button button, bool selected)
    {
        if (button == null)
        {
            return;
        }

        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(26, 31, 38))
            : new SolidColorBrush(Color.FromRgb(17, 21, 26));
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(42, 47, 54))
            : new SolidColorBrush(Color.FromRgb(38, 43, 51));
        button.Foreground = new SolidColorBrush(Color.FromRgb(230, 233, 237));
    }

    private static LocalApiTimelineResponse? PreferFreshTimeline(
        LocalApiTimelineResponse? existing,
        LocalApiTimelineResponse? latest)
    {
        return latest ?? existing;
    }

    private void PopulateOverview(
        NowTruth nowTruth,
        LocalApiLiveSessionStateResponse? liveSession,
        LocalApiInputsSummaryResponse? inputSummary,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
        _ = inputSummary;
        _ = previousSnapshot;
        PopulateNowCards(nowTruth, liveSession, _latestVisualState);
    }

    private void PopulateNowCards(
        NowTruth nowTruth,
        LocalApiLiveSessionStateResponse? liveSession,
        PortaVisualState visualState)
    {
        LocalApiCurrentSnapshotResponse? snapshot = nowTruth.Snapshot;
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        // Greeting + date
        int hour = DateTime.Now.Hour;
        TodayGreetingText.Text = hour < 12 ? "Good morning" : hour < 17 ? "Good afternoon" : "Good evening";
        TodayDateText.Text = DateTime.Now.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

        TodayPageMetrics todayMetrics = TodayPageMetricsBuilder.Build(
            _todayTimeline,
            snapshot,
            liveSession,
            nowUtc);
        TodayTrackingState todayState = TodayPageCopyBuilder.ResolveTrackingState(_latestStatus, snapshot, liveSession);
        string readableExplanation = TodayPageCopyBuilder.BuildReadableExplanation(todayState, nowTruth.Explanation);
        double todayFocusMin = todayMetrics.FocusedTodaySeconds / 60d;
        double todayTrackedMin = todayMetrics.TrackedTodaySeconds / 60d;
        bool isPressureHigh = snapshot?.OverloadActive == true ||
            (snapshot is { analysis_ready: true } && ResolvePressureLevel(snapshot) >= 0.75);
        // Derive session start label from first snapshot of today
        string? sessionStartLabel = null;
        IReadOnlyList<LocalApiCurrentSnapshotResponse> todaySnaps = _todayTimeline?.Snapshots ?? [];
        if (todaySnaps.Count > 0)
            sessionStartLabel = todaySnaps[0].TimestampUtc.ToLocalTime().ToString("h:mm tt");

        // Derive day name and dow average focus minutes from context
        string dayName = DateTime.Now.ToString("dddd"); // e.g. "Monday"
        double? dayAvgFocusMin = null;
        // (dow averages not yet in context — placeholder for future extension)

        string ariStatus = TodayPageCopyBuilder.BuildAriStatusMessage(
            snapshot,
            _ariContext,
            todayFocusMin,
            todayTrackedMin,
            distractionCount: 0,
            avgRecoveryMin: 0d,
            isPressureHigh,
            contextSwitchesRecent: 0,
            sessionStartLabel: sessionStartLabel,
            dayName: dayName,
            dayAvgFocusMin: dayAvgFocusMin);
        string currentStatusText = ariStatus;

        TodaySummaryText.Text = BuildTodaySummarySentence(
            todayState,
            todayMetrics,
            visualState.FocusEpisodeSeconds,
            readableExplanation);

        // Update state chip on Today greeting row
        UpdateTodayStateChip(visualState, snapshot);

        PopulateTodayWorkCard(todayMetrics, todayState, visualState);
        PopulateTodayLastFocusCard(todayState, liveSession, visualState);
        PopulateTodayOverloadCard(todayState, snapshot, visualState);
        PopulateTodaySupportingStatusLine(todayState, currentStatusText, readableExplanation);
        PopulateTodayRecoveryBanner(todayState, readableExplanation, visualState);
        PopulateCurrentSessionCard(todayState, liveSession);
        UpdateCardProgressBars(todayMetrics, snapshot);
        RefreshSessionTimelineBar();
    }

    private void PopulateTodayWorkCard(
        TodayPageMetrics todayMetrics,
        TodayTrackingState todayState,
        PortaVisualState visualState)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            NowOverloadValueText.Text = ResolveTodayWorkMetricText(todayState, todayMetrics, _latestStatus);
            NowOverloadActiveText.Text = "Tracking paused";
        }
        else if (todayState == TodayTrackingState.Unavailable)
        {
            NowOverloadValueText.Text = ResolveTodayWorkMetricText(todayState, todayMetrics, _latestStatus);
            NowOverloadActiveText.Text = "Service reconnecting";
        }
        else if (todayMetrics.HasProcessedTimeline)
        {
            AnimateTopMetric(
                metricKey: "today_work_total",
                targetValue: Math.Max(0d, todayMetrics.DisplayedTrackedTodaySeconds),
                applyValue: value => NowOverloadValueText.Text = LocalMetricDurationFormatter.FormatMetricDuration(value));
            // Show start time only — no internal status language
            NowOverloadActiveText.Text = BuildTodayWorkStartText(todayMetrics);
        }
        else
        {
            NowOverloadValueText.Text = ResolveTodayWorkMetricText(todayState, todayMetrics, _latestStatus);
            NowOverloadActiveText.Text = "Updating";
        }

        // Never show confidence on the home screen
        NowOverloadTrustText.Visibility = Visibility.Collapsed;
    }

    private void PopulateTodayLastFocusCard(
        TodayTrackingState todayState,
        LocalApiLiveSessionStateResponse? liveSession,
        PortaVisualState visualState)
    {
        // Show task continuity score as a % — this is the focus quality signal, not a duration.
        LocalApiCurrentSnapshotResponse? snapshot = _lastSnapshot;
        bool hasScore = snapshot?.analysis_ready == true
            && double.IsFinite(snapshot.TaskContinuityScore)
            && snapshot.TaskContinuityScore > 0d;

        NowFocusStreakValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84));
        NowDeepWorkStreakText.Visibility = Visibility.Collapsed;

        if (hasScore)
        {
            AnimateTopMetric(
                metricKey: "today_focus_score",
                targetValue: Math.Max(0d, snapshot!.TaskContinuityScore * 100d),
                applyValue: value => NowFocusStreakValueText.Text = $"{value:F0}%");
        }
        else if (todayState == TodayTrackingState.Paused)
        {
            NowFocusStreakValueText.Text = "Paused";
            NowFocusStreakValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
        }
        else
        {
            NowFocusStreakValueText.Text = "—";
        }

        // Subtext: live activity description only — no confidence labels
        NowFocusChoppinessText.Text = BuildTodayCurrentFocusSupportText(liveSession);
        NowFocusSummaryText.Visibility = Visibility.Collapsed;
        NowFocusFootnoteText.Visibility = Visibility.Collapsed;
        NowFocusStatusText.Visibility = Visibility.Collapsed;
    }

    private void PopulateTodayOverloadCard(
        TodayTrackingState todayState,
        LocalApiCurrentSnapshotResponse? snapshot,
        PortaVisualState visualState)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            NowCalibrationStatusText.Text = "Paused";
            NowCalibrationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            NowCalibrationProgressText.Text = "Regulation · paused";
            NowCalibrationQualityText.Text = "Today peak · —";
            NowCalibrationColdStartText.Text = "Confidence · —";
            NowCalibrationSummaryText.Text = "Pressure updates resume once tracking is unpaused.";
            NowCalibrationTrustCapText.Visibility = Visibility.Collapsed;
            return;
        }

        if (snapshot == null)
        {
            NowCalibrationStatusText.Text = _latestStatus == null || !_latestStatus.ServiceRunning || !_latestStatus.LocalApiAuthReady
                ? "n/a"
                : FillingInText;
            NowCalibrationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            NowCalibrationProgressText.Text = "Regulation · waiting for snapshot";
            NowCalibrationQualityText.Text = "Today peak · n/a";
            NowCalibrationColdStartText.Text = "Confidence · n/a";
            NowCalibrationSummaryText.Text = "Open Why when a fresh snapshot lands to inspect pressure reasoning.";
            NowCalibrationTrustCapText.Visibility = Visibility.Collapsed;
            return;
        }

        double pressureLevelNow = ResolvePressureLevel(snapshot);
        Color pressureColor = PressureColor(pressureLevelNow);

        if (snapshot.analysis_ready)
        {
            AnimateTopMetric(
                metricKey: "today_pressure_level",
                targetValue: Math.Max(0d, pressureLevelNow * 100d),
                applyValue: value =>
                {
                    NowCalibrationStatusText.Text = $"{value:F0}%";
                    NowCalibrationStatusText.Foreground = new SolidColorBrush(PressureColor(value / 100d));
                });
        }
        else
        {
            NowCalibrationStatusText.Text = $"{(pressureLevelNow * 100d):F0}%";
            NowCalibrationStatusText.Foreground = new SolidColorBrush(pressureColor);
        }

        bool overloadActive = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
        IReadOnlyList<LocalApiCurrentSnapshotResponse> todaySnapshots = _todayTimeline?.Snapshots ?? [];
        string peakText = todaySnapshots.Count == 0
            ? "n/a"
            : $"{(todaySnapshots.Max(ResolvePressureLevel) * 100d):F0}%";

        NowCalibrationProgressText.Text = string.Empty; // hidden in XAML — "Regulation · X" is developer text
        NowCalibrationQualityText.Text = $"Today peak · {peakText}";
        NowCalibrationColdStartText.Text = string.Empty; // hidden in XAML
        NowCalibrationSummaryText.Text = BuildNowOverloadSummary(snapshot);
        NowCalibrationTrustCapText.Visibility = snapshot.analysis_ready ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Color PressureColor(double level) =>
        level >= 0.80 ? Color.FromRgb(0xEF, 0x44, 0x44) :   // red  > 80%
        level >= 0.65 ? Color.FromRgb(0xF5, 0x9E, 0x0B) :   // amber 65–80%
        Color.FromRgb(0x64, 0x74, 0x8B);                     // grey  < 65%

    private void PopulateTodaySupportingStatusLine(
        TodayTrackingState todayState,
        string currentStatusText,
        string readableExplanation)
    {
        // The status line is removed per spec — the Ari bubble communicates state instead.
        // NowWaitingDataText is kept in XAML (Visibility=Collapsed) for code-behind compat.
        _ = todayState; _ = currentStatusText; _ = readableExplanation;
        NowWaitingDataText.Visibility = Visibility.Collapsed;
    }

    private void PopulateTodayRecoveryBanner(
        TodayTrackingState todayState,
        string readableExplanation,
        PortaVisualState visualState)
    {
        string? bannerText = null;
        long bannerEventId = 0;
        bool allowDismiss = false;

        // Only show the banner for genuine recovery events (e.g. capture channel restored).
        // Status messages like "Today is showing the last confirmed update..." are suppressed entirely.
        _ = todayState; _ = readableExplanation;
        if (visualState.ShowRecoveryBanner
            && visualState.RecoveryEventId > 0
            && visualState.RecoveryEventId != _dismissedRecoveryEventId)
        {
            bannerText = visualState.RecoveryBannerText;
            bannerEventId = visualState.RecoveryEventId;
            allowDismiss = true;
        }

        _activeRecoveryEventId = bannerEventId;
        NowRecoveryDismissButton.Visibility = allowDismiss ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(bannerText))
        {
            NowRecoveryBannerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        NowRecoveryBannerText.Text = bannerText;
        NowRecoveryBannerBorder.Visibility = Visibility.Visible;
    }

    private void PopulateCurrentSessionCard(
        TodayTrackingState todayState,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            CurrentSessionValueText.Text = "Paused";
            CurrentSessionValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69));
            CurrentSessionSubText.Text = string.Empty;
            return;
        }

        bool fresh = IsLiveSessionFresh(liveSession);
        if (!fresh || liveSession == null)
        {
            CurrentSessionValueText.Text = "—";
            CurrentSessionValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            CurrentSessionSubText.Text = "Connecting";
            return;
        }

        if (liveSession.idle)
        {
            CurrentSessionValueText.Text = "Away";
            CurrentSessionValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            CurrentSessionSubText.Text = liveSession.last_input_age_seconds.HasValue
                ? $"{FormatAgeLabel(liveSession.last_input_age_seconds)} idle"
                : "Idle";
        }
        else
        {
            CurrentSessionValueText.Text = "Focused now";
            CurrentSessionValueText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84));
            string typing = liveSession.keyboard_active ? "typing active" : "typing idle";
            string mouse = liveSession.mouse_active ? "mouse active" : "mouse idle";
            CurrentSessionSubText.Text = $"{typing} · {mouse}";
        }
    }

    /// <summary>Fills the three progress bars on Work Today, Focus Score, and Pressure cards.</summary>
    private void UpdateCardProgressBars(
        TodayPageMetrics todayMetrics,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        // Work Today bar — fill = totalWorkMinutes / 480 (8-hour target), capped at 100%
        double workMinutes = todayMetrics.HasProcessedTimeline
            ? Math.Max(0d, todayMetrics.DisplayedTrackedTodaySeconds / 60d)
            : 0d;
        double workFraction = Math.Clamp(workMinutes / 480d, 0d, 1d);
        ApplyBarFill(WorkTodayProgressFill, WorkTodayTrack, workFraction,
            Color.FromRgb(0x3B, 0x82, 0xF6));

        // Focus Score bar — fill = TaskContinuityScore (0–1)
        double focusScore = snapshot?.analysis_ready == true
            && double.IsFinite(snapshot.TaskContinuityScore)
            ? Math.Clamp(snapshot.TaskContinuityScore, 0d, 1d)
            : 0d;
        ApplyBarFill(FocusScoreProgressFill, FocusScoreTrack, focusScore,
            Color.FromRgb(0x16, 0xC7, 0x84));

        // Pressure bar — fill = pressure level, colour by threshold
        double pressure = snapshot != null ? ResolvePressureLevel(snapshot) : 0d;
        Color pressureBarColor = pressure >= 0.80d
            ? Color.FromRgb(0xEF, 0x44, 0x44)
            : pressure >= 0.65d
                ? Color.FromRgb(0xF5, 0x9E, 0x0B)
                : Color.FromRgb(0x64, 0x74, 0x8B);
        ApplyBarFill(PressureProgressFill, PressureTrack, Math.Clamp(pressure, 0d, 1d),
            pressureBarColor);
    }

    private void RefreshSessionTimelineBar()
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateTimeOffset dayStart = LocalTimelineRangeResolver.ResolveLocalDayStartUtc(nowUtc);
        HistoryFocusHeatmap heatmap = HistoryFocusHeatmapBuilder.Build(
            _todayTimeline,
            fallbackFromUtc: dayStart,
            fallbackToUtc: nowUtc);

        SessionTimelineBar.ColumnDefinitions.Clear();
        SessionTimelineBar.Children.Clear();

        if (!heatmap.HasAnyTracking || !heatmap.Episodes.Any())
        {
            SessionTimelineBar.Children.Add(new System.Windows.Controls.Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x35)),
                CornerRadius = new System.Windows.CornerRadius(4)
            });
            return;
        }

        var dummy = new System.Windows.Controls.TextBlock();
        RenderDayTimelineBar(heatmap.Episodes, SessionTimelineBar, dummy, height: 22d);
    }

    private static void ApplyBarFill(Border fill, Border track, double fraction, Color colour)
    {
        fill.Background = new SolidColorBrush(colour);
        double trackWidth = track.ActualWidth;
        if (trackWidth > 0d)
        {
            fill.Width = fraction > 0d ? Math.Max(2d, trackWidth * fraction) : 0d;
        }
        else
        {
            // ActualWidth not measured yet — defer via SizeChanged so bar fills after layout
            fill.Width = 0d;
            void OnSized(object? s, SizeChangedEventArgs e)
            {
                track.SizeChanged -= OnSized;
                fill.Width = fraction > 0d ? Math.Max(2d, track.ActualWidth * fraction) : 0d;
            }
            track.SizeChanged += OnSized;
        }
    }

    private string BuildTodaySummarySentence(
        TodayTrackingState todayState,
        TodayPageMetrics todayMetrics,
        double? focusEpisodeSeconds,
        string readableExplanation)
    {
        // Conversational Ari sentence — varies by real state. Colleague tone, not system language.
        if (todayState == TodayTrackingState.Paused)
            return "Tracking is paused — pick up where you left off when you're ready.";

        if (todayState == TodayTrackingState.Unavailable)
            return "Can't reach the local service right now — check the System page if this persists.";

        LocalApiCurrentSnapshotResponse? snap = _lastSnapshot;
        bool analysisReady = snap?.analysis_ready == true;
        double pressureLevel = snap != null ? ResolvePressureLevel(snap) : 0d;
        int hour = DateTime.Now.Hour;

        // 1. Overload active right now — most urgent
        if (analysisReady && snap!.OverloadActive)
            return "You're in overload right now — step away for a few minutes if you can.";

        // 2. Break pending — Ari already knows one is needed
        if (analysisReady && snap!.BreakPendingAction != null)
            return "Ari thinks you've earned a break. Even 5 minutes off will help you come back sharper.";

        // 3. No timeline yet — session just started or data warming up
        if (!todayMetrics.HasProcessedTimeline)
        {
            return hour < 10
                ? "Morning is just getting started — Ari is learning your patterns for today."
                : "Ari is warming up — your work stats will appear shortly.";
        }

        string workText = FormatReadableDuration(todayMetrics.DisplayedTrackedTodaySeconds);
        double trackedSec = todayMetrics.TrackedTodaySeconds;
        double distractionRatio = trackedSec > 0 ? todayMetrics.DistractionTodaySeconds / trackedSec : 0d;
        double focusRatio = trackedSec > 0 ? todayMetrics.FocusedTodaySeconds / trackedSec : 0d;

        // 4. Very short session — still warming up
        if (todayMetrics.DisplayedTrackedTodaySeconds < 900)
        {
            return hour < 10
                ? $"Good morning — you're {workText} in. Ari is tracking."
                : $"You're {workText} in — Ari is building your picture for today.";
        }

        // 5. Deep work active — celebrate and protect it
        if (analysisReady && snap!.DeepWorkActive)
        {
            double streakSec = snap.SustainedAttentionDurationSec;
            if (streakSec >= 2700) // 45+ min streak
            {
                string streakStr = FormatReadableDuration(streakSec);
                return $"You're in deep work — {streakStr} streak and counting. Don't stop now.";
            }
            return $"You're in deep work right now after {workText} today. Keep that window protected.";
        }

        // 6. High pressure — suggest recovery
        if (analysisReady && pressureLevel >= 0.80)
            return $"You've been at it for {workText} and pressure is climbing — a short break now will reset your focus clock.";

        // 7. High fatigue signal
        if (analysisReady && snap!.FatigueProbability >= 0.70)
            return $"You've been working {workText} — fatigue is creeping in. Consider stepping away for a few minutes.";

        // 8. High distraction ratio — give concrete feedback
        if (distractionRatio >= 0.35)
        {
            int distrPct = (int)(distractionRatio * 100);
            return $"You've been working {workText} but about {distrPct}% has been distraction time — narrowing your focus window could help.";
        }

        // 9. Solid focus day — positive reinforcement
        if (focusRatio >= 0.60 && (!analysisReady || pressureLevel < 0.65))
        {
            int focusPctVal = (int)(focusRatio * 100);
            return $"You've been working {workText} with {focusPctVal}% in focus — solid day so far.";
        }

        // 10. Evening wind-down
        if (hour >= 18)
            return $"You've put in {workText} today. Good time to wrap up and let things settle overnight.";

        // 11. Afternoon slump window — proactive fatigue nudge
        if (hour >= 14 && hour < 17 && analysisReady && snap!.FatigueProbability >= 0.45)
            return $"You've been working {workText} — this is usually when fatigue peaks. A 10-minute break now pays off later.";

        // 12. Default with pressure context
        if (analysisReady)
        {
            if (pressureLevel >= 0.65)
                return $"You've been working {workText} — watch the pressure, it's edging up.";
            if (pressureLevel < 0.40)
                return $"You've been working {workText} and you're in a good zone — keep the rhythm.";
            return $"You've been working {workText}. Pressure is steady — keep it going.";
        }

        return $"You've been working {workText} — Ari is still calibrating your pressure signal.";
    }

    private string BuildTodayWorkStartText(TodayPageMetrics todayMetrics)
    {
        // "Since 8:43 AM" — no history status language
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshots.Count > 0)
        {
            DateTimeOffset first = snapshots[0].TimestampUtc.ToLocalTime();
            return $"Since {first:h:mm tt}";
        }

        return string.Empty;
    }

    private static string BuildTodayFocusedNowLabel(
        LocalApiLiveSessionStateResponse? liveSession,
        double? focusEpisodeSeconds)
    {
        if (!IsLiveSessionFresh(liveSession))
        {
            return string.Empty;
        }

        if (liveSession!.idle)
        {
            return "Episode: idle now";
        }

        if (focusEpisodeSeconds.HasValue && focusEpisodeSeconds.Value > 0d)
        {
            return "Episode: active";
        }

        return liveSession.user_active
            ? "Episode: starting"
            : "Episode: idle now";
    }

    private static string BuildTodayCurrentFocusSupportText(LocalApiLiveSessionStateResponse? liveSession)
    {
        // Friendly subtext: no internal state language, no "Episode: X"
        if (!IsLiveSessionFresh(liveSession))
        {
            return "Building baseline";
        }

        if (liveSession!.idle)
        {
            return liveSession.last_input_age_seconds.HasValue
                ? $"Away · {FormatAgeLabel(liveSession.last_input_age_seconds)}"
                : "Away";
        }

        return liveSession.keyboard_active ? "Typing active" : "Active now";
    }

    private static string BuildTodayCurrentFocusSummary(
        TodayTrackingState todayState,
        LocalApiLiveSessionStateResponse? liveSession,
        PortaVisualState visualState)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            return "This resumes from the current focus episode after tracking is unpaused.";
        }

        if (visualState.FocusEpisodeSeconds.HasValue)
        {
            return "This keeps the same focus episode through short support switches and brief thinking pauses.";
        }

        if (IsLiveSessionFresh(liveSession) && liveSession!.idle)
        {
            return "Live presence is idle right now. The next durable focus episode will appear after you return.";
        }

        if (!visualState.SnapshotAnalysisReady)
        {
            return "Live capture is connected, and the next analyzed block will pin this focus episode into History.";
        }

        return "This follows the same focus episode shown in the widget.";
    }

    private string BuildTodayHistoryStatusText()
    {
        LocalApiCurrentSnapshotResponse? latestProcessedSnapshot = _todayTimeline?.Snapshots.LastOrDefault();
        if (latestProcessedSnapshot == null)
        {
            return "History status: waiting for processed buckets";
        }

        return $"History status: Updated {latestProcessedSnapshot.TimestampUtc.ToLocalTime():h:mm tt}";
    }

    private string BuildNowOverloadSummary(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot is { analysis_ready: false })
        {
            return $"Current pressure: {(ResolvePressureLevel(snapshot) * 100d):F0}%. Confidence is still settling; Click for Why once the next processed bucket lands.";
        }

        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshots.Count == 0)
        {
            return snapshot == null
                ? "Open Why for current contributors as soon as live overload data arrives."
                : $"Current pressure: {(ResolvePressureLevel(snapshot) * 100d):F0}%. Click for Why.";
        }

        string peakText = $"{(snapshots.Max(ResolvePressureLevel) * 100d):F0}%";
        string volatileHour = ComputeMostVolatileHour(snapshots);
        return volatileHour == NotAvailableText
            ? $"Today peak: {peakText}. Click for Why."
            : $"Today peak: {peakText} | Volatile hour: {volatileHour}. Click for Why.";
    }

    private string BuildNowFocusSummary(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        double? focusEpisodeSeconds = VisualStateAdapter.ResolveFocusEpisodeSeconds(snapshot, liveSession);
        if (IsLiveSessionFresh(liveSession)
            && focusEpisodeSeconds.HasValue
            && snapshot is { analysis_ready: false })
        {
            return $"Last focus session: {FormatFocusedTime(focusEpisodeSeconds.Value)}. Click for History once processed buckets catch up.";
        }

        if (snapshot is { analysis_ready: false })
        {
            return "Watching your live activity now. History will fill in after processed buckets arrive.";
        }

        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshots.Count == 0)
        {
            return snapshot == null
                ? "Open History once live focus buckets arrive."
                : $"Last focus session: {FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot))}. Click for History.";
        }

        return focusEpisodeSeconds.HasValue
            ? "This follows the same last focus session shown above and in the widget. Click for History."
            : "Open History to review the day-by-hour focus map once the next focus episode is processed.";
    }

    private string BuildNowCalibrationSummary(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot == null)
        {
            return "Open System for live calibration blockers.";
        }

        string confidenceLabel = ToConfidenceLabel(NormalizeTrustTier(snapshot.TrustTier, snapshot.ModelConfidence ?? snapshot.Confidence));
        string systemActions = snapshot.SystemActionsEnabled == true ? "enabled" : "gated";
        string progressText = snapshot.BaselineReadyPct.HasValue
            ? $"{(Math.Clamp(snapshot.BaselineReadyPct.Value, 0d, 1d) * 100d):F0}% ready"
            : "progress pending";
        return $"Confidence: {confidenceLabel} | System actions: {systemActions} | {progressText}. Click for System.";
    }

    private string BuildNowDataQualitySummary(LocalApiCurrentSnapshotResponse? snapshot)
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshot?.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join(" / ", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"Tracking is limited now: missing {missing}. Click for tracking details.";
        }

        if (snapshot?.PartialData == true)
        {
            return TryBuildTodayQualityRange(snapshots, out string range)
                ? $"Recent buckets are partial | Today's tracking quality range: {range}. Click for tracking details."
                : "Recent buckets are partial, so tracking quality is conservative. Click for tracking details.";
        }

        if (TryBuildTodayQualityRange(snapshots, out string todayRange))
        {
            return $"Today's tracking quality range: {todayRange}. Click for tracking details.";
        }

        if (snapshot == null)
        {
            return "Open System once live tracking-quality buckets arrive.";
        }

        return "All major tracking signals are currently available. Click for tracking details.";
    }

    private static bool TryBuildTodayQualityRange(
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        out string range)
    {
        List<double> qualityValues = snapshots
            .Where(static snapshot => snapshot.CurrentQualityScore.HasValue)
            .Select(static snapshot => snapshot.CurrentQualityScore!.Value)
            .ToList();

        if (qualityValues.Count == 0)
        {
            range = string.Empty;
            return false;
        }

        double min = qualityValues.Min();
        double max = qualityValues.Max();
        range = Math.Abs(min - max) < 0.005d
            ? $"{max:F2}"
            : $"{min:F2}-{max:F2}";
        return true;
    }

    private void PopulateWhyDrawer(
        NowTruth nowTruth,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        PortaVisualState visualState)
    {
        WhyDrawerTitleText.Text = $"Why ({visualState.ModeLabel})";
        LocalApiCurrentSnapshotResponse? snapshot = nowTruth.Snapshot;
        WhyDrawerStateText.Text = $"State: {visualState.ModeLabel}";
        WhyDrawerConfidenceText.Text = snapshot == null
            ? $"Confidence: {NotAvailableText}"
            : $"Confidence: {(snapshot.Confidence * 100d):F0}% ({visualState.ConfidenceLabel})";

        OverloadExplainView explain = snapshot == null
            ? OverloadExplainView.Unavailable
            : ParseOverloadExplain(snapshot.ContributorsJson);
        WhyDrawerFivText.Text = BuildWhyDrawerFivText(explain);
        string topContributors = explain.HasStructuredData
            ? FormatTopReasons(explain.TopReasons, explainMode: false)
            : NotAvailableText;
        WhyDrawerContributorsText.Text = $"Top contributors: {topContributors}";

        WhyDrawerPolicyText.Text = BuildWhyDrawerPolicyText(snapshot, visualState);
        WhyDrawerCooldownText.Text = $"Cooldowns: {BuildWhyDrawerCooldownText(_latestStatus, recentEvents)}";
        WhyDrawerActionStateText.Text = BuildWhyDrawerActionStateText(_latestStatus);

        string? contributorsJson = _latestStatus?.LatestContributorsJson ?? snapshot?.ContributorsJson;
        WhyDrawerContributorsSummaryText.Text = BuildContributorsStructuredSummary(contributorsJson, explain);
        LocalApiEventResponse? latestEvent = recentEvents
            .OrderByDescending(static ev => ev.TsUtc)
            .ThenByDescending(static ev => ev.Id)
            .FirstOrDefault();
        WhyDrawerWhySummaryText.Text = BuildWhyStructuredSummary(_latestStatus?.LatestWhyJson ?? latestEvent?.WhyJson);
    }

    private static string BuildWhyDrawerFivText(OverloadExplainView explain)
    {
        if (!explain.Clarity.HasValue || !explain.Pressure.HasValue || !explain.Jitter.HasValue)
        {
            return $"Clarity/Pressure/Jitter: {NotAvailableText}";
        }

        return $"Clarity (F): {explain.Clarity.Value:F2} ({ToBandLabel(explain.Clarity.Value)})"
            + $"{Environment.NewLine}Pressure (I): {explain.Pressure.Value:F2} ({ToBandLabel(explain.Pressure.Value)})"
            + $"{Environment.NewLine}Jitter (V): {explain.Jitter.Value:F2} ({ToBandLabel(explain.Jitter.Value)})";
    }

    private static string BuildWhyDrawerPolicyText(
        LocalApiCurrentSnapshotResponse? snapshot,
        PortaVisualState visualState)
    {
        string overload = visualState.OverloadActive ? "ON" : "OFF";
        string deepWork = visualState.DeepWorkActive ? "ON" : "OFF";
        string stabilization = BuildStabilizationText(snapshot?.StateFlagsJson, explainMode: false);
        return $"overload_active: {overload}{Environment.NewLine}deep_work_active: {deepWork}{Environment.NewLine}{stabilization}";
    }

    private static string BuildWhyDrawerCooldownText(
        LocalApiStatusResponse? status,
        IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        var parts = new List<string>();
        if (status?.NotificationSuppressionBackoffUntilUtc is DateTimeOffset notifyUntil
            && notifyUntil > DateTimeOffset.UtcNow)
        {
            parts.Add($"notifications backoff {FormatCooldownRemaining(notifyUntil)}");
        }

        if (status?.BrightnessBackoffUntilUtc is DateTimeOffset brightnessUntil
            && brightnessUntil > DateTimeOffset.UtcNow)
        {
            parts.Add($"brightness backoff {FormatCooldownRemaining(brightnessUntil)}");
        }

        DateTimeOffset? recentCooldownEnd = recentEvents
            .Where(static ev => ev.CooldownEndTimestampUtc.HasValue)
            .OrderByDescending(static ev => ev.CooldownEndTimestampUtc)
            .Select(static ev => ev.CooldownEndTimestampUtc)
            .FirstOrDefault();
        if (recentCooldownEnd.HasValue && recentCooldownEnd.Value > DateTimeOffset.UtcNow)
        {
            parts.Add($"event cooldown {FormatCooldownRemaining(recentCooldownEnd)}");
        }

        return parts.Count == 0 ? "none" : string.Join(" | ", parts);
    }

    private static string BuildWhyDrawerActionStateText(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return NotAvailableText;
        }

        return $"Notifications suppressed: {MapYesNo(status.NotificationSuppressionActive)}"
            + $"{Environment.NewLine}Brightness reduced: {MapYesNo(status.BrightnessActionActive)}";
    }

    private static string BuildContributorsStructuredSummary(string? contributorsJson, OverloadExplainView explain)
    {
        if (string.IsNullOrWhiteSpace(contributorsJson))
        {
            return "Contributors data: Not available";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(contributorsJson);
            JsonElement root = doc.RootElement;
            var parts = new List<string>();
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schema_ver", out JsonElement schemaElem)
                && schemaElem.ValueKind == JsonValueKind.Number
                && schemaElem.TryGetInt32(out int schemaVer))
            {
                parts.Add($"schema v{schemaVer}");
            }

            if (TryReadDouble(root, "quality", out double quality))
            {
                parts.Add($"quality {quality:F2}");
            }

            if (TryReadDouble(root, "agreement", out double agreement))
            {
                parts.Add($"agreement {agreement:F2}");
            }

            if (explain.HasStructuredData)
            {
                parts.Add($"top {FormatTopReasons(explain.TopReasons, explainMode: false)}");
            }

            return parts.Count == 0
                ? "Contributors data: available"
                : $"Contributors data: {string.Join(" | ", parts)}";
        }
        catch
        {
            return "Contributors data: unavailable (invalid payload)";
        }
    }

    private static string BuildWhyStructuredSummary(string? whyJson)
    {
        if (string.IsNullOrWhiteSpace(whyJson))
        {
            return "Why data: Not available";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(whyJson);
            JsonElement root = doc.RootElement;
            var parts = new List<string>();
            if (TryReadStringProperty(root, "policy", out string? policy))
            {
                parts.Add($"policy {policy}");
            }

            if (TryReadStringProperty(root, "action", out string? action))
            {
                parts.Add($"action {action}");
            }

            if (TryReadStringProperty(root, "reason", out string? reason))
            {
                parts.Add(reason!);
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("outcome", out JsonElement outcome)
                && outcome.ValueKind == JsonValueKind.Object)
            {
                if (TryReadStringProperty(outcome, "reason", out string? outcomeReason))
                {
                    parts.Add($"outcome {outcomeReason}");
                }
                else if (TryReadStringProperty(outcome, "error_code", out string? errorCode))
                {
                    parts.Add($"error {errorCode}");
                }
            }

            return parts.Count == 0
                ? "Why data: available"
                : $"Why data: {string.Join(" | ", parts.Take(4))}";
        }
        catch
        {
            return "Why data: unavailable (invalid payload)";
        }
    }

    private static bool TryReadStringProperty(JsonElement parent, string propertyName, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(propertyName, out JsonElement prop))
        {
            return false;
        }

        if (prop.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = prop.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private void AnimateTopMetric(string metricKey, double targetValue, Action<double> applyValue)
    {
        targetValue = Math.Max(0d, targetValue);
        if (!_topMetricAnimationLastValues.TryGetValue(metricKey, out double startValue))
        {
            _topMetricAnimationLastValues[metricKey] = targetValue;
            applyValue(targetValue);
            return;
        }

        if (Math.Abs(startValue - targetValue) < 0.05d)
        {
            _topMetricAnimationLastValues[metricKey] = targetValue;
            applyValue(targetValue);
            return;
        }

        if (_topMetricAnimationTimers.TryGetValue(metricKey, out DispatcherTimer? runningTimer))
        {
            runningTimer.Stop();
        }

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var timer = new DispatcherTimer { Interval = TopMetricAnimationFrameInterval };
        timer.Tick += (_, _) =>
        {
            double elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;
            double t = Math.Clamp(elapsedMs / TopMetricAnimationDuration.TotalMilliseconds, 0d, 1d);
            double eased = 1d - ((1d - t) * (1d - t));
            double current = startValue + ((targetValue - startValue) * eased);
            applyValue(current);
            if (t >= 1d)
            {
                timer.Stop();
                _topMetricAnimationLastValues[metricKey] = targetValue;
                applyValue(targetValue);
            }
        };

        _topMetricAnimationTimers[metricKey] = timer;
        timer.Start();
    }

    private static string FormatFocusedTime(double sustainedAttentionSeconds)
    {
        if (sustainedAttentionSeconds < 60d)
        {
            return $"{Math.Max(0d, sustainedAttentionSeconds):F0} sec";
        }

        double minutes = Math.Floor(sustainedAttentionSeconds / 60d);
        return $"{minutes:F0} min";
    }

    internal static string ResolveTodayWorkMetricText(
        TodayTrackingState todayState,
        TodayPageMetrics todayMetrics,
        LocalApiStatusResponse? status)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            return "Paused";
        }

        if (todayState == TodayTrackingState.Unavailable)
        {
            return "n/a";
        }

        if (todayMetrics.HasProcessedTimeline)
        {
            return LocalMetricDurationFormatter.FormatMetricDuration(todayMetrics.DisplayedTrackedTodaySeconds);
        }

        return status == null || !status.ServiceRunning || !status.LocalApiAuthReady
            ? "n/a"
            : FillingInText;
    }

    internal static string ResolveTodayLastFocusMetricText(
        TodayTrackingState todayState,
        double? focusEpisodeSeconds,
        bool liveSessionFresh,
        LocalApiStatusResponse? status)
    {
        if (todayState == TodayTrackingState.Paused)
        {
            return "Paused";
        }

        if (focusEpisodeSeconds.HasValue)
        {
            return LocalMetricDurationFormatter.FormatMetricDuration(focusEpisodeSeconds.Value);
        }

        if (liveSessionFresh)
        {
            return "0s";
        }

        return status == null || !status.ServiceRunning || !status.LocalApiAuthReady
            ? "n/a"
            : FillingInText;
    }

    private static string FormatReadableDuration(double seconds)
    {
        double clampedSeconds = Math.Max(0d, seconds);
        if (clampedSeconds < 60d)
        {
            return $"{Math.Round(clampedSeconds, MidpointRounding.AwayFromZero):F0} sec";
        }

        int wholeMinutes = (int)Math.Floor(clampedSeconds / 60d);
        if (wholeMinutes < 60)
        {
            return $"{wholeMinutes} min";
        }

        int hours = wholeMinutes / 60;
        int minutes = wholeMinutes % 60;
        return minutes == 0
            ? $"{hours}h"
            : $"{hours}h {minutes}m";
    }

    private static string MapChoppiness(double idleFragmentationIndex)
    {
        if (idleFragmentationIndex < 0.20d)
        {
            return "Low";
        }

        if (idleFragmentationIndex < 0.45d)
        {
            return "Medium";
        }

        return "High";
    }

    private static string BuildFocusStatusMessage(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (snapshot.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"Focus session paused: missing {missing} signals.";
        }

        if (snapshot.PartialData == true)
        {
            return "Focus session timing is conservative while data is partial.";
        }

        if (snapshot.SustainedAttentionDurationSec <= 0.5d)
        {
            return "No focus episode detected yet.";
        }

        return string.Empty;
    }

    private static string MapOnOff(bool? value)
    {
        if (!value.HasValue)
        {
            return NotAvailableText;
        }

        return value.Value ? "On" : "Off";
    }

    private static string MapYesNo(bool? value)
    {
        if (!value.HasValue)
        {
            return NotAvailableText;
        }

        return value.Value ? "Yes" : "No";
    }

    private static string ToDisplayChannelName(string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "keyboard" => "Keyboard",
            "mouse" => "Mouse",
            "focus" => "Focus",
            "idle" => "Idle",
            _ => channel
        };
    }

    private static string FormatCaptureContextLabel(string? captureContext)
    {
        return captureContext switch
        {
            "UserSession" => "User session",
            "ServiceSession" => "Service session",
            "Unknown" => "Unknown",
            _ => NotAvailableText
        };
    }

    private static string FormatCaptureSignalLabel(bool signalActive, bool statusFresh)
    {
        if (!statusFresh)
        {
            return "Stale";
        }

        return signalActive ? "Active" : "No recent signal";
    }

    private static string FormatCaptureLastInputLabel(DateTimeOffset? lastInputUtc)
    {
        return lastInputUtc.HasValue
            ? lastInputUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : NotAvailableText;
    }

    private static string FormatAgeLabel(double? ageSeconds)
    {
        if (!ageSeconds.HasValue)
        {
            return NotAvailableText;
        }

        double seconds = Math.Max(0d, ageSeconds.Value);
        if (seconds < 60d)
        {
            return $"{seconds:F0}s";
        }

        if (seconds < 3600d)
        {
            return $"{seconds / 60d:F1}m";
        }

        return $"{seconds / 3600d:F1}h";
    }

    private static bool IsLiveSessionFresh(LocalApiLiveSessionStateResponse? liveSession)
    {
        return liveSession != null
            && string.Equals(liveSession.freshness_state, "fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildLiveContextLabel(string? captureContext, LocalApiLiveSessionStateResponse? liveSession)
    {
        string contextLabel = FormatCaptureContextLabel(captureContext);
        if (!IsLiveSessionFresh(liveSession))
        {
            return contextLabel;
        }

        return liveSession!.session_elapsed_seconds.HasValue
            ? $"{contextLabel} | live {FormatAgeLabel(liveSession.session_elapsed_seconds)}"
            : $"{contextLabel} | live";
    }

    private static string BuildLiveActivitySummary(LocalApiLiveSessionStateResponse liveSession)
    {
        // This helper intentionally stays qualitative. Numeric modeled state belongs to snapshot/status.
        string sessionText = liveSession.session_elapsed_seconds.HasValue
            ? $"session {FormatAgeLabel(liveSession.session_elapsed_seconds)}"
            : "session n/a";
        string lastInputText = liveSession.last_input_age_seconds.HasValue
            ? $"last input {FormatAgeLabel(liveSession.last_input_age_seconds)}"
            : "last input n/a";

        if (liveSession.idle)
        {
            return $"Live session is idle ({FormatAgeLabel(liveSession.idle_seconds)}) | {lastInputText} | {sessionText}";
        }

        string focusText = liveSession.user_active
            ? "active now"
            : "watching live";
        string typingText = liveSession.keyboard_active ? "typing active" : "typing idle";
        string mouseText = liveSession.mouse_active ? "mouse active" : "mouse idle";
        return $"{focusText} | {typingText} | {mouseText} | {lastInputText} | {sessionText}";
    }

    private static string BuildLiveSignalsCurrentText(LocalApiLiveSessionStateResponse liveSession)
    {
        string sessionText = liveSession.session_elapsed_seconds.HasValue
            ? $"session {FormatAgeLabel(liveSession.session_elapsed_seconds)}"
            : "session n/a";
        string lastInputText = liveSession.last_input_age_seconds.HasValue
            ? $"last input {FormatAgeLabel(liveSession.last_input_age_seconds)}"
            : "last input n/a";

        if (liveSession.idle)
        {
            return $"Current status: typing idle, mouse idle, idle {FormatAgeLabel(liveSession.idle_seconds)}, {lastInputText}, {sessionText}.";
        }

        string focusText = liveSession.user_active ? "active now" : "watching";
        return $"Current status: typing {(liveSession.keyboard_active ? "active" : "idle")}, mouse {(liveSession.mouse_active ? "active" : "idle")}, {focusText}, {lastInputText}, {sessionText}.";
    }

    private static string? BuildLiveSessionFocusStatus(LocalApiLiveSessionStateResponse? liveSession)
    {
        if (!IsLiveSessionFresh(liveSession))
        {
            return null;
        }

        if (liveSession!.idle)
        {
            string lastInputText = liveSession.last_input_age_seconds.HasValue
                ? $"last input {FormatAgeLabel(liveSession.last_input_age_seconds)}"
                : "last input n/a";
            return $"Idle now | {lastInputText}";
        }

        string focusText = liveSession.user_active
            ? "Active now"
            : "Watching live";
        return $"{focusText} | typing {(liveSession.keyboard_active ? "active" : "idle")} | mouse {(liveSession.mouse_active ? "active" : "idle")}";
    }

    private static string? BuildCurrentStatusSentence(LocalApiLiveSessionStateResponse? liveSession)
    {
        if (!IsLiveSessionFresh(liveSession))
        {
            return null;
        }

        if (liveSession!.idle)
        {
            return $"Idle right now ({FormatAgeLabel(liveSession.idle_seconds)}).";
        }

        if (liveSession.keyboard_active && liveSession.mouse_active)
        {
            return "Typing and mouse activity detected right now.";
        }

        if (liveSession.keyboard_active)
        {
            return "Typing activity detected right now.";
        }

        if (liveSession.mouse_active)
        {
            return "Mouse activity detected right now.";
        }

        if (liveSession.user_active)
        {
            return "Active right now.";
        }

        return "Waiting for fresh live activity.";
    }

    private static string FormatHeartbeatLabel(DateTimeOffset? heartbeatUtc)
    {
        if (!heartbeatUtc.HasValue)
        {
            return NotAvailableText;
        }

        TimeSpan age = DateTimeOffset.UtcNow - heartbeatUtc.Value;
        string ageText = age.TotalSeconds < 60d
            ? $"{Math.Max(0, (int)Math.Round(age.TotalSeconds))}s ago"
            : age.TotalMinutes < 60d
                ? $"{Math.Max(0, (int)Math.Round(age.TotalMinutes))}m ago"
                : $"{Math.Max(0, (int)Math.Round(age.TotalHours))}h ago";
        return $"{heartbeatUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({ageText})";
    }

    private static string FormatUserAgentIdentity(string? instanceId, int? processId)
    {
        string left = string.IsNullOrWhiteSpace(instanceId) ? "None" : instanceId!;
        return processId.HasValue
            ? $"{left} | pid {processId.Value.ToString(CultureInfo.InvariantCulture)}"
            : left;
    }

    private static string FormatInputOwnerLabel(LocalApiStatusResponse status)
    {
        string instance = string.IsNullOrWhiteSpace(status.LastInputAgentInstanceId) ? "None" : status.LastInputAgentInstanceId!;
        string session = string.IsNullOrWhiteSpace(status.LastInputSessionId) ? "None" : status.LastInputSessionId!;
        return $"{instance} | {session}";
    }

    private static string FormatStartupSourceLabel(string? startupSource) =>
        UserAgentRecoveryCopy.FormatStartupSourceLabel(startupSource, NotAvailableText);

    private static string FormatLaunchReasonLabel(string? launchReason)
    {
        return launchReason?.Trim().ToLowerInvariant() switch
        {
            "startup" => "startup",
            "run-key" => "run key",
            "windows-restart" => "Windows restart",
            "awaiting_heartbeat" => "awaiting heartbeat",
            "stale_heartbeat" => "stale heartbeat",
            "missing_heartbeat" => "missing heartbeat",
            "session_switched" => "session switched",
            "" or null => NotAvailableText,
            _ => launchReason!
        };
    }

    private static string BuildRecoveryStateLabel(LocalApiStatusResponse status)
    {
        string heartbeatFailures = status.UserAgentHeartbeatWriteFailureCount.HasValue
            ? status.UserAgentHeartbeatWriteFailureCount.Value.ToString(CultureInfo.InvariantCulture)
            : "0";
        string remediations = status.CaptureSelfTestRemediationCount.HasValue
            ? status.CaptureSelfTestRemediationCount.Value.ToString(CultureInfo.InvariantCulture)
            : "0";
        string selfTestState = string.IsNullOrWhiteSpace(status.CaptureSelfTestState)
            ? "not reported"
            : status.CaptureSelfTestState!;
        string selfTestError = string.IsNullOrWhiteSpace(status.CaptureSelfTestErrorCode)
            ? "none"
            : status.CaptureSelfTestErrorCode!;
        return $"heartbeat write failures={heartbeatFailures} | self-test={selfTestState} | self-test error={selfTestError} | recovery attempts={remediations}";
    }

    private static EngineHealthView BuildEngineHealthView(
        ServiceProbeResult serviceProbe,
        bool apiReachable,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh,
        int? statusHttpCode)
    {
        string serviceLabel = BuildEngineServiceLabel(serviceProbe, status, apiReachable);
        string databaseLabel = BuildEngineDatabaseLabel(status);
        string snapshotLabel = BuildEngineSnapshotLabel(snapshot, statusFresh, snapshotFresh);
        string policyLabel = BuildEnginePolicyLabel(status);
        string apiLabel = apiReachable ? "Yes" : "No";
        string lastUpdateLabel = status == null
            ? NotAvailableText
            : status.LastUpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string lastErrorLabel = string.IsNullOrWhiteSpace(status?.LastError) ? "None" : status.LastError!;

        if (status == null)
        {
            return new EngineHealthView(
                StatusLabel: "Unavailable",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: BuildLocalApiConnectionExplanation(
                    serviceProbe,
                    runtimeSettings: null,
                    statusHttpCode,
                    apiReachable),
                StatusColor: Color.FromRgb(248, 113, 113),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!status.ServiceRunning)
        {
            return new EngineHealthView(
                StatusLabel: "Offline",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: "Service offline.",
                StatusColor: Color.FromRgb(248, 113, 113),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!status.LocalApiAuthReady)
        {
            return new EngineHealthView(
                StatusLabel: "Blocked",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: "API auth mismatch. Reopen Control Center or refresh local credentials.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!statusFresh)
        {
            return new EngineHealthView(
                StatusLabel: "Stale",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: BuildFreshnessExplanation(status, snapshot),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (status.DbMigrationFailed)
        {
            return new EngineHealthView(
                StatusLabel: "Warning",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: "Database startup or migration failed. Engine state is not trustworthy until storage is healthy.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (snapshot == null)
        {
            return new EngineHealthView(
                StatusLabel: "Watching",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: "Service alive, waiting for current snapshot.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!snapshotFresh)
        {
            return new EngineHealthView(
                StatusLabel: "Watching",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: BuildFreshnessExplanation(status, snapshot),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!status.ConsentActive)
        {
            return new EngineHealthView(
                StatusLabel: "Watching",
                ServiceLabel: serviceLabel,
                DatabaseLabel: databaseLabel,
                SnapshotLabel: snapshotLabel,
                PolicyLabel: policyLabel,
                ApiLabel: apiLabel,
                LastUpdateLabel: lastUpdateLabel,
                LastErrorLabel: lastErrorLabel,
                WarningText: "Engine is running, but policy execution is blocked until consent is granted.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        return new EngineHealthView(
            StatusLabel: "Healthy",
            ServiceLabel: serviceLabel,
            DatabaseLabel: databaseLabel,
            SnapshotLabel: snapshotLabel,
            PolicyLabel: policyLabel,
            ApiLabel: apiLabel,
            LastUpdateLabel: lastUpdateLabel,
            LastErrorLabel: lastErrorLabel,
            WarningText: "Service alive, status fresh.",
            StatusColor: Color.FromRgb(74, 222, 128),
            DetailColor: Color.FromRgb(148, 163, 184));
    }

    private static CaptureHealthView BuildCaptureHealthView(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        if (status == null)
        {
            return new CaptureHealthView(
                StatusLabel: "Unavailable",
                ContextLabel: NotAvailableText,
                KeyboardSignalLabel: NotAvailableText,
                MouseSignalLabel: NotAvailableText,
                LastInputLabel: NotAvailableText,
                WarningText: "Capture status is unavailable because the local API did not return a live status payload.",
                StatusColor: Color.FromRgb(248, 113, 113),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        string? captureFailureDetail = DescribeCaptureFailureReason(status.CaptureFailureReason);
        string? ownershipDetail = DescribeOwnershipWarning(status);
        bool liveFresh = IsLiveSessionFresh(liveSession);
        bool keyboardSignalActive = liveFresh ? liveSession!.KeyboardActive : statusFresh && status.KeyboardSignalActive;
        bool mouseSignalActive = liveFresh ? liveSession!.MouseActive : statusFresh && status.MouseSignalActive;
        string lastInputLabel = liveFresh
            ? FormatCaptureLastInputLabel(liveSession!.LastInputEventUtc)
            : FormatCaptureLastInputLabel(status.LastInputEventUtc);
        string contextLabel = BuildLiveContextLabel(status.CaptureContext, liveSession);

        if (!statusFresh)
        {
            return new CaptureHealthView(
                StatusLabel: "Stale",
                ContextLabel: contextLabel,
                KeyboardSignalLabel: FormatCaptureSignalLabel(keyboardSignalActive, liveFresh || statusFresh),
                MouseSignalLabel: FormatCaptureSignalLabel(mouseSignalActive, liveFresh || statusFresh),
                LastInputLabel: lastInputLabel,
                WarningText: BuildFreshnessExplanation(status, snapshot: null),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.IsNullOrWhiteSpace(ownershipDetail))
        {
            return new CaptureHealthView(
                StatusLabel: "Warning",
                ContextLabel: contextLabel,
                KeyboardSignalLabel: FormatCaptureSignalLabel(keyboardSignalActive, true),
                MouseSignalLabel: FormatCaptureSignalLabel(mouseSignalActive, true),
                LastInputLabel: lastInputLabel,
                WarningText: $"Ownership mismatch detected: {ownershipDetail}.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.IsNullOrWhiteSpace(captureFailureDetail))
        {
            return new CaptureHealthView(
                StatusLabel: "Warning",
                ContextLabel: contextLabel,
                KeyboardSignalLabel: FormatCaptureSignalLabel(keyboardSignalActive, true),
                MouseSignalLabel: FormatCaptureSignalLabel(mouseSignalActive, true),
                LastInputLabel: lastInputLabel,
                WarningText: UserAgentRecoveryCopy.BuildCaptureFailureWarningText(captureFailureDetail),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!keyboardSignalActive || !mouseSignalActive)
        {
            string missingSignals = BuildMissingSignalList(keyboardSignalActive, mouseSignalActive);
            return new CaptureHealthView(
                StatusLabel: "Watching",
                ContextLabel: contextLabel,
                KeyboardSignalLabel: FormatCaptureSignalLabel(keyboardSignalActive, true),
                MouseSignalLabel: FormatCaptureSignalLabel(mouseSignalActive, true),
                LastInputLabel: lastInputLabel,
                WarningText: $"Waiting for fresh {missingSignals} activity from the current user session. Single-channel activity is normal here and does not mean capture failed.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        return new CaptureHealthView(
            StatusLabel: "Healthy",
            ContextLabel: contextLabel,
            KeyboardSignalLabel: FormatCaptureSignalLabel(keyboardSignalActive, true),
            MouseSignalLabel: FormatCaptureSignalLabel(mouseSignalActive, true),
            LastInputLabel: lastInputLabel,
            WarningText: liveFresh
                ? BuildLiveActivitySummary(liveSession!)
                : "Signals are coming from the interactive user session.",
            StatusColor: Color.FromRgb(74, 222, 128),
            DetailColor: Color.FromRgb(148, 163, 184));
    }

    private static SessionTruthView BuildSessionTruthView(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        if (status == null)
        {
            return new SessionTruthView(
                StatusLabel: "Unavailable",
                ContextLabel: NotAvailableText,
                HeartbeatLabel: NotAvailableText,
                ObservedHeartbeatLabel: NotAvailableText,
                AgentLabel: NotAvailableText,
                SessionLabel: NotAvailableText,
                StartupSourceLabel: NotAvailableText,
                LaunchReasonLabel: NotAvailableText,
                RecoveryStateLabel: NotAvailableText,
                WarningText: "Session truth is unavailable because the local API did not return a live status payload.",
                StatusColor: Color.FromRgb(248, 113, 113),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        string contextLabel = BuildLiveContextLabel(status.CaptureContext, liveSession);
        string heartbeatLabel = FormatHeartbeatLabel(status.UserAgentHeartbeatUpdatedAtUtc);
        string observedHeartbeatLabel = FormatHeartbeatLabel(status.UserAgentObservedHeartbeatUpdatedAtUtc);
        string agentLabel = FormatUserAgentIdentity(status.UserAgentInstanceId, status.UserAgentProcessId);
        string sessionLabel = string.IsNullOrWhiteSpace(status.UserAgentSessionId) ? NotAvailableText : status.UserAgentSessionId!;
        string startupSourceLabel = FormatStartupSourceLabel(status.UserAgentStartupSource);
        string launchReasonLabel = FormatLaunchReasonLabel(status.UserAgentLaunchReason);
        string recoveryStateLabel = BuildRecoveryStateLabel(status);
        if (IsLiveSessionFresh(liveSession) && liveSession!.session_elapsed_seconds.HasValue)
        {
            recoveryStateLabel = $"{recoveryStateLabel} | live {FormatAgeLabel(liveSession.session_elapsed_seconds)}";
        }
        string? ownershipDetail = DescribeOwnershipWarning(status);
        string? captureFailureDetail = DescribeCaptureFailureReason(status.CaptureFailureReason);

        if (!statusFresh)
        {
            return new SessionTruthView(
                StatusLabel: "Stale",
                ContextLabel: contextLabel,
                HeartbeatLabel: heartbeatLabel,
                ObservedHeartbeatLabel: observedHeartbeatLabel,
                AgentLabel: agentLabel,
                SessionLabel: sessionLabel,
                StartupSourceLabel: startupSourceLabel,
                LaunchReasonLabel: launchReasonLabel,
                RecoveryStateLabel: recoveryStateLabel,
                WarningText: BuildFreshnessExplanation(status, snapshot: null),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.IsNullOrWhiteSpace(ownershipDetail))
        {
            return new SessionTruthView(
                StatusLabel: "Warning",
                ContextLabel: contextLabel,
                HeartbeatLabel: heartbeatLabel,
                ObservedHeartbeatLabel: observedHeartbeatLabel,
                AgentLabel: agentLabel,
                SessionLabel: sessionLabel,
                StartupSourceLabel: startupSourceLabel,
                LaunchReasonLabel: launchReasonLabel,
                RecoveryStateLabel: recoveryStateLabel,
                WarningText: $"Ownership mismatch detected: {ownershipDetail}.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.Ordinal))
        {
            string warning = string.IsNullOrWhiteSpace(captureFailureDetail)
                ? "PORTA is not attached to the active user session."
                : $"PORTA is not attached to the active user session: {captureFailureDetail}.";
            return new SessionTruthView(
                StatusLabel: "Warning",
                ContextLabel: contextLabel,
                HeartbeatLabel: heartbeatLabel,
                ObservedHeartbeatLabel: observedHeartbeatLabel,
                AgentLabel: agentLabel,
                SessionLabel: sessionLabel,
                StartupSourceLabel: startupSourceLabel,
                LaunchReasonLabel: launchReasonLabel,
                RecoveryStateLabel: recoveryStateLabel,
                WarningText: warning,
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!status.UserAgentHeartbeatUpdatedAtUtc.HasValue)
        {
            return new SessionTruthView(
                StatusLabel: "Watching",
                ContextLabel: contextLabel,
                HeartbeatLabel: heartbeatLabel,
                ObservedHeartbeatLabel: observedHeartbeatLabel,
                AgentLabel: agentLabel,
                SessionLabel: sessionLabel,
                StartupSourceLabel: startupSourceLabel,
                LaunchReasonLabel: launchReasonLabel,
                RecoveryStateLabel: recoveryStateLabel,
                WarningText: UserAgentRecoveryCopy.HeartbeatWaitingWarningText,
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        return new SessionTruthView(
            StatusLabel: "Healthy",
            ContextLabel: contextLabel,
            HeartbeatLabel: heartbeatLabel,
            ObservedHeartbeatLabel: observedHeartbeatLabel,
            AgentLabel: agentLabel,
            SessionLabel: sessionLabel,
            StartupSourceLabel: startupSourceLabel,
            LaunchReasonLabel: launchReasonLabel,
            RecoveryStateLabel: recoveryStateLabel,
            WarningText: "Session ownership is healthy and attached to the interactive user session.",
            StatusColor: Color.FromRgb(74, 222, 128),
            DetailColor: Color.FromRgb(148, 163, 184));
    }

    private static InputTruthView BuildInputTruthView(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        if (status == null)
        {
            return new InputTruthView(
                StatusLabel: "Unavailable",
                KeyboardSignalLabel: NotAvailableText,
                MouseSignalLabel: NotAvailableText,
                LastInputLabel: NotAvailableText,
                LastInputAgeLabel: NotAvailableText,
                InputOwnerLabel: NotAvailableText,
                OwnershipReasonLabel: NotAvailableText,
                WarningText: "Input truth is unavailable because the local API did not return a live status payload.",
                StatusColor: Color.FromRgb(248, 113, 113),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        bool liveFresh = IsLiveSessionFresh(liveSession);
        bool keyboardSignalActive = liveFresh ? liveSession!.KeyboardActive : status.KeyboardSignalActive;
        bool mouseSignalActive = liveFresh ? liveSession!.MouseActive : status.MouseSignalActive;
        string keyboardLabel = FormatCaptureSignalLabel(keyboardSignalActive, liveFresh || statusFresh);
        string mouseLabel = FormatCaptureSignalLabel(mouseSignalActive, liveFresh || statusFresh);
        string lastInputLabel = liveFresh
            ? FormatCaptureLastInputLabel(liveSession!.LastInputEventUtc)
            : FormatCaptureLastInputLabel(status.LastInputEventUtc);
        string lastInputAgeLabel = liveFresh
            ? FormatAgeLabel(liveSession!.last_input_age_seconds)
            : FormatAgeLabel(status.last_accepted_input_age_seconds);
        string inputOwnerLabel = FormatInputOwnerLabel(status);
        string? captureFailureDetail = DescribeCaptureFailureReason(status.CaptureFailureReason);
        string? ownershipDetail = DescribeOwnershipWarning(status);
        string ownershipReasonLabel = string.IsNullOrWhiteSpace(ownershipDetail) ? "None" : ownershipDetail;

        if (!statusFresh)
        {
            return new InputTruthView(
                StatusLabel: "Stale",
                KeyboardSignalLabel: keyboardLabel,
                MouseSignalLabel: mouseLabel,
                LastInputLabel: lastInputLabel,
                LastInputAgeLabel: lastInputAgeLabel,
                InputOwnerLabel: inputOwnerLabel,
                OwnershipReasonLabel: ownershipReasonLabel,
                WarningText: BuildFreshnessExplanation(status, snapshot: null),
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.IsNullOrWhiteSpace(ownershipDetail))
        {
            return new InputTruthView(
                StatusLabel: "Warning",
                KeyboardSignalLabel: keyboardLabel,
                MouseSignalLabel: mouseLabel,
                LastInputLabel: lastInputLabel,
                LastInputAgeLabel: lastInputAgeLabel,
                InputOwnerLabel: inputOwnerLabel,
                OwnershipReasonLabel: ownershipReasonLabel,
                WarningText: $"Live input owner does not match the current session: {ownershipDetail}.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!string.IsNullOrWhiteSpace(captureFailureDetail))
        {
            return new InputTruthView(
                StatusLabel: "Warning",
                KeyboardSignalLabel: keyboardLabel,
                MouseSignalLabel: mouseLabel,
                LastInputLabel: lastInputLabel,
                LastInputAgeLabel: lastInputAgeLabel,
                InputOwnerLabel: inputOwnerLabel,
                OwnershipReasonLabel: ownershipReasonLabel,
                WarningText: $"Live input is not trusted: {captureFailureDetail}.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        if (!keyboardSignalActive || !mouseSignalActive)
        {
            string missingSignals = BuildMissingSignalList(keyboardSignalActive, mouseSignalActive);
            return new InputTruthView(
                StatusLabel: "Watching",
                KeyboardSignalLabel: keyboardLabel,
                MouseSignalLabel: mouseLabel,
                LastInputLabel: lastInputLabel,
                LastInputAgeLabel: lastInputAgeLabel,
                InputOwnerLabel: inputOwnerLabel,
                OwnershipReasonLabel: ownershipReasonLabel,
                WarningText: $"Waiting for fresh {missingSignals} activity from the current user session. This is a watching state, not a capture-failure alert.",
                StatusColor: Color.FromRgb(251, 191, 36),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        return new InputTruthView(
            StatusLabel: "Healthy",
            KeyboardSignalLabel: keyboardLabel,
            MouseSignalLabel: mouseLabel,
            LastInputLabel: lastInputLabel,
            LastInputAgeLabel: lastInputAgeLabel,
            InputOwnerLabel: inputOwnerLabel,
            OwnershipReasonLabel: ownershipReasonLabel,
            WarningText: liveFresh
                ? BuildLiveActivitySummary(liveSession!)
                : "Keyboard and mouse signals are arriving from the current user session.",
            StatusColor: Color.FromRgb(74, 222, 128),
            DetailColor: Color.FromRgb(148, 163, 184));
    }

    private StateTruthView BuildStateTruthView(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh)
    {
        if (snapshot == null)
        {
            string warning = statusFresh
                ? "State truth unavailable because no current snapshot was returned."
                : BuildFreshnessExplanation(status, snapshot);
            return new StateTruthView(
                ContinuityLabel: NotAvailableText,
                ConfidenceLabel: NotAvailableText,
                CalibrationLabel: NotAvailableText,
                BreakReadinessLabel: NotAvailableText,
                ModesLabel: NotAvailableText,
                WarningText: warning,
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        string continuityState = string.IsNullOrWhiteSpace(snapshot.ContinuityState)
            ? "Unknown"
            : snapshot.ContinuityState!.Trim().ToLowerInvariant() switch
            {
                "keep" => "Keep",
                "pause" => "Pause",
                "decay" => "Decay",
                "reset" => "Reset",
                _ => snapshot.ContinuityState!
            };
        string continuitySeconds = FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot));
        string continuityLabel =
            $"{continuityState} | {continuitySeconds} focus episode | quality {snapshot.FocusEpisodeQuality:F2} | confidence {(snapshot.ContinuityConfidence ?? 0d):F2}";

        string trustTier = ToConfidenceLabel(NormalizeTrustTier(snapshot.TrustTier, snapshot.ModelConfidence));
        string confidenceLabel =
            $"Confidence {trustTier} | model {(snapshot.ModelConfidence ?? snapshot.Confidence):F2} | signal quality {(snapshot.SignalQualityScore ?? snapshot.CurrentQualityScore ?? 0d):F2} | partial {MapYesNo(snapshot.PartialData)}";

        string calibrationStage = NormalizeCalibrationStage(snapshot.CalibrationStage, snapshot.BaselineReady, snapshot.ColdStart);
        string progressText = snapshot.BaselineReadyPct.HasValue
            ? $"{Math.Clamp(snapshot.BaselineReadyPct.Value * 100d, 0d, 100d):F0}% metrics ready"
            : "progress unavailable";
        string calibrationLabel =
            $"{ToCalibrationStageLabel(calibrationStage)} | {progressText} | quality {(snapshot.CurrentQualityScore?.ToString("F2", CultureInfo.InvariantCulture) ?? NotAvailableText)}";

        string breakReadinessLabel = BuildBreakReadinessLabel(snapshot);
        string modesLabel =
            $"Pressure {(ResolvePressureLevel(snapshot) * 100d):F0}% | Overload active {(snapshot.OverloadActive ? "ON" : "OFF")} | Deep work {(snapshot.DeepWorkActive ? "ON" : "OFF")} | Notifications {(snapshot.NotificationsSuppressed ? "suppressed" : "normal")}";

        string? captureHint = status == null ? null : BuildCaptureContextHint(status);
        string warningText;
        Color detailColor;
        if (!statusFresh || !snapshotFresh)
        {
            warningText = BuildFreshnessExplanation(status, snapshot);
            detailColor = Color.FromRgb(245, 158, 11);
        }
        else if (!string.IsNullOrWhiteSpace(captureHint))
        {
            warningText = $"State is present, but live capture is unhealthy: {captureHint}.";
            detailColor = Color.FromRgb(245, 158, 11);
        }
        else
        {
            warningText = "State truth is live and aligned with current capture health.";
            detailColor = Color.FromRgb(148, 163, 184);
        }

        return new StateTruthView(
            ContinuityLabel: continuityLabel,
            ConfidenceLabel: confidenceLabel,
            CalibrationLabel: calibrationLabel,
            BreakReadinessLabel: breakReadinessLabel,
            ModesLabel: modesLabel,
            WarningText: warningText,
            DetailColor: detailColor);
    }

    private static ActionTruthView BuildActionTruthView(
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh)
    {
        LocalApiEventResponse? latestEvent = recentEvents.FirstOrDefault();
        if (latestEvent == null)
        {
            return new ActionTruthView(
                TitleLabel: "No recent actions",
                TypeLabel: NotAvailableText,
                DecisionLabel: NotAvailableText,
                ExecutionLabel: NotAvailableText,
                WhenLabel: NotAvailableText,
                WarningText: statusFresh
                    ? "No recent action decisions are available yet."
                    : BuildFreshnessExplanation(status, snapshot),
                DetailColor: Color.FromRgb(245, 158, 11));
        }

        string warningText = BuildExecutionStateText(latestEvent);
        Color detailColor = NormalizeExecutionState(latestEvent.ExecutionState) == "failed"
            ? Color.FromRgb(248, 113, 113)
            : NormalizeExecutionState(latestEvent.ExecutionState) == "verified"
                ? Color.FromRgb(74, 222, 128)
                : NormalizeExecutionState(latestEvent.ExecutionState) == "applied"
                ? Color.FromRgb(148, 163, 184)
                : Color.FromRgb(245, 158, 11);

        return new ActionTruthView(
            TitleLabel: ToActionTitle(latestEvent),
            TypeLabel: ClassifyAction(latestEvent),
            DecisionLabel: BuildDecisionStateText(latestEvent).Replace("Decision: ", string.Empty, StringComparison.Ordinal),
            ExecutionLabel: BuildExecutionStateText(latestEvent).Replace("Execution: ", string.Empty, StringComparison.Ordinal),
            WhenLabel: latestEvent.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            WarningText: warningText,
            DetailColor: detailColor);
    }

    private static string BuildMissingSignalList(bool keyboardSignalActive, bool mouseSignalActive)
    {
        if (!keyboardSignalActive && !mouseSignalActive)
        {
            return "keyboard and mouse";
        }

        return keyboardSignalActive ? "mouse" : "keyboard";
    }

    private static string BuildBreakReadinessLabel(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.BreakPendingAction))
        {
            string actionTitle = PortaBreakSemantics.ResolveBreakLabel(
                snapshot.BreakPendingKind ?? snapshot.BreakPendingAction,
                HumanizeActionName(snapshot.BreakPendingAction));
            string effectiveFocusText = snapshot.BreakPendingEffectiveFocusSeconds.HasValue
                ? FormatFocusedTime(snapshot.BreakPendingEffectiveFocusSeconds.Value)
                : NotAvailableText;
            string windowText = snapshot.BreakPendingEffectiveFocusWindowSeconds.HasValue
                ? $"{Math.Max(0, snapshot.BreakPendingEffectiveFocusWindowSeconds.Value) / 60} min window"
                : "rolling window";
            string reasonText = string.IsNullOrWhiteSpace(snapshot.BreakPendingReasonCode)
                ? "pending"
                : snapshot.BreakPendingReasonCode!.Replace('_', ' ');

            if (snapshot.BreakDeliveryBoundaryReached == true)
            {
                string boundaryKind = string.IsNullOrWhiteSpace(snapshot.BreakDeliveryBoundaryKind)
                    ? "ready to deliver"
                    : HumanizeBreakBoundaryKind(snapshot.BreakDeliveryBoundaryKind);
                return $"{actionTitle} ready | {effectiveFocusText} effective focus in {windowText} | boundary {boundaryKind} | {reasonText}";
            }

            string waitingFor = string.IsNullOrWhiteSpace(snapshot.BreakDeliveryBoundaryKind)
                ? "waiting for a delivery boundary"
                : $"waiting for {HumanizeBreakBoundaryKind(snapshot.BreakDeliveryBoundaryKind)}";
            return $"{actionTitle} pending | {effectiveFocusText} effective focus in {windowText} | {waitingFor} | {reasonText}";
        }

        if (snapshot.BreakShortBreakEffectiveFocusSeconds.HasValue || snapshot.BreakLongBreakEffectiveFocusSeconds.HasValue)
        {
            string shortBreak = snapshot.BreakShortBreakEffectiveFocusSeconds.HasValue
                ? FormatFocusedTime(snapshot.BreakShortBreakEffectiveFocusSeconds.Value)
                : NotAvailableText;
            string longBreak = snapshot.BreakLongBreakEffectiveFocusSeconds.HasValue
                ? FormatFocusedTime(snapshot.BreakLongBreakEffectiveFocusSeconds.Value)
                : NotAvailableText;
            return $"Short-break window: {shortBreak} | Long-break window: {longBreak}";
        }

        return "No short-break or long-break suggestion is currently pending.";
    }

    private static string HumanizeBreakBoundaryKind(string? boundaryKind)
    {
        if (string.IsNullOrWhiteSpace(boundaryKind))
        {
            return "delivery boundary";
        }

        return boundaryKind.Trim().ToLowerInvariant() switch
        {
            "short_idle" => "short idle",
            "micro_idle" => "micro-idle",
            "post_switch_settle" => "post-switch settle",
            "typing_quiet" => "typing quiet",
            _ => boundaryKind.Replace('_', ' ')
        };
    }

    private static string BuildEngineServiceLabel(
        ServiceProbeResult serviceProbe,
        LocalApiStatusResponse? status,
        bool apiReachable)
    {
        if (serviceProbe.Running)
        {
            return "Running";
        }

        if (status?.ServiceRunning == true && apiReachable)
        {
            return "Running (manual host)";
        }

        return serviceProbe.Exists
            ? $"Stopped ({serviceProbe.StatusText})"
            : "Not installed";
    }

    private static string BuildEngineDatabaseLabel(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return NotAvailableText;
        }

        if (status.DbMigrationFailed)
        {
            return "Error (migration/startup)";
        }

        bool schemaKnown = !string.IsNullOrWhiteSpace(status.SchemaVersion)
            && !status.SchemaVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        return schemaKnown
            ? $"Ready (schema {status.SchemaVersion})"
            : NotAvailableText;
    }

    private static string BuildEngineSnapshotLabel(
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh)
    {
        if (snapshot == null)
        {
            return "Unavailable";
        }

        if (!statusFresh || !snapshotFresh)
        {
            return "Cached";
        }

        return $"Live ({(snapshot.Confidence * 100d):F0}% confidence)";
    }

    private static string BuildEnginePolicyLabel(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return NotAvailableText;
        }

        if (!status.ConsentActive)
        {
            return "Blocked (consent needed)";
        }

        if (status.Paused)
        {
            if (status.PauseUntilUtc.HasValue && status.PauseUntilUtc.Value > DateTimeOffset.UtcNow)
            {
                return $"Paused until {status.PauseUntilUtc.Value.ToLocalTime():h:mm tt}";
            }

            return "Paused";
        }

        return string.IsNullOrWhiteSpace(status.PolicyVersion)
            ? "Active"
            : $"Active ({status.PolicyVersion})";
    }

    private static string ToActionTitle(LocalApiEventResponse actionEvent)
    {
        // Resolve canonical break kind first so both new (short_break / long_break)
        // and legacy (stretch_break_suggestion / break_suggestion) show human labels.
        string? breakKind = PortaBreakSemantics.ResolveBreakKind(actionEvent.Action);
        if (breakKind != null)
        {
            return $"{PortaBreakSemantics.ResolveBreakLabel(breakKind)} suggested";
        }

        if (string.Equals(actionEvent.Type, "BREAK_SUGGESTION", StringComparison.OrdinalIgnoreCase))
        {
            return "Break suggested";
        }

        if (string.IsNullOrWhiteSpace(actionEvent.Action))
        {
            return "Action recorded";
        }

        return actionEvent.Action.Replace('_', ' ');
    }

    private string BuildNowDoingSentence(LocalApiCurrentSnapshotResponse snapshot)
    {
        if (_latestStatus?.Paused == true)
        {
            if (_latestStatus.PauseUntilUtc.HasValue && _latestStatus.PauseUntilUtc.Value > DateTimeOffset.UtcNow)
            {
                return $"Paused until {_latestStatus.PauseUntilUtc.Value.ToLocalTime():h:mm tt}";
            }

            return "Paused.";
        }

        if (_latestStatus?.ConsentActive == false)
        {
            return "Waiting for consent before collecting.";
        }

        string? captureFailureDetail = DescribeCaptureFailureReason(_latestStatus?.CaptureFailureReason);
        if (!string.IsNullOrWhiteSpace(captureFailureDetail))
        {
            return $"Waiting for valid capture: {captureFailureDetail}.";
        }

        string? signalWaitDetail = BuildMissingSignalHint(_latestStatus);
        if (!string.IsNullOrWhiteSpace(signalWaitDetail))
        {
            return $"Waiting for live capture: {signalWaitDetail}.";
        }

        if (_effectiveSettings != null
            && !_effectiveSettings.NotificationReductionEnabled
            && !_effectiveSettings.FocusModeEnabled
            && !_effectiveSettings.AnimationReductionEnabled
            && !_effectiveSettings.TemporaryAppRestrictionEnabled)
        {
            return "Watching only (no actions enabled)";
        }

        string calibrationStage = NormalizeCalibrationStage(snapshot.CalibrationStage, snapshot.BaselineReady, snapshot.ColdStart);
        if (string.Equals(calibrationStage, CalibrationStageCold, StringComparison.Ordinal))
        {
            return "Calibrating before suggesting actions.";
        }

        if (string.Equals(calibrationStage, CalibrationStageProvisional, StringComparison.Ordinal))
        {
            return "Suggestions are active while calibration continues.";
        }

        return "Ready to regulate in real time.";
    }

    private void UpdateCaptureHealthCards(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        _ = BuildCaptureHealthView(status, liveSession, statusFresh);
    }

    private void UpdateSessionTruthCards(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        SessionTruthView view = BuildSessionTruthView(status, liveSession, statusFresh);
        var statusBrush = new SolidColorBrush(view.StatusColor);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        HealthSessionStatusValue.Text = view.StatusLabel;
        HealthSessionStatusValue.Foreground = statusBrush;
        HealthSessionContextValue.Text = view.ContextLabel;
        HealthSessionHeartbeatValue.Text = view.HeartbeatLabel;
        HealthSessionObservedHeartbeatValue.Text = view.ObservedHeartbeatLabel;
        HealthSessionAgentValue.Text = view.AgentLabel;
        HealthSessionIdValue.Text = view.SessionLabel;
        HealthSessionStartupSourceValue.Text = view.StartupSourceLabel;
        HealthSessionLaunchReasonValue.Text = view.LaunchReasonLabel;
        HealthSessionRecoveryValue.Text = view.RecoveryStateLabel;
        HealthSessionHeartbeatValue.Text = SimplifyHeartbeat(HealthSessionHeartbeatValue.Text);
        HealthSessionRecoveryValue.Text  = SimplifyRecovery(HealthSessionRecoveryValue.Text);
        HealthSessionWarningValue.Text = view.WarningText;
        HealthSessionWarningValue.Foreground = detailBrush;
        ApplyStatusBadge(HealthSessionBadge, HealthSessionBadgeText, HealthSessionDot, view.StatusLabel);
    }

    private void UpdateInputTruthCards(LocalApiStatusResponse? status, LocalApiLiveSessionStateResponse? liveSession, bool statusFresh)
    {
        InputTruthView view = BuildInputTruthView(status, liveSession, statusFresh);
        var statusBrush = new SolidColorBrush(view.StatusColor);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        HealthInputStatusValue.Text = view.StatusLabel;
        HealthInputStatusValue.Foreground = statusBrush;
        HealthKeyboardSignalValue.Text = view.KeyboardSignalLabel;
        HealthMouseSignalValue.Text = view.MouseSignalLabel;
        HealthLastInputValue.Text = view.LastInputLabel;
        HealthLastInputAgeValue.Text = view.LastInputAgeLabel;
        HealthInputOwnerValue.Text = view.InputOwnerLabel;
        HealthInputOwnershipReasonValue.Text = view.OwnershipReasonLabel;
        HealthInputWarningValue.Text = view.WarningText;
        HealthInputWarningValue.Foreground = detailBrush;
        ApplyStatusBadge(HealthInputBadge, HealthInputBadgeText, HealthInputDot, view.StatusLabel);
    }

    private void UpdateStateTruthCards(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh)
    {
        StateTruthView view = BuildStateTruthView(status, snapshot, statusFresh, snapshotFresh);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        HealthStateContinuityValue.Text = view.ContinuityLabel;
        HealthStateConfidenceValue.Text = view.ConfidenceLabel;
        HealthStateCalibrationValue.Text = view.CalibrationLabel;
        HealthStateBreakReadinessValue.Text = view.BreakReadinessLabel;
        HealthStateModesValue.Text = view.ModesLabel;
        HealthStateConfidenceValue.Text     = SimplifyConfidence(HealthStateConfidenceValue.Text);
        HealthStateCalibrationValue.Text    = SimplifyCalibration(HealthStateCalibrationValue.Text);
        HealthStateModesValue.Text          = SimplifyModes(HealthStateModesValue.Text);
        HealthStateBreakReadinessValue.Text = SimplifyBreakReadiness(HealthStateBreakReadinessValue.Text);
        HealthStateWarningValue.Text = view.WarningText;
        HealthStateWarningValue.Foreground = detailBrush;
        var stateBadgeLabel = HealthStateConfidenceValue.Text.StartsWith("High", StringComparison.OrdinalIgnoreCase) ? "Confident"
            : HealthStateConfidenceValue.Text == "Not available" ? "Unknown"
            : "Watching";
        ApplyStatusBadge(HealthStateBadge, HealthStateBadgeText, HealthStateDot,
            stateBadgeLabel == "Confident" ? "Healthy" : "Watching");
        HealthStateBadgeText.Text = stateBadgeLabel;
    }

    private void UpdateActionTruthCards(
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh)
    {
        ActionTruthView view = BuildActionTruthView(recentEvents, status, snapshot, statusFresh);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        HealthActionTitleValue.Text = view.TitleLabel;
        HealthActionTypeValue.Text = view.TypeLabel;
        HealthActionDecisionValue.Text = view.DecisionLabel;
        HealthActionExecutionValue.Text = view.ExecutionLabel;
        HealthActionWhenValue.Text = view.WhenLabel;
        HealthActionWarningValue.Text = view.WarningText;
        HealthActionWarningValue.Foreground = detailBrush;
        var actionBadge = string.IsNullOrEmpty(view.TitleLabel) || view.TitleLabel == "Not available" ? "No action"
            : view.TypeLabel.Contains("Policy", StringComparison.OrdinalIgnoreCase) ? "Policy"
            : "Applied";
        ApplyStatusBadge(HealthActionBadge, HealthActionBadgeText, HealthActionDot, "Watching");
        HealthActionBadgeText.Text = actionBadge;
    }

    private void ApplyStatusBadge(Border badgeBorder, TextBlock badgeText, Border dot, string statusLabel)
    {
        var (bgHex, borderHex, fgHex, dotHex, label) = statusLabel switch
        {
            "Healthy"     => ("#1422C55E", "#2222C55E", "#16A34A", "#22C55E", "Healthy"),
            "Active"      => ("#1422C55E", "#2222C55E", "#16A34A", "#22C55E", "Active"),
            "Offline" or "Unavailable" => ("#14EF4444", "#22EF4444", "#991B1B", "#EF4444", statusLabel),
            "Stale" or "Warning" or "Watching" or "Blocked" => ("#14F59E0B", "#22F59E0B", "#92400E", "#F59E0B", statusLabel),
            _ => ("#14F59E0B", "#22F59E0B", "#92400E", "#F59E0B", statusLabel)
        };
        var conv = new System.Windows.Media.BrushConverter();
        badgeBorder.Background  = (System.Windows.Media.Brush)conv.ConvertFromString(bgHex)!;
        badgeBorder.BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(borderHex)!;
        badgeText.Foreground    = (System.Windows.Media.Brush)conv.ConvertFromString(fgHex)!;
        badgeText.Text          = label;
        dot.Background          = (System.Windows.Media.Brush)conv.ConvertFromString(dotHex)!;
    }

    private static string SimplifyHeartbeat(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        var s = raw.IndexOf('('); var e = raw.IndexOf(')');
        if (s >= 0 && e > s) return "Live " + raw.Substring(s + 1, e - s - 1).Trim();
        return raw;
    }

    private static string SimplifyRecovery(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        foreach (var part in raw.Split('|'))
        {
            var t = part.Trim().ToLowerInvariant();
            if (t.StartsWith("live ")) continue;
            if (t.Contains("=0") || t.Contains("=none") || t.Contains("=not reported")) continue;
            return "Issues detected";
        }
        return "No issues";
    }

    private static string SimplifyConfidence(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        var first = raw.Split('|')[0].Trim();
        foreach (var word in first.Split(' '))
            if (word is "High" or "Medium" or "Low" or "Unknown" or "None" or "Partial")
                return word;
        return first;
    }

    private static string SimplifyCalibration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        return raw.Split('|')[0].Trim();
    }

    private static string SimplifyModes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        var active = raw.Split('|')
            .Select(p => p.Trim())
            .Where(p => p.EndsWith(" ON", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[..^3].Trim())
            .ToList();
        return active.Count > 0 ? string.Join(" + ", active) : "Normal";
    }

    private static string SimplifyBreakReadiness(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "Not available") return raw;
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("short-break window:"))
        {
            var idx = raw.IndexOf(":", raw.IndexOf("short-break", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
            var mins = raw[(idx + 1)..].Split('|')[0].Trim();
            return "Break in " + mins;
        }
        return raw.Split('|')[0].Trim();
    }

    private static OverloadExplainView ParseOverloadExplain(string? contributorsJson)
    {
        if (string.IsNullOrWhiteSpace(contributorsJson))
        {
            return OverloadExplainView.Unavailable;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(contributorsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("components", out JsonElement components))
            {
                return OverloadExplainView.Unavailable;
            }

            double? clarity = ReadComponentValue(components, "F");
            double? pressure = ReadComponentValue(components, "I");
            double? jitter = ReadComponentValue(components, "V");
            string fivSummary = BuildFivSummary(clarity, pressure, jitter);

            string topReasons = BuildTopReasons(components);

            string qualityText = TryReadDouble(root, "quality", out double quality)
                ? quality.ToString("F2", CultureInfo.InvariantCulture)
                : NotAvailableText;
            string agreementText = TryReadDouble(root, "agreement", out double agreement)
                ? agreement.ToString("F2", CultureInfo.InvariantCulture)
                : NotAvailableText;

            return new OverloadExplainView(
                FivSummary: fivSummary,
                TopReasons: topReasons,
                Quality: qualityText,
                Agreement: agreementText,
                Clarity: clarity,
                Pressure: pressure,
                Jitter: jitter,
                HasStructuredData: true);
        }
        catch
        {
            return OverloadExplainView.Unavailable;
        }
    }

    private static string BuildFivSummary(double? clarity, double? pressure, double? jitter)
    {
        if (!clarity.HasValue || !pressure.HasValue || !jitter.HasValue)
        {
            return "Explain not available (backend field missing).";
        }

        return $"Clarity: {clarity.Value:F2} | Pressure: {pressure.Value:F2} | Jitter: {jitter.Value:F2}";
    }

    private static string BuildHumanFivSummary(OverloadExplainView explain)
    {
        if (!explain.Clarity.HasValue || !explain.Pressure.HasValue || !explain.Jitter.HasValue)
        {
            return "No structured explain data at this timestamp.";
        }

        string clarityBand = ToBandLabel(explain.Clarity.Value);
        string pressureBand = ToBandLabel(explain.Pressure.Value);
        string jitterBand = ToBandLabel(explain.Jitter.Value);
        string sentence = BuildHumanWhySentence(clarityBand, pressureBand, jitterBand);
        return $"Clarity: {clarityBand} | Pressure: {pressureBand} | Jitter: {jitterBand}{Environment.NewLine}{sentence}";
    }

    private static string BuildHumanWhySentence(string clarityBand, string pressureBand, string jitterBand)
    {
        if (pressureBand == "High" && jitterBand == "High")
        {
            return "Pattern shows sustained pressure with unstable behavior.";
        }

        if (pressureBand == "High")
        {
            return "Pattern shows elevated workload pressure.";
        }

        if (clarityBand == "Low" && jitterBand == "High")
        {
            return "Pattern shows fragmented and volatile interaction.";
        }

        if (clarityBand == "High" && pressureBand == "Low")
        {
            return "Pattern looks stable with low pressure.";
        }

        return "Pattern is mixed; monitor trend over the next intervals.";
    }

    private static string ToBandLabel(double value)
    {
        double clamped = Math.Clamp(value, 0d, 1d);
        if (clamped < 0.34d)
        {
            return "Low";
        }

        if (clamped < 0.67d)
        {
            return "Medium";
        }

        return "High";
    }

    private static double? ReadComponentValue(JsonElement components, string key)
    {
        if (!components.TryGetProperty(key, out JsonElement component))
        {
            return null;
        }

        if (!component.TryGetProperty("value", out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed)
            ? parsed
            : null;
    }

    private static string BuildTopReasons(JsonElement components)
    {
        var reasons = new List<(string Key, double Weight)>();
        foreach (string componentKey in new[] { "F", "I", "V" })
        {
            if (!components.TryGetProperty(componentKey, out JsonElement component)
                || !component.TryGetProperty("top", out JsonElement top)
                || top.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement item in top.EnumerateArray())
            {
                if (!item.TryGetProperty("key", out JsonElement keyElem))
                {
                    continue;
                }

                string? key = keyElem.GetString();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                double weight = 0d;
                if (item.TryGetProperty("contrib", out JsonElement contrib)
                    && contrib.ValueKind == JsonValueKind.Number
                    && contrib.TryGetDouble(out double parsedContrib))
                {
                    weight = Math.Abs(parsedContrib);
                }
                else if (item.TryGetProperty("w", out JsonElement w)
                    && w.ValueKind == JsonValueKind.Number
                    && w.TryGetDouble(out double parsedW))
                {
                    weight = Math.Abs(parsedW);
                }
                else if (item.TryGetProperty("value", out JsonElement value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetDouble(out double parsedValue))
                {
                    weight = Math.Abs(parsedValue);
                }

                reasons.Add((key, weight));
            }
        }

        string[] topReasons = reasons
            .OrderByDescending(static reason => reason.Weight)
            .ThenBy(static reason => reason.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static reason => reason.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();

        return topReasons.Length > 0
            ? string.Join(", ", topReasons)
            : "Explain not available (backend field missing).";
    }

    private static string FormatTopReasons(string topReasonsRaw, bool explainMode)
    {
        if (string.IsNullOrWhiteSpace(topReasonsRaw)
            || topReasonsRaw.Contains("not available", StringComparison.OrdinalIgnoreCase))
        {
            return "No ranked contributors in this snapshot.";
        }

        string[] items = topReasonsRaw
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (items.Length == 0)
        {
            return "No ranked contributors in this snapshot.";
        }

        if (explainMode)
        {
            return string.Join(", ", items);
        }

        return string.Join(", ", items.Select(HumanizeReasonKey));
    }

    private static string HumanizeReasonKey(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "typing_burst_intensity" => "typing intensity",
            "focus_switch_rate" => "focus switching",
            "scroll_velocity_index" => "scroll intensity",
            "keyboard_iki_cv" => "typing rhythm variance",
            "mouse_jerk_p95" => "mouse jerk",
            "idle_micro_idle_seconds" => "micro-idle time",
            "focus_app_entropy" => "app-switch entropy",
            _ => key.Replace('_', ' ')
        };
    }

    private static bool TryReadDouble(JsonElement parent, string propertyName, out double value)
    {
        value = default;
        return parent.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static IReadOnlyList<LocalApiEventResponse> MergeRecentEvents(
        IReadOnlyList<LocalApiEventResponse> existing,
        IReadOnlyList<LocalApiEventResponse> incoming,
        int maxEvents)
    {
        var byId = new Dictionary<long, LocalApiEventResponse>();
        foreach (LocalApiEventResponse evt in existing)
        {
            byId[evt.Id] = evt;
        }

        foreach (LocalApiEventResponse evt in incoming)
        {
            byId[evt.Id] = evt;
        }

        return byId.Values
            .OrderByDescending(static evt => evt.Id)
            .Take(Math.Max(1, maxEvents))
            .ToList();
    }

    private void UpdateLastSeenEventId(long newValue)
    {
        if (newValue <= _lastSeenEventId)
        {
            return;
        }

        _lastSeenEventId = newValue;
        SaveLastSeenEventId(_lastSeenEventId);
    }

    private static long LoadLastSeenEventId()
    {
        try
        {
            string path = BuildEventCursorPath();
            if (!File.Exists(path))
            {
                return 0;
            }

            string raw = File.ReadAllText(path).Trim();
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
                ? parsed
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void SaveLastSeenEventId(long id)
    {
        try
        {
            string path = BuildEventCursorPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch
        {
            // Best effort cursor persistence.
        }
    }

    private static string BuildEventCursorPath()
    {
        return BuildLocalStatePath(EventCursorFileName);
    }

    private static long LoadRecoveryAckEventId()
    {
        try
        {
            string path = BuildRecoveryAckPath();
            if (!File.Exists(path))
            {
                return 0;
            }

            string raw = File.ReadAllText(path).Trim();
            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0
                ? parsed
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void SaveRecoveryAckEventId(long id)
    {
        try
        {
            string path = BuildRecoveryAckPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, id.ToString(CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch
        {
            // Best effort local ack persistence.
        }
    }

    private static string BuildRecoveryAckPath()
    {
        return BuildLocalStatePath(RecoveryAckFileName);
    }

    private static bool LoadOnboardingAcknowledged()
    {
        try
        {
            string path = BuildOnboardingAckPath();
            if (!File.Exists(path))
            {
                return false;
            }

            string raw = File.ReadAllText(path).Trim();
            return !string.IsNullOrWhiteSpace(raw);
        }
        catch
        {
            return false;
        }
    }

    private static void SaveOnboardingAcknowledged()
    {
        try
        {
            string path = BuildOnboardingAckPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Encoding.UTF8);
        }
        catch
        {
            // Best effort onboarding persistence.
        }
    }

    private static string BuildOnboardingAckPath()
    {
        return BuildLocalStatePath(OnboardingAckFileName);
    }

    private static string BuildLocalStatePath(string fileName)
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", fileName);
    }

    private static string ComputeMostVolatileHour(IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
    {
        if (snapshots.Count < 2)
        {
            return NotAvailableText;
        }

        var byHour = snapshots
            .GroupBy(s => s.TimestampUtc.ToLocalTime().Hour)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TimestampUtc).ToList());

        double bestScore = -1d;
        int bestHour = -1;
        foreach ((int hour, List<LocalApiCurrentSnapshotResponse> hourSnapshots) in byHour)
        {
            double score = 0d;
            for (int i = 1; i < hourSnapshots.Count; i++)
            {
                LocalApiCurrentSnapshotResponse previous = hourSnapshots[i - 1];
                LocalApiCurrentSnapshotResponse current = hourSnapshots[i];
                score += Math.Abs(current.FocusStrength - previous.FocusStrength);
                score += Math.Abs(ResolvePressureLevel(current) - ResolvePressureLevel(previous));
                score += Math.Abs(current.FatigueProbability - previous.FatigueProbability);
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestHour = hour;
            }
        }

        if (bestHour < 0)
        {
            return NotAvailableText;
        }

        return $"{bestHour:00}:00-{bestHour:00}:59";
    }

    private void PopulateInputs(
        LocalApiInputsSummaryResponse? inputsSummary,
        LocalApiRetentionResponse? retention,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiStatusResponse? status,
        LocalApiSettingsResponse? settings)
    {
        TrackingPagePresentation presentation = TrackingPagePresentationBuilder.Build(
            status,
            settings,
            snapshot,
            inputsSummary,
            retention);

        TrackingCollectionSummaryText.Text = presentation.CollectionSummary;
        TrackingQualityHeadlineText.Text = presentation.QualityHeadline;
        TrackingQualitySummaryText.Text = presentation.QualitySummary;
        TrackingQualityHelpText.Text = presentation.QualityHelp;
        TrackingConsentValue.Text = presentation.ConsentSummary;
        TrackingPrivacyValue.Text = presentation.PrivacySummary;
        TrackingControlsValue.Text = presentation.ControlsSummary;
        TrackingPauseResumeButton.Content = presentation.PauseResumeLabel;
        TrackingPauseResumeButton.IsEnabled = presentation.PauseResumeEnabled;

        // Quality chip label + colour
        TrackingQualityChipText.Text = presentation.QualityChipLabel;
        TrackingQualityChipText.Foreground = new SolidColorBrush(presentation.QualityChipLabel switch
        {
            "Good"       => Color.FromRgb(0x16, 0xC7, 0x84),
            "Fair"       => Color.FromRgb(0xA5, 0xB4, 0xFC),
            "Settling"   => Color.FromRgb(0xF5, 0x9E, 0x0B),
            "No consent" => Color.FromRgb(0xEF, 0x44, 0x44),
            _            => Color.FromRgb(0x64, 0x74, 0x8B)
        });

        // Quality progress bar fill
        TrackingQualityProgressFill.Background = new SolidColorBrush(presentation.QualityChipLabel switch
        {
            "Good"    => Color.FromRgb(0x16, 0xC7, 0x84),
            "Fair"    => Color.FromRgb(0x63, 0x66, 0xF1),
            "Settling"=> Color.FromRgb(0xF5, 0x9E, 0x0B),
            _         => Color.FromRgb(0x64, 0x74, 0x8B)
        });
        if (TrackingQualityProgressFill.Parent is Border qualityTrack)
        {
            double fillFrac = presentation.QualityProgress;
            if (qualityTrack.ActualWidth > 0)
                TrackingQualityProgressFill.Width = Math.Max(2d, qualityTrack.ActualWidth * fillFrac);
            else
            {
                void OnSized(object? s, SizeChangedEventArgs e) {
                    qualityTrack.SizeChanged -= OnSized;
                    TrackingQualityProgressFill.Width = Math.Max(2d, qualityTrack.ActualWidth * fillFrac);
                }
                qualityTrack.SizeChanged += OnSized;
            }
        }

        if (retention == null)
        {
            RetentionWindowValue.Text = "Stored on this device: Not available";
            RetentionSizeValue.Text = "Local storage used: Not available";
            return;
        }

        RetentionWindowValue.Text = $"Stored on this device for {retention.RetentionDays} days";
        RetentionSizeValue.Text = $"Local storage used: {FormatBytes(retention.TotalBytes)}";
    }

    private void PopulateInterventions(IReadOnlyList<LocalApiEventResponse> events, LocalApiSettingsResponse? settings)
    {
        _suppressSettingsEvents = true;
        if (settings != null)
        {
            InterventionNotificationToggle.IsChecked = settings.NotificationReductionEnabled;
            InterventionFocusModeToggle.IsChecked = settings.FocusModeEnabled;
            InterventionAnimationToggle.IsChecked = settings.AnimationReductionEnabled;
            InterventionAppRestrictionToggle.IsChecked = settings.TemporaryAppRestrictionEnabled;
        }
        else
        {
            InterventionNotificationToggle.IsChecked = false;
            InterventionFocusModeToggle.IsChecked = false;
            InterventionAnimationToggle.IsChecked = false;
            InterventionAppRestrictionToggle.IsChecked = false;
        }

        _suppressSettingsEvents = false;

        IReadOnlyList<LocalApiEventResponse> ordered = events
            .OrderByDescending(static e => e.TimestampUtc)
            .ToList();

        IReadOnlyList<SuggestionRow> suggestions = ordered
            .Where(static e => IsSuggestionEvent(e.Action))
            .Select(e => new SuggestionRow(
                BuildSuggestionTitle(e.Action),
                e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                BuildSuggestionShortWhy(e),
                e))
            .ToList();

        IReadOnlyList<ChangeRow> changes = ordered
            .Where(static e => IsSystemChangeEvent(e))
            .Select(e => new ChangeRow(
                WhatChanged: string.IsNullOrWhiteSpace(e.WhatChanged) ? HumanizeActionName(e.Action) : e.WhatChanged,
                When: e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Why: BuildChangeShortWhy(e),
                Source: e))
            .ToList();

        IReadOnlyList<AlertRow> alerts = ordered
            .Where(static e => IsUserFacingAlert(e))
            .Select(e => new AlertRow(
                Title: BuildAlertTitle(e),
                When: e.TimestampUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                Reason: !string.IsNullOrWhiteSpace(e.Reason) ? TrimSentence(e.Reason, 100) : "",
                Source: e))
            .ToList();

        // Legacy list bindings (hidden elements — kept for compat)
        SuggestionsListView.ItemsSource = suggestions;
        ChangesListView.ItemsSource = changes;
        SuggestionsCountText.Text = suggestions.Count.ToString(CultureInfo.InvariantCulture);
        ChangesCountText.Text = changes.Count.ToString(CultureInfo.InvariantCulture);
        PopulateActionsOverview(ordered);

        SuggestionsEmptyStateText.Visibility = suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsListView.Visibility = suggestions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangesEmptyStateText.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesListView.Visibility = changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionsTabControls();

        // ── New Actions page UI ────────────────────────────────────────
        _lastInterventionEvents = events; // cache for re-renders
        PopulateActionsNewUI(ordered);

        bool selectedStillExists = _selectedIntervention != null
            && ordered.Any(e => e.Id == _selectedIntervention.Id);
        if (!selectedStillExists)
        {
            _selectedIntervention = null;
            UpdateInterventionDrawer(null);
            return;
        }

        if (_selectedIntervention != null)
        {
            LocalApiEventResponse? latestSelected = ordered.FirstOrDefault(e => e.Id == _selectedIntervention.Id);
            _selectedIntervention = latestSelected;
            UpdateInterventionDrawer(_selectedIntervention);
        }
    }

    private void PopulateActionsNewUI(IReadOnlyList<LocalApiEventResponse> allOrdered)
    {
        // ── Apply today's cutoff filter ────────────────────────────────
        DateTimeOffset cutoff = new DateTimeOffset(DateTime.Today,
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime();
        var ordered = allOrdered
            .Where(e => e.TimestampUtc >= cutoff)
            .ToList();

        // ── Re-derive typed counts from the filtered slice ─────────────
        var suggestions = ordered
            .Where(static e => IsSuggestionEvent(e.Action))
            .Select(e => new SuggestionRow(
                BuildSuggestionTitle(e.Action),
                e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                BuildSuggestionShortWhy(e),
                e))
            .ToList();

        var changes = ordered
            .Where(static e => IsSystemChangeEvent(e) && !IsInternalStateTransition(e) && !IsEngineHeartbeatEvent(e))
            .Select(e => new ChangeRow(
                WhatChanged: string.IsNullOrWhiteSpace(e.WhatChanged) ? HumanizeActionName(e.Action) : e.WhatChanged,
                When: e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Why: BuildChangeShortWhy(e),
                Source: e))
            .ToList();

        var alerts = ordered
            .Where(static e => IsUserFacingAlert(e))
            .Select(e => new AlertRow(
                Title: BuildAlertTitle(e),
                When: e.TimestampUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                Reason: !string.IsNullOrWhiteSpace(e.Reason) ? TrimSentence(e.Reason, 100) : "",
                Source: e))
            .ToList();

        // ── Period label for the card subtitle ─────────────────────────
        const string periodSuffix = "today";

        // ── Summary cards ──────────────────────────────────────────────
        ActionsCardSuggestionsCount.Text = suggestions.Count.ToString(CultureInfo.InvariantCulture);
        ActionsCardSuggestionsRecent.Text = suggestions.Count > 0
            ? $"{suggestions.Count} suggestion{(suggestions.Count > 1 ? "s" : "")} {periodSuffix}"
            : $"None {periodSuffix}";
        ApplyActionBarFill(ActionsCardSuggestionsTrack, ActionsCardSuggestionsBarFill,
            Math.Clamp(suggestions.Count / 10d, 0d, 1d));

        ActionsCardActionsCount.Text = changes.Count.ToString(CultureInfo.InvariantCulture);
        ActionsCardActionsRecent.Text = changes.Count > 0
            ? $"{changes.Count} action{(changes.Count > 1 ? "s" : "")} taken {periodSuffix}"
            : $"None {periodSuffix}";
        ApplyActionBarFill(ActionsCardActionsTrack, ActionsCardActionsBarFill,
            Math.Clamp(changes.Count / 10d, 0d, 1d));

        ActionsCardAlertsCount.Text = alerts.Count.ToString(CultureInfo.InvariantCulture);
        ActionsCardAlertsRecent.Text = alerts.Count > 0
            ? $"{alerts.Count} alert{(alerts.Count > 1 ? "s" : "")} sent {periodSuffix}"
            : $"None {periodSuffix}";
        ApplyActionBarFill(ActionsCardAlertsTrack, ActionsCardAlertsBarFill,
            Math.Clamp(alerts.Count / 10d, 0d, 1d));

        // ── Unified feed (visual redesign) ────────────────────────────
        ActionsUnifiedFeed.Children.Clear();
        bool allEmpty = ordered.Count == 0;
        ActionsEmptyState.Visibility = allEmpty ? Visibility.Visible : Visibility.Collapsed;
        ActionsUnifiedFeed.Visibility = allEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (allEmpty) return;

        // All events for the selected period — same filters as the cards
        // so Breakdown, Hourly, and "What Happened" are all in sync.
        var allRows = ordered
            .Where(e => !string.IsNullOrWhiteSpace(e.Action)
                     && !IsInternalStateTransition(e)
                     && !IsEngineHeartbeatEvent(e))
            .ToList();

        DateTimeOffset now = DateTimeOffset.Now;

        // Section 1: Category breakdown chart
        ActionsUnifiedFeed.Children.Add(BuildActionsCategoryBreakdown(allRows));
        ActionsUnifiedFeed.Children.Add(new Border { Height = 12 });

        // Section 2: 24-hour activity chart
        ActionsUnifiedFeed.Children.Add(BuildActionsHourlyActivity(allRows));
        ActionsUnifiedFeed.Children.Add(new Border { Height = 12 });

        // Section 3: Grouped action log (replaces 47 identical rows)
        ActionsUnifiedFeed.Children.Add(BuildActionsGroupedLog(allRows, now));
    }

    private static void ApplyActionBarFill(Border track, Border fill, double fraction)
    {
        void Apply(object? s, SizeChangedEventArgs e)
        {
            track.SizeChanged -= Apply;
            fill.Width = fraction > 0d ? Math.Max(2d, track.ActualWidth * fraction) : 0d;
        }

        if (track.ActualWidth > 0d)
            fill.Width = fraction > 0d ? Math.Max(2d, track.ActualWidth * fraction) : 0d;
        else
            track.SizeChanged += Apply;
    }

    private static FrameworkElement BuildActionsUnifiedRow(LocalApiEventResponse ev, DateTimeOffset now)
    {
        bool isSuggestion = IsSuggestionEvent(ev.Action);
        bool isAlert      = IsUserFacingAlert(ev);
        // isChange = everything else

        string chipLabel, title;
        Color chipText, chipBg;

        if (isSuggestion)
        {
            chipLabel = "Suggestion";
            chipText  = Color.FromRgb(0xA5, 0xB4, 0xFC);
            chipBg    = Color.FromArgb(0x26, 0x63, 0x66, 0xF1);
            title     = BuildSuggestionTitle(ev.Action);
        }
        else if (isAlert)
        {
            chipLabel = "Alert";
            chipText  = Color.FromRgb(0xF5, 0x9E, 0x0B);
            chipBg    = Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B);
            title     = BuildAlertTitle(ev);
        }
        else
        {
            chipLabel = "Action";
            chipText  = Color.FromRgb(0x16, 0xC7, 0x84);
            chipBg    = Color.FromArgb(0x26, 0x16, 0xC7, 0x84);
            title     = string.IsNullOrWhiteSpace(ev.WhatChanged) ? HumanizeActionName(ev.Action) : ev.WhatChanged;
        }

        // Truncate title to 60 chars
        if (title.Length > 60) title = title[..57] + "…";

        string timeStr = FormatRelativeTime(ev.TimestampUtc.ToLocalTime(), now);

        string? reason = null;
        if (!string.IsNullOrWhiteSpace(ev.Reason) && ev.Reason.Length > 6
            && !ev.Reason.Contains("null", StringComparison.OrdinalIgnoreCase))
        {
            reason = ev.Reason.Length > 100 ? ev.Reason[..97] + "…" : ev.Reason;
        }

        // Type chip
        var chip = new Border
        {
            Background       = new SolidColorBrush(chipBg),
            CornerRadius     = new CornerRadius(4),
            Padding          = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Top,
            Margin           = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text       = chipLabel,
                FontSize   = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(chipText)
            }
        };

        var titleTb = new TextBlock
        {
            Text         = title,
            FontSize     = 13,
            FontWeight   = FontWeights.Medium,
            Foreground   = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var timeTb = new TextBlock
        {
            Text              = timeStr,
            FontSize          = 11,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(8, 0, 0, 0)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(chip, 0);
        Grid.SetColumn(titleTb, 1);
        Grid.SetColumn(timeTb, 2);
        headerGrid.Children.Add(chip);
        headerGrid.Children.Add(titleTb);
        headerGrid.Children.Add(timeTb);

        var content = new StackPanel { Margin = new Thickness(0, 10, 0, 10) };
        content.Children.Add(headerGrid);

        if (reason != null)
        {
            content.Children.Add(new TextBlock
            {
                Text         = reason,
                FontSize     = 11,
                Foreground   = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0)
            });
        }

        return content;
    }

    private static string FormatRelativeTime(DateTimeOffset local, DateTimeOffset now)
    {
        double minutesAgo = (now - local).TotalMinutes;
        if (minutesAgo < 60) return $"{(int)minutesAgo}m ago";
        if (minutesAgo < 60 * 24) return $"{(int)(minutesAgo / 60)}h ago";
        if (local.Date == now.Date.AddDays(-1)) return $"Yesterday {local:HH:mm}";
        return local.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    }

    // ── Category color / label helpers ──────────────────────────────────────

    private static Color GetActionCategoryColor(ActionCategory cat)
    {
        return cat switch
        {
            ActionCategory.Breaks        => Color.FromRgb(0x3B, 0x82, 0xF6), // blue
            ActionCategory.Focus         => Color.FromRgb(0x10, 0xB9, 0x81), // emerald
            ActionCategory.Notifications => Color.FromRgb(0xF5, 0x9E, 0x0B), // amber
            ActionCategory.Protection    => Color.FromRgb(0x8B, 0x5C, 0xF6), // violet
            _                            => Color.FromRgb(0x64, 0x74, 0x8B), // slate
        };
    }

    private static string GetActionCategoryLabel(ActionCategory cat)
    {
        return cat switch
        {
            ActionCategory.Breaks        => "Breaks",
            ActionCategory.Focus         => "Focus mode",
            ActionCategory.Notifications => "Notifications",
            ActionCategory.Protection    => "App restrictions",
            _                            => "Other",
        };
    }

    // ── Visual section builders ──────────────────────────────────────────────

    /// <summary>
    /// Section 1 — donut ring chart (left) + legend (right) showing category breakdown.
    /// </summary>
    private FrameworkElement BuildActionsCategoryBreakdown(List<LocalApiEventResponse> events)
    {
        var cardBrush   = (TryFindResource("PortaCardBrush")       as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x18));
        var borderBrush = (TryFindResource("PortaCardBorderBrush") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35));
        var container = new Border
        {
            Background      = new SolidColorBrush(cardBrush.Color),
            BorderBrush     = new SolidColorBrush(borderBrush.Color),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(16)
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text       = "ACTIVITY BREAKDOWN",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin     = new Thickness(0, 0, 0, 14)
        });

        int total = Math.Max(events.Count, 1);
        var groups = new[]
        {
            ActionCategory.Focus, ActionCategory.Protection,
            ActionCategory.Breaks, ActionCategory.Notifications, ActionCategory.Other
        }
        .Select(cat => new
        {
            Category = cat,
            Count    = events.Count(e => ResolveActionCategory(e) == cat),
            Color    = GetActionCategoryColor(cat),
            Label    = GetActionCategoryLabel(cat)
        })
        .Where(g => g.Count > 0)
        .OrderByDescending(g => g.Count)
        .ToList();

        // ── Two-column layout: donut (left) + legend (right) ────────
        var bodyGrid = new Grid();
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Donut canvas ─────────────────────────────────────────────
        const double cx = 60d, cy = 60d, outerR = 52d, innerR = 33d;
        var donutHost = new Grid { Width = 130, Height = 120, VerticalAlignment = VerticalAlignment.Center };

        var canvas = new Canvas { Width = 130, Height = 120 };

        if (groups.Count > 0)
        {
            double gapDeg      = groups.Count > 1 ? 2.5 : 0.0;
            double totalSweep  = 360.0 - groups.Count * gapDeg;
            double startAngle  = -90.0; // top of circle

            foreach (var g in groups)
            {
                double sweep = (double)g.Count / total * totalSweep;
                if (sweep < 0.5) { startAngle += gapDeg; continue; }

                var seg = CreateDonutSegmentPath(cx, cy, outerR, innerR, startAngle, sweep);
                seg.Fill = new SolidColorBrush(g.Color);
                canvas.Children.Add(seg);
                startAngle += sweep + gapDeg;
            }
        }
        else
        {
            // Empty state: grey ring
            var emptyRing = CreateDonutSegmentPath(cx, cy, outerR, innerR, -90, 359.99);
            emptyRing.Fill = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35));
            canvas.Children.Add(emptyRing);
        }

        donutHost.Children.Add(canvas);

        // Center text: total count + "actions"
        var centerStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center
        };
        centerStack.Children.Add(new TextBlock
        {
            Text                = total.ToString(CultureInfo.InvariantCulture),
            FontSize            = 22,
            FontWeight          = FontWeights.Bold,
            Foreground          = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        centerStack.Children.Add(new TextBlock
        {
            Text                = "actions",
            FontSize            = 10,
            Foreground          = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        donutHost.Children.Add(centerStack);

        Grid.SetColumn(donutHost, 0);
        bodyGrid.Children.Add(donutHost);

        // ── Legend ────────────────────────────────────────────────────
        var legend = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(14, 0, 0, 0)
        };

        foreach (var g in groups)
        {
            double pct = g.Count * 100d / total;

            // Compact horizontal row — no star column, everything clusters on the left
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 0, 0, 10),
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(new Border
            {
                Width             = 10,
                Height            = 10,
                CornerRadius      = new CornerRadius(5),
                Background        = new SolidColorBrush(g.Color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0)
            });

            row.Children.Add(new TextBlock
            {
                Text              = g.Label,
                FontSize          = 12,
                Foreground        = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 8, 0)
            });

            row.Children.Add(new Border
            {
                Background        = new SolidColorBrush(
                                        Color.FromArgb(0x33, g.Color.R, g.Color.G, g.Color.B)),
                CornerRadius      = new CornerRadius(10),
                Padding           = new Thickness(8, 2, 8, 2),
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child             = new TextBlock
                {
                    Text       = $"× {g.Count}",
                    FontSize   = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(g.Color)
                }
            });

            row.Children.Add(new TextBlock
            {
                Text              = $"{pct:0}%",
                FontSize          = 11,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                VerticalAlignment = VerticalAlignment.Center
            });

            legend.Children.Add(row);
        }

        Grid.SetColumn(legend, 1);
        bodyGrid.Children.Add(legend);

        content.Children.Add(bodyGrid);
        container.Child = content;
        return container;
    }

    /// <summary>
    /// Builds a WPF Path that draws one donut arc segment.
    /// startDeg = -90 puts the first segment at the top (12 o'clock).
    /// </summary>
    private static ShapePath CreateDonutSegmentPath(
        double cx, double cy, double outerR, double innerR,
        double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 360) sweepDeg = 359.99;

        double startRad = startDeg * Math.PI / 180.0;
        double endRad   = (startDeg + sweepDeg) * Math.PI / 180.0;

        var outerStart = new Point(cx + outerR * Math.Cos(startRad), cy + outerR * Math.Sin(startRad));
        var outerEnd   = new Point(cx + outerR * Math.Cos(endRad),   cy + outerR * Math.Sin(endRad));
        var innerEnd   = new Point(cx + innerR * Math.Cos(endRad),   cy + innerR * Math.Sin(endRad));
        var innerStart = new Point(cx + innerR * Math.Cos(startRad), cy + innerR * Math.Sin(startRad));

        bool largeArc = sweepDeg > 180;

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true };
        figure.Segments.Add(new ArcSegment
        {
            Point          = outerEnd,
            Size           = new Size(outerR, outerR),
            IsLargeArc     = largeArc,
            SweepDirection = SweepDirection.Clockwise
        });
        figure.Segments.Add(new LineSegment { Point = innerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point          = innerStart,
            Size           = new Size(innerR, innerR),
            IsLargeArc     = largeArc,
            SweepDirection = SweepDirection.Counterclockwise
        });

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        return new ShapePath { Data = geom };
    }

    /// <summary>
    /// Section 2 — 24-bar hourly activity chart showing when Ari was most active.
    /// </summary>
    private FrameworkElement BuildActionsHourlyActivity(List<LocalApiEventResponse> events)
    {
        var cardBrush   = (TryFindResource("PortaCardBrush")       as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x18));
        var borderBrush = (TryFindResource("PortaCardBorderBrush") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35));
        var container = new Border
        {
            Background      = new SolidColorBrush(cardBrush.Color),
            BorderBrush     = new SolidColorBrush(borderBrush.Color),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(16)
        };

        var content = new StackPanel();

        content.Children.Add(new TextBlock
        {
            Text       = "ACTIVITY BY HOUR",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin     = new Thickness(0, 0, 0, 10)
        });

        // Count events per hour
        int[] hourCounts = new int[24];
        foreach (var ev in events)
            hourCounts[ev.TimestampUtc.ToLocalTime().Hour]++;

        int maxCount = Math.Max(1, hourCounts.Max());
        const double barAreaH = 48d;
        const double labelH   = 14d;

        // 24-column grid; each cell has a fixed bar-area row + label row
        var outerGrid = new Grid { Height = barAreaH + labelH };
        for (int h = 0; h < 24; h++)
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition());

        for (int h = 0; h < 24; h++)
        {
            int    count = hourCounts[h];
            double barH  = count == 0 ? 0d : Math.Max(3d, (double)count / maxCount * barAreaH);

            // Each column: a grid with two rows — bar area (bottom-aligned) + label
            var cellGrid = new Grid();
            cellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(barAreaH) });
            cellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(labelH) });

            if (barH > 0)
            {
                // Colour: violet for work hours (6–22), slate for night (0–5, 23)
                Color barColor = h is >= 6 and <= 22
                    ? Color.FromArgb(0xCC, 0x63, 0x66, 0xF1)   // indigo
                    : Color.FromArgb(0x88, 0x47, 0x55, 0x69);  // slate

                var bar = new Border
                {
                    Height            = barH,
                    Background        = new SolidColorBrush(barColor),
                    CornerRadius      = new CornerRadius(2, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin            = new Thickness(1, 0, 1, 0),
                    ToolTip           = $"{h:00}:00 — {count} action{(count == 1 ? "" : "s")}"
                };
                Grid.SetRow(bar, 0);
                cellGrid.Children.Add(bar);
            }

            bool showLabel = h % 6 == 0 || h == 23;
            var hourLabel = new TextBlock
            {
                Text                = showLabel ? $"{h}" : "",
                FontSize            = 8.5,
                Foreground          = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Top,
                Margin              = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(hourLabel, 1);
            cellGrid.Children.Add(hourLabel);

            Grid.SetColumn(cellGrid, h);
            outerGrid.Children.Add(cellGrid);
        }

        content.Children.Add(outerGrid);
        container.Child = content;
        return container;
    }

    /// <summary>
    /// Maps any event to a short, user-friendly title — no internal jargon.
    /// </summary>
    private static string GetUserFriendlyActionTitle(LocalApiEventResponse ev)
    {
        string action = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;

        // ── Inform / alert events ────────────────────────────────────
        return action switch
        {
            "inform_break_due"        => "Break reminder",
            "inform_overload_watch"   => "Overload detected",
            "inform_overload_rising"  => "Overload rising",
            "inform_pressure_rising"  => "Pressure rising",
            "inform_pressure_watch"   => "Pressure watch",
            "inform_focus_lock_in"    => "Focus coaching nudge",
            "inform_recovery_needed"  => "Recovery needed",
            _ when action.StartsWith("inform_", StringComparison.Ordinal)
                                      => "Ari alert",

            // ── Break events ─────────────────────────────────────────
            _ when PortaBreakSemantics.IsBreakSuggestionAction(action)
                                      => BuildSuggestionTitle(ev.Action),
            _ when action.Contains("break", StringComparison.Ordinal)
                                      => "Break suggested",

            // ── WhatChanged field — strip verbose boilerplate ────────
            _ => SimplifyWhatChanged(
                    string.IsNullOrWhiteSpace(ev.WhatChanged)
                        ? HumanizeActionName(ev.Action)
                        : ev.WhatChanged)
        };
    }

    /// <summary>
    /// Strips internal boilerplate from WhatChanged strings and returns a clean label.
    /// </summary>
    private static string SimplifyWhatChanged(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Action recorded";

        // ── Focus mode (matches both WhatChanged text and HumanizeActionName output) ──
        bool hasFocus = raw.Contains("focus mode", StringComparison.OrdinalIgnoreCase)
                     || raw.Contains("focus_mode", StringComparison.OrdinalIgnoreCase);
        if (hasFocus)
        {
            return raw.Contains("ended", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("end",   StringComparison.OrdinalIgnoreCase)
                ? "Focus mode ended"
                : "Focus mode activated";
        }

        // ── App restrictions ──────────────────────────────────────────────────────────
        bool hasRestriction = raw.Contains("app restriction", StringComparison.OrdinalIgnoreCase)
                           || raw.Contains("app_restriction", StringComparison.OrdinalIgnoreCase)
                           || raw.Contains("temporary app",   StringComparison.OrdinalIgnoreCase)
                           || raw.Contains("temporary_app",   StringComparison.OrdinalIgnoreCase);
        if (hasRestriction)
        {
            return raw.Contains("ended", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("end",   StringComparison.OrdinalIgnoreCase)
                ? "App restriction ended"
                : "App restricted";
        }

        // ── Other known patterns ──────────────────────────────────────────────────────
        if (raw.Contains("Prompt snoozed",              StringComparison.OrdinalIgnoreCase)) return "Prompt snoozed";
        if (raw.Contains("Short break overlay",         StringComparison.OrdinalIgnoreCase)) return "Short break shown";
        if (raw.Contains("Short break suggestion",      StringComparison.OrdinalIgnoreCase)) return "Short break suggested";
        if (raw.Contains("short_break",                 StringComparison.OrdinalIgnoreCase)) return "Short break";
        if (raw.Contains("Notification reduction",      StringComparison.OrdinalIgnoreCase)) return "Notifications reduced";
        if (raw.Contains("notification_reduction",      StringComparison.OrdinalIgnoreCase)) return "Notifications reduced";
        if (raw.Contains("Animation reduction",         StringComparison.OrdinalIgnoreCase)) return "Animations reduced";
        if (raw.Contains("animation_reduction",         StringComparison.OrdinalIgnoreCase)) return "Animations reduced";
        if (raw.Contains("Brightness",                  StringComparison.OrdinalIgnoreCase)) return "Brightness adjusted";

        // ── Strip the verbose "No concrete desktop changes" suffix ────────────────────
        int idx = raw.IndexOf(". No concrete", StringComparison.OrdinalIgnoreCase);
        if (idx > 10) raw = raw[..idx];

        // ── Replace underscores, trim, and capitalise first letter ────────────────────
        raw = raw.Replace('_', ' ').Trim().TrimEnd('.');
        if (raw.Length > 0) raw = char.ToUpperInvariant(raw[0]) + raw[1..];
        return raw.Length > 60 ? raw[..57] + "…" : raw;
    }

    /// <summary>
    /// Section 3 — grouped feed: one row per unique friendly title, count badge, last seen.
    /// Replaces the old 47-identical-row list.
    /// </summary>
    /// <summary>
    /// Groups events by ActionCategory + 5-minute time proximity windows.
    /// Related events fired close together (e.g. break_started, break_reminder, break_ended)
    /// collapse into a single summary row showing the category label and a count badge.
    /// </summary>
        /// <summary>
    /// Returns true for events that are purely internal state transitions,
    /// not user-facing actions. These are excluded from counts and the log.
    /// </summary>
    private static bool IsInternalStateTransition(LocalApiEventResponse ev)
    {
        string a = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        return a is "break_started" or "break_ended"
               or "overlay_started" or "overlay_ended" or "overlay_recovered"
               or "accepted" or "dismissed"
               or "break_accepted" or "break_dismissed"
               or "suggest_break" or "break_active" or "break_inactive"
               or "break_recommended" or "break_triggered";
    }

    /// <summary>
    /// Returns true for high-frequency engine heartbeat or internal recording events
    /// that are not meaningful as individual user-visible actions.
    /// Uses prefix/substring matching so any future engine event with these patterns
    /// is automatically excluded without needing a list update.
    /// </summary>
    private static bool IsEngineHeartbeatEvent(LocalApiEventResponse ev)
    {
        string a = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(a)) return false;

        // Exact known heartbeat names
        if (a is "focus_assist_enabled" or "focus_assist_disabled"
               or "deep_work_notifications_suppressed" or "deep_work_notifications_restored"
               or "focus_mode_nudge" or "focus_mode_enabled" or "focus_mode_disabled"
               or "suppress_notifications" or "restore_notifications")
            return true;

        // Pattern-based: any engine internal recording/intent/signal
        // These fire constantly on analysis buckets — never a single user action
        if (a.Contains("policy_intent", StringComparison.Ordinal)) return true;
        if (a.Contains("_intent_recorded", StringComparison.Ordinal)) return true;
        if (a.Contains("_heartbeat", StringComparison.Ordinal)) return true;
        if (a.Contains("_bucket", StringComparison.Ordinal)) return true;
        if (a.Contains("_signal", StringComparison.Ordinal)) return true;
        if (a.Contains("_tick", StringComparison.Ordinal)) return true;

        // Prefix-based: entire families of continuous engine events
        if (a.StartsWith("focus_assist_", StringComparison.Ordinal)) return true;
        if (a.StartsWith("deep_work_", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// Groups events by ActionCategory.
    /// Before grouping:
    ///   1. Strips internal state transitions (break_started/ended, accepted, etc.)
    ///   2. Strips high-frequency engine heartbeat events (focus_assist_enabled, deep_work_*)
    ///   3. Deduplicates: same action within 5 minutes = 1 occurrence (engine fires many times)
    /// Result: one row per category, sorted by most recent.
    /// </summary>
    private static List<(string Title, int Count, ActionCategory Category, DateTimeOffset Latest)>
        CollapseByTimeAndCategory(List<LocalApiEventResponse> events)
    {
        // Step 1: strip internal state + engine heartbeat events
        var filtered = events
            .Where(e => !IsInternalStateTransition(e) && !IsEngineHeartbeatEvent(e))
            .ToList();

        // Step 2: deduplicate — same action within 30 minutes = 1 occurrence.
        // 30 minutes covers a full break or focus session so related firings collapse
        // into one entry rather than splitting across multiple groups.
        var deduped = new List<LocalApiEventResponse>();
        var lastByAction = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in filtered.OrderBy(e => e.TimestampUtc))
        {
            string key = ev.Action?.ToLowerInvariant() ?? "";
            if (lastByAction.TryGetValue(key, out var prev)
                && (ev.TimestampUtc - prev).TotalMinutes < 5d)
            {
                continue; // collapse rapid-fire or session-scoped duplicate
            }
            lastByAction[key] = ev.TimestampUtc;
            deduped.Add(ev);
        }

        // Step 3: keep each deduplicated event as its own row for full-day visibility
        return deduped
            .OrderByDescending(e => e.TimestampUtc)
            .Select(e =>
            {
                string title = GetUserFriendlyActionTitle(e);
                var cat = ResolveActionCategory(e);
                return (title, 1, cat, e.TimestampUtc);
            })
            .ToList();
    }
    private FrameworkElement BuildActionsGroupedLog(
        List<LocalApiEventResponse> events, DateTimeOffset now)
    {
        var cardBrush   = (TryFindResource("PortaCardBrush")       as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x18));
        var borderBrush = (TryFindResource("PortaCardBorderBrush") as SolidColorBrush) ?? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35));
        var container = new Border
        {
            Background      = new SolidColorBrush(cardBrush.Color),
            BorderBrush     = new SolidColorBrush(borderBrush.Color),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(10),
            Padding         = new Thickness(16)
        };

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text       = "WHAT HAPPENED",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            Margin     = new Thickness(0, 0, 0, 12)
        });

        // Group by category + 5-minute time windows so related events (break_started,
        // break_reminder, break_ended) collapse into a single row with a count.
        var groups = CollapseByTimeAndCategory(events)
            .OrderByDescending(g => g.Latest)
            .ToList();

        bool firstGroup = true;
        foreach (var g in groups)
        {
            if (!firstGroup)
            {
                content.Children.Add(new Border
                {
                    Height     = 1,
                    Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
                    Margin     = new Thickness(0)
                });
            }
            firstGroup = false;

            Color  catColor = GetActionCategoryColor(g.Category);
            string timeAgo  = FormatRelativeTime(g.Latest.ToLocalTime(), now);

            var row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // dot
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // title
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // badge
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // time

            var dot = new Border
            {
                Width             = 10,
                Height            = 10,
                CornerRadius      = new CornerRadius(5),
                Background        = new SolidColorBrush(catColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            var titleTb = new TextBlock
            {
                Text              = g.Title,
                FontSize          = 13,
                FontWeight        = FontWeights.Medium,
                Foreground        = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)),
                TextTrimming      = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleTb, 1);

            if (g.Count > 1)
            {
                var badge = new Border
                {
                    Background        = new SolidColorBrush(
                                            Color.FromArgb(0x33, catColor.R, catColor.G, catColor.B)),
                    CornerRadius      = new CornerRadius(10),
                    Padding           = new Thickness(8, 2, 8, 2),
                    Margin            = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child             = new TextBlock
                    {
                        Text       = $"× {g.Count}",
                        FontSize   = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(catColor)
                    }
                };
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
            }

            var timeTb = new TextBlock
            {
                Text              = timeAgo,
                FontSize          = 11,
                Foreground        = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(timeTb, 3);

            row.Children.Add(dot);
            row.Children.Add(titleTb);
            row.Children.Add(timeTb);
            content.Children.Add(row);
        }

        container.Child = content;
        return container;
    }

    private void PopulateActionsOverview(IReadOnlyList<LocalApiEventResponse> ordered)
    {
        ActionsTotalCountText.Text = ordered.Count.ToString(CultureInfo.InvariantCulture);
        ActionsLastSeenText.Text = ordered.Count == 0
            ? "No recent actions"
            : BuildActionLastSeenText(ordered[0]);
        ActionsSummaryLeadText.Text = BuildActionsLeadText(ordered);

        RenderActionsTimeline(ordered);
        UpdateActionCategoryBreakdown(ordered);
    }

    private void RenderActionsTimeline(IReadOnlyList<LocalApiEventResponse> ordered)
    {
        ActionsTimelineStripPanel.Children.Clear();
        if (ordered.Count == 0)
        {
            ActionsTimelineEmptyText.Visibility = Visibility.Visible;
            return;
        }

        ActionsTimelineEmptyText.Visibility = Visibility.Collapsed;
        IReadOnlyList<LocalApiEventResponse> timelineItems = ordered
            .Take(12)
            .Reverse()
            .ToList();

        foreach (LocalApiEventResponse ev in timelineItems)
        {
            ActionsTimelineStripPanel.Children.Add(BuildTimelineActionChip(ev));
        }
    }

    private FrameworkElement BuildTimelineActionChip(LocalApiEventResponse ev)
    {
        ActionCategory category = ResolveActionCategory(ev);
        Brush background = GetActionCategoryBrush(category);
        Brush border = GetActionCategoryBorderBrush(category);
        StackPanel stack = new()
        {
            Margin = new Thickness(0, 0, 10, 0),
            Width = 62
        };

        Border chip = new()
        {
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Height = 34,
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = $"{BuildActionTimelineTitle(ev)}{Environment.NewLine}{ev.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{BuildActionTimelineSubtitle(ev)}"
        };
        chip.Child = new TextBlock
        {
            Text = GetActionCategoryShortLabel(category),
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        stack.Children.Add(chip);
        stack.Children.Add(new TextBlock
        {
            Text = BuildRelativeTimeLabel(ev.TimestampUtc),
            Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontSize = 10.5,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        return stack;
    }

    private void UpdateActionCategoryBreakdown(IReadOnlyList<LocalApiEventResponse> ordered)
    {
        int total = Math.Max(ordered.Count, 1);
        int breakCount = ordered.Count(ev => ResolveActionCategory(ev) == ActionCategory.Breaks);
        int focusCount = ordered.Count(ev => ResolveActionCategory(ev) == ActionCategory.Focus);
        int notificationCount = ordered.Count(ev => ResolveActionCategory(ev) == ActionCategory.Notifications);
        int protectionCount = ordered.Count(ev => ResolveActionCategory(ev) == ActionCategory.Protection);
        int otherCount = ordered.Count(ev => ResolveActionCategory(ev) == ActionCategory.Other);

        SetActionCategoryBar(ActionCategoryBreakFill, ActionCategoryBreakValue, breakCount, total);
        SetActionCategoryBar(ActionCategoryFocusFill, ActionCategoryFocusValue, focusCount, total);
        SetActionCategoryBar(ActionCategoryNotificationsFill, ActionCategoryNotificationsValue, notificationCount, total);
        SetActionCategoryBar(ActionCategoryProtectionFill, ActionCategoryProtectionValue, protectionCount, total);
        SetActionCategoryBar(ActionCategoryOtherFill, ActionCategoryOtherValue, otherCount, total);
    }

    private static void SetActionCategoryBar(Border fill, TextBlock valueText, int count, int total)
    {
        double ratio = total <= 0 ? 0d : (double)count / total;
        fill.Width = ActionCategoryBarWidth * ratio;
        valueText.Text = total <= 0
            ? "0%"
            : $"{Math.Round(ratio * 100d, MidpointRounding.AwayFromZero):0}%";
    }

    private static string BuildActionLastSeenText(LocalApiEventResponse ev)
    {
        return ActionsPageCopyBuilder.BuildLastSeenText(
            isSuggestion: IsSuggestionEvent(ev.Action),
            title: BuildActionTimelineTitle(ev),
            relativeTimeText: BuildRelativeTimeLabel(ev.TimestampUtc));
    }

    private static string BuildActionTimelineTitle(LocalApiEventResponse ev)
    {
        return IsSuggestionEvent(ev.Action)
            ? BuildSuggestionTitle(ev.Action)
            : (string.IsNullOrWhiteSpace(ev.WhatChanged) ? HumanizeActionName(ev.Action) : ev.WhatChanged);
    }

    private static string BuildActionTimelineSubtitle(LocalApiEventResponse ev)
    {
        return IsSuggestionEvent(ev.Action)
            ? BuildSuggestionShortWhy(ev)
            : BuildChangeShortWhy(ev);
    }

    private static string BuildRelativeTimeLabel(DateTimeOffset timestampUtc)
    {
        TimeSpan age = DateTimeOffset.UtcNow - timestampUtc;
        if (age <= TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes, MidpointRounding.AwayFromZero))}m";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)Math.Round(age.TotalHours, MidpointRounding.AwayFromZero))}h";
        }

        return $"{Math.Max(1, (int)Math.Round(age.TotalDays, MidpointRounding.AwayFromZero))}d";
    }

    private static ActionCategory ResolveActionCategory(LocalApiEventResponse ev)
    {
        string action = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;

        // All inform_* events are Ari alerts / notifications
        if (action.StartsWith("inform_", StringComparison.Ordinal))
            return ActionCategory.Notifications;

        if (PortaBreakSemantics.IsBreakSuggestionAction(action)
            || action.Contains("break", StringComparison.Ordinal))
        {
            return ActionCategory.Breaks;
        }

        if (action.Contains("focus", StringComparison.Ordinal))
        {
            return ActionCategory.Focus;
        }

        if (action.Contains("notification", StringComparison.Ordinal)
            || action.Contains("alert", StringComparison.Ordinal))
        {
            return ActionCategory.Notifications;
        }

        if (action.Contains("restrict", StringComparison.Ordinal)
            || action.Contains("app", StringComparison.Ordinal)
            || action.Contains("animation", StringComparison.Ordinal)
            || action.Contains("brightness", StringComparison.Ordinal))
        {
            return ActionCategory.Protection;
        }

        return ActionCategory.Other;
    }

    private static string GetActionCategoryShortLabel(ActionCategory category)
    {
        return category switch
        {
            ActionCategory.Breaks => "Break",
            ActionCategory.Focus => "Focus",
            ActionCategory.Notifications => "Alert",
            ActionCategory.Protection => "Protect",
            _ => "Other"
        };
    }

    private static Brush GetActionCategoryBrush(ActionCategory category)
    {
        return category switch
        {
            ActionCategory.Breaks => new SolidColorBrush(Color.FromRgb(30, 58, 138)),
            ActionCategory.Focus => new SolidColorBrush(Color.FromRgb(6, 95, 70)),
            ActionCategory.Notifications => new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            ActionCategory.Protection => new SolidColorBrush(Color.FromRgb(91, 33, 182)),
            _ => new SolidColorBrush(Color.FromRgb(51, 65, 85))
        };
    }

    private static Brush GetActionCategoryBorderBrush(ActionCategory category)
    {
        return category switch
        {
            ActionCategory.Breaks => new SolidColorBrush(Color.FromRgb(96, 165, 250)),
            ActionCategory.Focus => new SolidColorBrush(Color.FromRgb(74, 222, 128)),
            ActionCategory.Notifications => new SolidColorBrush(Color.FromRgb(251, 191, 36)),
            ActionCategory.Protection => new SolidColorBrush(Color.FromRgb(196, 181, 253)),
            _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
        };
    }

    private void SuggestionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestionsListView.SelectedItem is SuggestionRow row)
        {
            _actionsView = ActionsView.Suggestions;
            UpdateActionsTabControls();
            ChangesListView.SelectedItem = null;
            _selectedIntervention = row.Source;
            UpdateInterventionDrawer(row.Source);
            return;
        }

        if (ChangesListView.SelectedItem == null)
        {
            _selectedIntervention = null;
            UpdateInterventionDrawer(null);
        }
    }

    private void SuggestionsListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleNestedListViewMouseWheel(sender, e);
    }

    private void ChangesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChangesListView.SelectedItem is ChangeRow row)
        {
            _actionsView = ActionsView.Changes;
            UpdateActionsTabControls();
            SuggestionsListView.SelectedItem = null;
            _selectedIntervention = row.Source;
            UpdateInterventionDrawer(row.Source);
            return;
        }

        if (SuggestionsListView.SelectedItem == null)
        {
            _selectedIntervention = null;
            UpdateInterventionDrawer(null);
        }
    }

    private void ChangesListView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HandleNestedListViewMouseWheel(sender, e);
    }

    private void ActionDetailsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (TryScrollViewerByMouseWheel(scrollViewer, e.Delta))
        {
            e.Handled = true;
            return;
        }

        if (FindAncestor<ScrollViewer>(scrollViewer) is ScrollViewer parentScrollViewer
            && TryScrollViewerByMouseWheel(parentScrollViewer, e.Delta))
        {
            e.Handled = true;
        }
    }

    private static void HandleNestedListViewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListView listView)
        {
            return;
        }

        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(listView);
        if (scrollViewer != null && TryScrollViewerByMouseWheel(scrollViewer, e.Delta))
        {
            e.Handled = true;
            return;
        }

        if (FindAncestor<ScrollViewer>(listView) is ScrollViewer parentScrollViewer
            && TryScrollViewerByMouseWheel(parentScrollViewer, e.Delta))
        {
            e.Handled = true;
        }
    }

    private void InitializeUnifiedScrollBehavior()
    {
        RegisterUnifiedScroll(OverviewPanel);
        RegisterUnifiedScroll(TasksPanel);
        RegisterUnifiedScroll(ProjectsPanel);
        RegisterUnifiedScroll(ReportsPanel);
        RegisterUnifiedScroll(ActionDetailsScrollViewer);
        RegisterUnifiedScroll(SettingsContentScrollViewer);
        RegisterUnifiedScroll(StatesPanel);
        RegisterUnifiedScroll(TimelinePanel);
        RegisterUnifiedScroll(AccountContentScrollViewer);
        RegisterUnifiedScroll(DiagnosticsPanel);
    }

    private static void RegisterUnifiedScroll(ScrollViewer? scrollViewer)
    {
        if (scrollViewer == null)
        {
            return;
        }

        scrollViewer.PanningMode = PanningMode.VerticalFirst;
        scrollViewer.MouseWheel -= UnifiedScrollViewer_MouseWheel;
        scrollViewer.MouseWheel += UnifiedScrollViewer_MouseWheel;
    }

    private static void UnifiedScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (TryScrollViewerByMouseWheel(scrollViewer, e.Delta))
        {
            e.Handled = true;
        }
    }

    private static bool TryScrollViewerByMouseWheel(ScrollViewer scrollViewer, int delta)
    {
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return false;
        }

        double distance = ComputeWheelScrollDistance(scrollViewer, delta);
        double nextOffset = Math.Clamp(
            scrollViewer.VerticalOffset - (Math.Sign(delta) * distance),
            0d,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(nextOffset - scrollViewer.VerticalOffset) < 0.5d)
        {
            return false;
        }

        scrollViewer.ScrollToVerticalOffset(nextOffset);
        return true;
    }

    private static double ComputeWheelScrollDistance(ScrollViewer scrollViewer, int delta)
    {
        double multiplier = Math.Max(1d, Math.Abs(delta) / 120d);
        double baseDistance = SystemParameters.WheelScrollLines < 0
            ? Math.Max(60d, scrollViewer.ViewportHeight * 0.9d)
            : Math.Max(56d, SystemParameters.WheelScrollLines * WheelScrollPixelsPerLine);
        return baseDistance * multiplier;
    }

    private void UpdateInterventionDrawer(LocalApiEventResponse? item)
    {
        if (item == null)
        {
            ActionDrawerTitle.Text = ActionsPageCopyBuilder.BuildEmptyDrawerTitle();
            ActionDrawerWhen.Text = ActionsPageCopyBuilder.BuildEmptyDrawerWhen();
            ActionDrawerClassification.Text = $"Type: {NotAvailableText}";
            ActionDrawerDecisionState.Text = $"Decision: {NotAvailableText}";
            ActionDrawerExecutionState.Text = $"Execution: {NotAvailableText}";
            ActionDrawerClassification.Visibility = Visibility.Collapsed;
            ActionDrawerDecisionState.Visibility = Visibility.Collapsed;
            ActionDrawerExecutionState.Visibility = Visibility.Collapsed;
            ActionDrawerShortWhy.Text = ActionsPageCopyBuilder.BuildEmptyDrawerWhy();
            ActionDrawerCooldown.Text = $"Cooldown remaining: {NotAvailableText}";
            ActionDrawerRepeat.Text = string.Empty;
            ActionDrawerRepeat.Visibility = Visibility.Collapsed;
            ActionDrawerWhatChanged.Text = ActionsPageCopyBuilder.BuildEmptyDrawerOutcome();
            ActionDrawerUndo.Text = ActionsPageCopyBuilder.BuildEmptyDrawerUndo();
            ActionDrawerUndo.Visibility = Visibility.Visible;
            ActionDrawerWhyJson.Text = NotAvailableText;
            ActionDrawerUndoButton.IsEnabled = false;
            ActionDrawerUndoButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool isSuggestion = IsSuggestionEvent(item.Action);
        string actionSummary = isSuggestion
            ? BuildSuggestionTitle(item.Action)
            : (string.IsNullOrWhiteSpace(item.WhatChanged) ? HumanizeActionName(item.Action) : item.WhatChanged);
        ActionDrawerTitle.Text = actionSummary;
        ActionDrawerWhen.Text = isSuggestion
            ? $"Suggested at: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"Changed at: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        ActionDrawerClassification.Text = BuildActionClassificationText(item);
        ActionDrawerDecisionState.Text = BuildDecisionStateText(item);
        ActionDrawerExecutionState.Text = BuildExecutionStateText(item);
        ActionDrawerClassification.Visibility = Visibility.Collapsed;
        ActionDrawerDecisionState.Visibility = Visibility.Collapsed;
        ActionDrawerExecutionState.Visibility = Visibility.Collapsed;
        ActionDrawerShortWhy.Text = ActionsPageCopyBuilder.BuildDrawerWhyText(
            isSuggestion
                ? BuildSuggestionShortWhy(item)
                : BuildChangeShortWhy(item));
        ActionDrawerCooldown.Text = $"Cooldown remaining: {FormatCooldownRemaining(item.CooldownEndTimestampUtc)}";
        ActionDrawerRepeat.Text = isSuggestion
            ? BuildSuggestionRepeatLine(item)
            : string.Empty;
        ActionDrawerRepeat.Visibility = isSuggestion ? Visibility.Visible : Visibility.Collapsed;
        ActionDrawerWhatChanged.Text = ActionsPageCopyBuilder.BuildDrawerOutcomeText(isSuggestion, actionSummary);
        ActionDrawerUndo.Text = ActionsPageCopyBuilder.BuildDrawerUndoText(isSuggestion, BuildUndoDetails(item));
        ActionDrawerUndo.Visibility = isSuggestion ? Visibility.Collapsed : Visibility.Visible;
        ActionDrawerWhyJson.Text = FormatWhyJsonForDisplay(item.WhyJson);
        ActionDrawerUndoButton.IsEnabled = !isSuggestion && item.Reversible;
        ActionDrawerUndoButton.Visibility = !isSuggestion && item.Reversible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void InterventionCategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        await SaveSettingsAsync("intervention_category_toggle").ConfigureAwait(true);
    }

    private async void ActionDrawerUndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedIntervention == null || !_selectedIntervention.Reversible)
        {
            return;
        }

        string action = _selectedIntervention.Action.ToLowerInvariant();
        if (action.Contains("notification", StringComparison.Ordinal))
        {
            InterventionNotificationToggle.IsChecked = !(InterventionNotificationToggle.IsChecked ?? false);
        }
        else if (action.Contains("focus", StringComparison.Ordinal))
        {
            InterventionFocusModeToggle.IsChecked = !(InterventionFocusModeToggle.IsChecked ?? false);
        }
        else if (action.Contains("animation", StringComparison.Ordinal))
        {
            InterventionAnimationToggle.IsChecked = !(InterventionAnimationToggle.IsChecked ?? false);
        }
        else if (action.Contains("app", StringComparison.Ordinal) || action.Contains("restrict", StringComparison.Ordinal))
        {
            InterventionAppRestrictionToggle.IsChecked = !(InterventionAppRestrictionToggle.IsChecked ?? false);
        }
        else
        {
            MessageBox.Show(
                "Undo is not mapped for this reversible action yet.",
                "PORTA Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await SaveSettingsAsync("intervention_reversible_toggle").ConfigureAwait(true);
    }

    private static bool IsSuggestionEvent(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        string normalized = action.Trim().ToLowerInvariant();
        return normalized.EndsWith("_suggestion", StringComparison.Ordinal)
            || normalized.Contains("suggestion", StringComparison.Ordinal);
    }

    private static bool IsSystemChangeEvent(LocalApiEventResponse ev)
    {
        if (IsSuggestionEvent(ev.Action))
        {
            return false;
        }

        string action = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (action.StartsWith("inform_", StringComparison.Ordinal))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(ev.WhatChanged)
            || !string.IsNullOrWhiteSpace(action);
    }

    private static bool IsUserFacingAlert(LocalApiEventResponse ev)
    {
        string action = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!action.StartsWith("inform_", StringComparison.Ordinal))
            return false;

        bool isKnownUseful = action is
            "inform_overload_watch" or
            "inform_pressure_rising" or
            // inform_break_due excluded: the user sees show_break_overlay, not this internal signal
            "inform_focus_lock_in" or
            "inform_overload_rising" or
            "inform_pressure_watch" or
            "inform_recovery_needed";

        bool hasUserReason = !string.IsNullOrWhiteSpace(ev.Reason)
            && ev.Reason.Length > 10
            && !ev.Reason.Contains("null", StringComparison.OrdinalIgnoreCase)
            && !ev.Reason.Contains("error", StringComparison.OrdinalIgnoreCase);

        return isKnownUseful || hasUserReason;
    }

    private static string BuildAlertTitle(LocalApiEventResponse ev)
    {
        string action = ev.Action?.Trim().ToLowerInvariant() ?? string.Empty;
        return action switch
        {
            "inform_overload_watch" or "inform_overload_rising" => "Overload watch",
            "inform_pressure_rising" or "inform_pressure_watch" => "Pressure rising",
            "inform_break_due"                                  => "Break reminder",
            "inform_focus_lock_in"                              => "Focus coaching",
            "inform_recovery_needed"                            => "Recovery needed",
            _ => string.IsNullOrWhiteSpace(ev.Reason)
                    ? HumanizeActionName(ev.Action)
                    : TrimSentence(ev.Reason, 60)
        };
    }

    private static string BuildSuggestionTitle(string? action)
    {
        // Handles canonical (short_break / long_break) and legacy action IDs.
        string? breakKind = PortaBreakSemantics.ResolveBreakKind(action);
        if (breakKind == PortaBreakSemantics.LongBreakKind) return "Take a long break";
        if (breakKind == PortaBreakSemantics.ShortBreakKind) return "Take a short break";
        string normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "focus_lock_in_suggestion" => "Lock in focus",
            _ => HumanizeActionName(action)
        };
    }

    private static string BuildSuggestionShortWhy(LocalApiEventResponse ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.Reason))
        {
            return TrimSentence(ev.Reason, 120);
        }

        if (TryExtractSuggestionReason(ev.WhyJson, out string? reasonFromWhyJson) && !string.IsNullOrWhiteSpace(reasonFromWhyJson))
        {
            return reasonFromWhyJson;
        }

        return "Ari noticed a focus or load pattern that may need attention.";
    }

    private static string BuildChangeShortWhy(LocalApiEventResponse ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.Reason))
        {
            return TrimSentence(ev.Reason, 120);
        }

        if (TryExtractSuggestionReason(ev.WhyJson, out string? reasonFromWhyJson) && !string.IsNullOrWhiteSpace(reasonFromWhyJson))
        {
            return reasonFromWhyJson;
        }

        if (IsPolicyIntentAction(ev.Action))
        {
            return "Ari recorded this as a possible change, but nothing changed on your desktop yet.";
        }

        if (IsUnsupportedAction(ev.Action))
        {
            return "Ari recognized this action, but this device cannot apply it automatically.";
        }

        string executionState = NormalizeExecutionState(ev.ExecutionState);
        if (!IsRealDesktopAction(ev))
        {
            return "Ari logged this action, but it did not change your desktop directly.";
        }

        if (executionState == "failed")
        {
            return "Ari tried to make this change, but it did not go through.";
        }

        if (executionState == "decided")
        {
            return "Ari queued this change and is still waiting to apply it.";
        }

        return "Ari changed this to support your current focus pattern.";
    }

    private static string BuildDecisionExecutionSummary(LocalApiEventResponse ev) =>
        $"{BuildActionClassificationText(ev)} | {BuildDecisionStateBadge(ev)} | {BuildExecutionStateBadge(ev)}";

    private static string BuildActionClassificationText(LocalApiEventResponse ev) =>
        $"Type: {ClassifyAction(ev)}";

    private static string BuildDecisionStateText(LocalApiEventResponse ev)
    {
        DateTimeOffset decisionUtc = ev.DecidedAtUtc ?? ev.TimestampUtc;
        return $"Decision: Decided at {decisionUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
    }

    private static string BuildExecutionStateText(LocalApiEventResponse ev)
    {
        if (IsPolicyIntentAction(ev.Action))
        {
            return "Execution: Policy intent only (no desktop executor)";
        }

        if (IsUnsupportedAction(ev.Action))
        {
            return "Execution: Unsupported on this device/build";
        }

        string executionState = NormalizeExecutionState(ev.ExecutionState);
        return executionState switch
        {
            "applied" when ev.AppliedAtUtc.HasValue
                => $"Execution: Applied at {ev.AppliedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            "applied" => "Execution: Applied",
            "verified" when ev.AppliedAtUtc.HasValue
                => $"Execution: Verified at {ev.AppliedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            "verified" => "Execution: Verified",
            "failed" when !string.IsNullOrWhiteSpace(ev.ExecutionErrorCode)
                => $"Execution: Failed ({HumanizeExecutionError(ev.ExecutionErrorCode)})",
            "failed" => "Execution: Failed",
            "decided" => "Execution: Pending user-session acknowledgement",
            _ => "Execution: Decision only (no desktop executor)"
        };
    }

    private static string BuildDecisionStateBadge(LocalApiEventResponse ev)
    {
        _ = ev;
        return "Decision: Decided";
    }

    private static string BuildExecutionStateBadge(LocalApiEventResponse ev)
    {
        if (IsPolicyIntentAction(ev.Action))
        {
            return "Execution: Policy Intent";
        }

        if (IsUnsupportedAction(ev.Action))
        {
            return "Execution: Unsupported";
        }

        string executionState = NormalizeExecutionState(ev.ExecutionState);
        return executionState switch
        {
            "applied" => "Execution: Applied",
            "verified" => "Execution: Verified",
            "failed" => "Execution: Failed",
            "decided" => "Execution: Pending",
            _ => "Execution: Decision only"
        };
    }

    private static string NormalizeExecutionState(string? executionState)
    {
        if (string.IsNullOrWhiteSpace(executionState))
        {
            return "decision_only";
        }

        return executionState.Trim().ToLowerInvariant();
    }

    private static string HumanizeExecutionError(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return "unknown error";
        }

        return errorCode.Trim().Replace('_', ' ').ToLowerInvariant();
    }

    private static string ClassifyAction(LocalApiEventResponse ev)
        => PortaActionSemantics.ClassifyAction(ev.Action, ev.CommandType);

    private static bool IsRealDesktopAction(LocalApiEventResponse ev)
        => PortaActionSemantics.IsRealDesktopAction(ev.Action, ev.CommandType);

    private static bool IsPolicyIntentAction(string? action)
        => PortaActionSemantics.IsPolicyIntentAction(action);

    private static bool IsUnsupportedAction(string? action)
        => PortaActionSemantics.IsUnsupportedAction(action);

    private static string BuildUndoDetails(LocalApiEventResponse ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.HowToUndo))
        {
            return ev.HowToUndo.Trim();
        }

        if (TryInferManualUndo(ev.Action, out string? manualUndo))
        {
            return manualUndo ?? "No direct undo for this event type.";
        }

        return "No direct undo for this event type.";
    }

    private static bool HasManualUndoHint(string? action)
    {
        return TryInferManualUndo(action, out _);
    }

    private static bool TryInferManualUndo(string? action, out string? instruction)
    {
        instruction = null;
        if (string.IsNullOrWhiteSpace(action))
        {
            return false;
        }

        string normalized = action.Trim().ToLowerInvariant();
        if (normalized.Contains("notification", StringComparison.Ordinal))
        {
            instruction = "Open Settings and disable Notification reduction.";
            return true;
        }

        if (normalized.Contains("focus", StringComparison.Ordinal))
        {
            instruction = "Open Settings and disable Focus mode nudge.";
            return true;
        }

        if (normalized.Contains("animation", StringComparison.Ordinal))
        {
            instruction = "Open Settings and disable Animation reduction.";
            return true;
        }

        if (normalized.Contains("app", StringComparison.Ordinal) || normalized.Contains("restrict", StringComparison.Ordinal))
        {
            instruction = "Open Settings and disable Temporary app restriction.";
            return true;
        }

        return false;
    }

    private static bool TryExtractSuggestionReason(string? whyJson, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(whyJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(whyJson);
            if (!doc.RootElement.TryGetProperty("reasons", out JsonElement reasons)
                || reasons.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            double? attentionSec = TryGetDouble(reasons, "sustained_attention_s");
            bool? overloadActive = TryGetBool(reasons, "overload_active");

            if (attentionSec.HasValue && overloadActive.HasValue)
            {
                double focusedMinutes = Math.Max(0d, attentionSec.Value / 60d);
                reason = overloadActive.Value
                    ? $"Focused for {focusedMinutes:F0} min while overload stayed high."
                    : $"Focused for {focusedMinutes:F0} min with elevated load.";
                return true;
            }

            if (attentionSec.HasValue)
            {
                reason = $"Focused for {Math.Max(0d, attentionSec.Value / 60d):F0} min.";
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string FormatCooldownRemaining(DateTimeOffset? cooldownEndUtc)
    {
        if (!cooldownEndUtc.HasValue)
        {
            return NotAvailableText;
        }

        TimeSpan remaining = cooldownEndUtc.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "none";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{Math.Ceiling(remaining.TotalMinutes):F0} min";
        }

        return $"{Math.Ceiling(remaining.TotalSeconds):F0} sec";
    }

    private static string BuildSuggestionRepeatLine(LocalApiEventResponse ev)
    {
        if (ev.CooldownEndTimestampUtc.HasValue)
        {
            TimeSpan remaining = ev.CooldownEndTimestampUtc.Value - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                return $"This won\u2019t repeat for {Math.Ceiling(remaining.TotalMinutes):F0} min.";
            }
        }

        if (TryExtractCooldownMinutes(ev.WhyJson, out int cooldownMinutes) && cooldownMinutes > 0)
        {
            return $"This won\u2019t repeat for {cooldownMinutes} min after a trigger.";
        }

        return "This suggestion can trigger again when policy conditions are met.";
    }

    private static bool TryExtractCooldownMinutes(string? whyJson, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(whyJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(whyJson);
            if (!doc.RootElement.TryGetProperty("thresholds", out JsonElement thresholds)
                || thresholds.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            double? cooldownSeconds = TryGetDouble(thresholds, "cooldown_min_s");
            if (!cooldownSeconds.HasValue || cooldownSeconds.Value <= 0)
            {
                return false;
            }

            minutes = (int)Math.Ceiling(cooldownSeconds.Value / 60d);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildActionsLeadText(IReadOnlyList<LocalApiEventResponse> ordered)
    {
        if (ordered.Count == 0)
        {
            return "Recommendations and recent actions will appear here once Ari has enough tracked activity to act on.";
        }

        int recommendationCount = ordered.Count(static ev => IsSuggestionEvent(ev.Action));
        int changeCount = ordered.Count(static ev => IsSystemChangeEvent(ev));

        if (recommendationCount > 0 && changeCount > 0)
        {
            return $"Ari logged {recommendationCount} recommendation{(recommendationCount == 1 ? string.Empty : "s")} and {changeCount} recent change{(changeCount == 1 ? string.Empty : "s")} in this view.";
        }

        if (recommendationCount > 0)
        {
            return $"Ari logged {recommendationCount} recommendation{(recommendationCount == 1 ? string.Empty : "s")} in this view.";
        }

        return $"Ari logged {changeCount} recent change{(changeCount == 1 ? string.Empty : "s")} in this view.";
    }

    private static string FormatWhyJsonForDisplay(string? whyJson)
    {
        if (string.IsNullOrWhiteSpace(whyJson))
        {
            return NotAvailableText;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(whyJson);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return whyJson.Trim();
        }
    }

    private static string HumanizeActionName(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return "Action recorded";
        }

        return action.Replace('_', ' ').Trim();
    }

    private static string TrimSentence(string value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NotAvailableText;
        }

        string trimmed = value.Trim();
        if (trimmed.Length <= maxLen)
        {
            return trimmed;
        }

        return $"{trimmed[..maxLen].TrimEnd()}...";
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            T? nested = FindDescendant<T>(child);
            if (nested != null)
            {
                return nested;
            }
        }

        return null;
    }

    private static double? TryGetDouble(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out double value))
        {
            return value;
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out JsonElement element))
        {
            if (element.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out int intValue))
            {
                return intValue != 0;
            }
        }

        return null;
    }

    private void PopulateSettings(LocalApiSettingsResponse? settings)
    {
        if (DateTimeOffset.UtcNow < _settingsDirtyUntilUtc)
        {
            SettingsStatusText.Text = "Settings status: applying changes...";
            return;
        }

        _suppressSettingsEvents = true;
        if (settings == null)
        {
            IntensitySlider.Value = 1;
            SelectPrivacyLevel("Standard");
            SettingsSuggestionsOnlyToggle.IsChecked = false;
            SettingsNotificationToggle.IsChecked = false;
            SettingsFocusModeToggle.IsChecked = false;
            SettingsAnimationToggle.IsChecked = false;
            SettingsAppRestrictionToggle.IsChecked = false;
            SettingsStatusText.Text = "Settings status: unavailable";
            _suppressSettingsEvents = false;
            return;
        }

        IntensitySlider.Value = settings.RegulationIntensity.ToLowerInvariant() switch
        {
            "gentle" => 0,
            "strong" => 2,
            _ => 1
        };
        SelectPrivacyLevel(settings.PrivacyLevel);
        SettingsNotificationToggle.IsChecked = settings.NotificationReductionEnabled;
        SettingsFocusModeToggle.IsChecked = settings.FocusModeEnabled;
        SettingsAnimationToggle.IsChecked = settings.AnimationReductionEnabled;
        SettingsAppRestrictionToggle.IsChecked = settings.TemporaryAppRestrictionEnabled;
        SettingsSuggestionsOnlyToggle.IsChecked =
            !settings.NotificationReductionEnabled
            && !settings.FocusModeEnabled
            && !settings.AnimationReductionEnabled
            && !settings.TemporaryAppRestrictionEnabled;
        SyncInterventionTogglesFromSettingsUi();
        SettingsStatusText.Text = $"Settings status: updated {settings.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
        _suppressSettingsEvents = false;
        UpdateIntensityLabel();
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateIntensityLabel();
        if (_suppressSettingsEvents)
        {
            return;
        }

        QueueSettingsAutoSave("settings_intensity_auto");
    }

    private void UpdateIntensityLabel()
    {
        IntensityValueText.Text = IntensitySlider.Value switch
        {
            <= 0.5 => "Low",
            >= 1.5 => "Strong",
            _ => "Balanced"
        };
        // Sync intensity card selection visuals
        UpdateIntensityCardSelection((int)Math.Round(IntensitySlider.Value));
    }

    private void UpdateIntensityCardSelection(int selected)
    {
        foreach (var (card, icon, idx) in new[]
        {
            (IntensityLightCard, IntensityLightIcon, 0),
            (IntensityBalancedCard, IntensityBalancedIcon, 1),
            (IntensityDeepCard, IntensityDeepIcon, 2)
        })
        {
            bool active = idx == selected;
            var inactiveBorder = (TryFindResource("PortaCardBorderBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x25, 0x25, 0x35);
            var inactiveBg     = (TryFindResource("PortaCardBrush")       as SolidColorBrush)?.Color ?? Color.FromRgb(0x1C, 0x1C, 0x2E);
            card.BorderBrush = new SolidColorBrush(active
                ? Color.FromRgb(0x63, 0x66, 0xF1)
                : inactiveBorder);
            card.Background = new SolidColorBrush(active
                ? Color.FromArgb(0x1A, 0x63, 0x66, 0xF1)
                : inactiveBg);
            icon.Stroke = new SolidColorBrush(active
                ? Color.FromRgb(0x63, 0x66, 0xF1)
                : Color.FromRgb(0x64, 0x74, 0x8B));
        }
    }

    private void IntensityCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border card && card.Tag is string tag && int.TryParse(tag, out int value))
        {
            IntensitySlider.Value = value;
        }
    }

    private void SchedulePauseToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SchedulePausePanel.Visibility = SchedulePausePanel.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateTodayStateChip(PortaVisualState visualState, LocalApiCurrentSnapshotResponse? snapshot)
    {
        double pressure = snapshot != null ? ResolvePressureLevel(snapshot) : 0d;
        bool hasSession = visualState.FocusEpisodeSeconds.HasValue && visualState.FocusEpisodeSeconds.Value > 0d;

        if (pressure >= 0.80)
        {
            TodayStateChipText.Text = "● Overloaded";
            TodayStateChipText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TodayStateChipBorder.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xEF, 0x44, 0x44));
            TodayStateChipBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xEF, 0x44, 0x44));
        }
        else if (pressure >= 0.65)
        {
            TodayStateChipText.Text = "● Watch this";
            TodayStateChipText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            TodayStateChipBorder.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xF5, 0x9E, 0x0B));
            TodayStateChipBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B));
        }
        else if (hasSession)
        {
            TodayStateChipText.Text = "● Focused now";
            TodayStateChipText.Foreground = new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84));
            TodayStateChipBorder.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0x16, 0xC7, 0x84));
            TodayStateChipBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x16, 0xC7, 0x84));
        }
        else
        {
            TodayStateChipText.Text = "● Away";
            TodayStateChipText.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            TodayStateChipBorder.Background = new SolidColorBrush(Color.FromArgb(0x1E, 0x3B, 0x82, 0xF6));
            TodayStateChipBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6));
        }
    }

    private void PrivacyLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        QueueSettingsAutoSave("settings_privacy_auto");
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync("settings_save").ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync(string reason)
    {
        if (_settingsSaveInFlight)
        {
            return;
        }

        _settingsSaveInFlight = true;
        if (reason.StartsWith("settings", StringComparison.OrdinalIgnoreCase))
        {
            SyncInterventionTogglesFromSettingsUi();
        }

        try
        {
            var request = new LocalApiUpdateSettingsRequest(
                RegulationIntensity: IntensitySlider.Value switch
                {
                    <= 0.5 => "Gentle",
                    >= 1.5 => "Strong",
                    _ => "Balanced"
                },
                PrivacyLevel: GetSelectedPrivacyLevel(),
                NotificationReductionEnabled: InterventionNotificationToggle.IsChecked ?? false,
                FocusModeEnabled: InterventionFocusModeToggle.IsChecked ?? false,
                AnimationReductionEnabled: InterventionAnimationToggle.IsChecked ?? false,
                TemporaryAppRestrictionEnabled: InterventionAppRestrictionToggle.IsChecked ?? false);

            LocalApiSettingsResponse? response = await _apiClient.UpdateSettingsAsync(request).ConfigureAwait(true);
            if (response == null)
            {
                SettingsStatusText.Text = $"Settings status: save failed ({reason})";
                return;
            }

            _settingsDirtyUntilUtc = DateTimeOffset.MinValue;
            _effectiveSettings = response;
            PopulateSettings(response);
            SettingsStatusText.Text = $"Settings status: saved ({reason})";
        }
        finally
        {
            _settingsSaveInFlight = false;
        }
    }

    private async void PauseUntilTomorrowButton_Click(object sender, RoutedEventArgs e)
    {
        DateTime tomorrow = DateTime.Today.AddDays(1).AddHours(8);
        await _apiClient.PauseAsync("settings_pause_until_tomorrow", new DateTimeOffset(tomorrow)).ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void PauseUntilSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PauseUntilDatePicker.SelectedDate.HasValue)
        {
            MessageBox.Show("Select a date first.", "PORTA Control Center", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PauseUntilTimeComboBox.SelectedItem is not string selectedTime
            || !TimeSpan.TryParseExact(selectedTime, "hh\\:mm", CultureInfo.InvariantCulture, out TimeSpan timeOfDay))
        {
            MessageBox.Show("Select a valid time.", "PORTA Control Center", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DateTime until = PauseUntilDatePicker.SelectedDate.Value.Date.Add(timeOfDay);
        await _apiClient.PauseAsync("settings_pause_scheduled", new DateTimeOffset(until)).ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void ResumeFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.ResumeAsync().ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void RevokeConsentFromSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.RevokeConsentAsync().ConfigureAwait(true);
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void RecalibrateBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        LocalApiRecalibrationResponse? response = await _apiClient.RecalibrateBaselineAsync().ConfigureAwait(true);
        SettingsStatusText.Text = response == null
            ? "Settings status: recalibration failed"
            : $"Settings status: recalibration active until {response.UntilUtc.ToLocalTime():HH:mm:ss}";
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private void SettingsSuggestionsOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        bool suggestionsOnly = SettingsSuggestionsOnlyToggle.IsChecked == true;
        if (suggestionsOnly)
        {
            SettingsNotificationToggle.IsChecked = false;
            SettingsFocusModeToggle.IsChecked = false;
            SettingsAnimationToggle.IsChecked = false;
            SettingsAppRestrictionToggle.IsChecked = false;
        }

        SyncInterventionTogglesFromSettingsUi();
        QueueSettingsAutoSave("settings_suggestions_only_auto");
    }

    private void SettingsCategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        bool allDisabled =
            SettingsNotificationToggle.IsChecked != true
            && SettingsFocusModeToggle.IsChecked != true
            && SettingsAnimationToggle.IsChecked != true
            && SettingsAppRestrictionToggle.IsChecked != true;

        bool anySelected = !allDisabled;
        if (anySelected)
        {
            SettingsSuggestionsOnlyToggle.IsChecked = false;
        }

        SyncInterventionTogglesFromSettingsUi();
        QueueSettingsAutoSave("settings_category_auto");
    }

    private void SyncInterventionTogglesFromSettingsUi()
    {
        InterventionNotificationToggle.IsChecked = SettingsNotificationToggle.IsChecked == true;
        InterventionFocusModeToggle.IsChecked = SettingsFocusModeToggle.IsChecked == true;
        InterventionAnimationToggle.IsChecked = SettingsAnimationToggle.IsChecked == true;
        InterventionAppRestrictionToggle.IsChecked = SettingsAppRestrictionToggle.IsChecked == true;
    }

    private void InitializePauseUntilControls()
    {
        if (PauseUntilTimeComboBox == null)
        {
            return;
        }

        PauseUntilTimeComboBox.Items.Clear();
        for (int hour = 0; hour < 24; hour++)
        {
            PauseUntilTimeComboBox.Items.Add($"{hour:00}:00");
            PauseUntilTimeComboBox.Items.Add($"{hour:00}:30");
        }

        DateTime now = DateTime.Now;
        int roundedMinutes = now.Minute < 30 ? 30 : 0;
        int roundedHour = now.Minute < 30 ? now.Hour : (now.Hour + 1) % 24;
        string defaultTime = $"{roundedHour:00}:{roundedMinutes:00}";
        PauseUntilTimeComboBox.SelectedItem = defaultTime;
        PauseUntilDatePicker.SelectedDate = now.Date;
    }

    private void QueueSettingsAutoSave(string reason)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        _settingsAutoSaveReason = reason;
        _settingsDirtyUntilUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        _settingsAutoSaveTimer.Stop();
        _settingsAutoSaveTimer.Start();
    }

    private void PopulateStates(LocalApiCurrentSnapshotResponse? snapshot, LocalApiLiveSessionStateResponse? liveSession)
    {
        if (snapshot == null)
        {
            bool liveFresh = IsLiveSessionFresh(liveSession);
            SetFlowStepState(SignalsStatusPill, SignalsStatusText, liveFresh ? FlowStepState.Ready : FlowStepState.Blocked, liveFresh ? "Ready" : "Blocked");
            SignalsBlockersText.Text = liveFresh
                ? "What can block it: no durable bucket yet; live session is still flowing."
                : "What can block it: service offline / consent needed / auth not ready.";
            SignalsCurrentText.Text = liveFresh
                ? BuildLiveSignalsCurrentText(liveSession!)
                : "Current status: no live signal snapshot.";

            SetFlowStepState(CalibrationStatusPill, CalibrationStatusText, FlowStepState.Blocked, "Blocked");
            CalibrationBlockersText.Text = "What can block it: no live signal snapshot.";
            CalibrationCurrentText.Text = "Current status: baseline readiness unknown.";

            SetFlowStepState(SmoothingStatusPill, SmoothingStatusText, FlowStepState.Blocked, "Blocked");
            SmoothingBlockersText.Text = "What can block it: no live signal snapshot.";
            SmoothingCurrentText.Text = "Current status: smoothing values unavailable.";

            SetFlowStepState(DecisionsStatusPill, DecisionsStatusText, FlowStepState.Blocked, "Blocked");
            DecisionsBlockersText.Text = "What can block it: no live signal snapshot.";
            DecisionsCurrentText.Text = "Current status: decision layer unavailable.";
            return;
        }

        bool consentActive = _latestStatus?.ConsentActive == true;
        bool apiAuthReady = _latestStatus?.LocalApiAuthReady == true;
        bool partialData = snapshot.PartialData == true;
        IReadOnlyList<string> missingChannels = snapshot.MissingMajorChannels ?? [];

        var signalBlockers = new List<string>();
        if (!consentActive)
        {
            signalBlockers.Add("consent needed");
        }

        if (!apiAuthReady)
        {
            signalBlockers.Add("local API auth not ready");
        }

        string? captureFailureDetail = DescribeCaptureFailureReason(_latestStatus?.CaptureFailureReason);
        if (!string.IsNullOrWhiteSpace(captureFailureDetail))
        {
            signalBlockers.Add(captureFailureDetail);
        }

        if (missingChannels.Count > 0)
        {
            signalBlockers.Add($"missing channels ({string.Join("/", missingChannels)})");
        }

        FlowStepState signalsState = signalBlockers.Count == 0
            ? FlowStepState.Ready
            : FlowStepState.Warning;
        SetFlowStepState(
            SignalsStatusPill,
            SignalsStatusText,
            signalsState,
            signalsState == FlowStepState.Ready ? "Ready" : "Partial");
        SignalsBlockersText.Text = signalBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", signalBlockers)}.";
        SignalsCurrentText.Text = IsLiveSessionFresh(liveSession)
            ? BuildLiveSignalsCurrentText(liveSession!)
            : $"Current status: keyboard {ChannelAvailabilityLabel(missingChannels, "keyboard")}, mouse {ChannelAvailabilityLabel(missingChannels, "mouse")}, focus {ChannelAvailabilityLabel(missingChannels, "focus")}, idle {ChannelAvailabilityLabel(missingChannels, "idle")}.";

        string calibrationStage = NormalizeCalibrationStage(snapshot.CalibrationStage, snapshot.BaselineReady, snapshot.ColdStart);
        bool baselineReady = string.Equals(calibrationStage, CalibrationStageCalibrated, StringComparison.Ordinal);
        bool provisional = string.Equals(calibrationStage, CalibrationStageProvisional, StringComparison.Ordinal);
        bool coldStart = snapshot.ColdStart == true;
        bool overloadActive = snapshot.OverloadActive;
        double quality = snapshot.CurrentQualityScore ?? 0d;
        var calibrationBlockers = new List<string>();
        if (!baselineReady)
        {
            if (quality > 0d && quality < UiCalibrationQualityFloor)
            {
                calibrationBlockers.Add("low quality");
            }

            if (overloadActive)
            {
                calibrationBlockers.Add("overload active");
            }

            if (partialData)
            {
                calibrationBlockers.Add("partial buckets");
            }

            if (coldStart && !provisional)
            {
                calibrationBlockers.Add("cold start");
            }

            if (calibrationBlockers.Count == 0)
            {
                calibrationBlockers.Add(provisional ? "finishing full calibration" : "collecting enough baseline data");
            }
        }

        FlowStepState calibrationState = baselineReady
            ? FlowStepState.Ready
            : provisional
                ? FlowStepState.Warning
                : FlowStepState.Blocked;
        SetFlowStepState(
            CalibrationStatusPill,
            CalibrationStatusText,
            calibrationState,
            ToCalibrationStageLabel(calibrationStage));
        CalibrationBlockersText.Text = calibrationBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", calibrationBlockers)}.";
        string baselinePctText = snapshot.BaselineReadyPct.HasValue
            ? $"{Math.Clamp(snapshot.BaselineReadyPct.Value * 100d, 0d, 100d):F0}% metrics ready"
            : "progress unavailable";
        CalibrationCurrentText.Text = $"Current status: {ToCalibrationStageSentence(calibrationStage)} ({baselinePctText}), quality {(snapshot.CurrentQualityScore?.ToString("F2", CultureInfo.InvariantCulture) ?? NotAvailableText)}, cold start {(coldStart ? "on" : "off")}.";

        bool smoothingHasValues =
            snapshot.SmoothedTypingBurstIntensity > 0d
            || snapshot.SmoothedScrollVelocityIndex > 0d
            || snapshot.SmoothedFocusSwitchRate > 0d;
        var smoothingBlockers = new List<string>();
        if (partialData)
        {
            smoothingBlockers.Add("partial buckets");
        }

        if (!smoothingHasValues)
        {
            smoothingBlockers.Add("very low recent activity");
        }

        FlowStepState smoothingState = smoothingBlockers.Count == 0
            ? FlowStepState.Ready
            : FlowStepState.Warning;
        SetFlowStepState(
            SmoothingStatusPill,
            SmoothingStatusText,
            smoothingState,
            smoothingState == FlowStepState.Ready ? "Ready" : "Watching");
        SmoothingBlockersText.Text = smoothingBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", smoothingBlockers)}.";
        SmoothingCurrentText.Text = $"Current status: smoothed keyboard {snapshot.SmoothedTypingBurstIntensity:F2}, smoothed mouse {snapshot.SmoothedScrollVelocityIndex:F2}, smoothed switches {snapshot.SmoothedFocusSwitchRate:F2}.";

        double trust = Math.Clamp(snapshot.Confidence, 0d, 1d);
        string trustTier = NormalizeTrustTier(snapshot.TrustTier, snapshot.Confidence);
        double overload = Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d);
        double focusedMinutes = Math.Max(0d, ResolveFocusEpisodeSeconds(snapshot) / 60d);
        bool suggestionsEnabled = !string.Equals(calibrationStage, CalibrationStageCold, StringComparison.Ordinal);
        bool systemActionsEnabled = snapshot.SystemActionsEnabled == true || string.Equals(calibrationStage, CalibrationStageCalibrated, StringComparison.Ordinal);
        var decisionBlockers = new List<string>();
        if (!suggestionsEnabled)
        {
            decisionBlockers.Add("still calibrating");
        }

        if (string.Equals(trustTier, TrustTierLow, StringComparison.Ordinal))
        {
            decisionBlockers.Add("low trust");
        }

        if (snapshot.CooldownTriggered)
        {
            decisionBlockers.Add("cooldown active");
        }

        if (ResolveFocusEpisodeSeconds(snapshot) < UiBreakFocusThresholdSeconds)
        {
            decisionBlockers.Add("building focus episode");
        }

        if (!snapshot.OverloadActive && overload < UiDecisionOverloadThreshold)
        {
            decisionBlockers.Add("overload not sustained");
        }

        FlowStepState decisionsState = decisionBlockers.Count == 0
            ? FlowStepState.Ready
            : FlowStepState.Warning;
        string decisionLabel = decisionsState == FlowStepState.Ready
            ? "Ready"
            : systemActionsEnabled
                ? "Monitoring"
                : suggestionsEnabled
                    ? "Suggestions only"
                    : "Calibrating";
        SetFlowStepState(DecisionsStatusPill, DecisionsStatusText, decisionsState, decisionLabel);
        DecisionsBlockersText.Text = decisionBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", decisionBlockers)}.";
        DecisionsCurrentText.Text = $"Current status: overload {(overload * 100d):F0}%, confidence {ToConfidenceLabel(trustTier)} ({(trust * 100d):F0}%), focus episode {focusedMinutes:F0} min, suggestions {(suggestionsEnabled ? "enabled" : "gated")}, system actions {(systemActionsEnabled ? "enabled" : "gated")}.";
    }

    private static double ResolveFocusEpisodeSeconds(LocalApiCurrentSnapshotResponse snapshot)
    {
        return Math.Max(0d, snapshot.FocusEpisodeSeconds);
    }

    private static double ResolvePressureLevel(LocalApiCurrentSnapshotResponse snapshot)
    {
        return Math.Clamp(snapshot.PressureLevel ?? snapshot.OverloadLikelihood, 0d, 1d);
    }

    private static string ChannelAvailabilityLabel(IReadOnlyList<string> missingChannels, string channel)
    {
        return missingChannels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase))
            ? "missing"
            : "ok";
    }

    private void SetFlowStepState(Border pill, TextBlock label, FlowStepState state, string text)
    {
        label.Text = text;
        switch (state)
        {
            case FlowStepState.Ready:
                pill.Background = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                label.Foreground = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                break;
            case FlowStepState.Warning:
                pill.Background = new SolidColorBrush(Color.FromRgb(133, 77, 14));
                label.Foreground = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                break;
            default:
                pill.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                label.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                break;
        }
    }

    private void PopulateTimeline(LocalApiTimelineResponse? timeline)
    {
        UpdateHistoryControls();
        UpdateHistoryPeriodTabs();
        HistoryCoordinateCanvas.Children.Clear();
        HistoryHourLabelsPanel.Children.Clear();
        TimelineOverloadTrackPanel.Children.Clear();
        TimelineTrustTrackPanel.Children.Clear();
        TimelineFocusTrackPanel.Children.Clear();
        TimelineInterventionTrackPanel.Children.Clear();
        TimelineOverloadActiveOverlayPanel.Children.Clear();

        (DateTimeOffset displayFromUtc, DateTimeOffset displayToUtc) = ResolveHistoryDisplayRangeUtc(DateTimeOffset.UtcNow);
        HistoryFocusHeatmap heatmap = HistoryFocusHeatmapBuilder.Build(
            timeline,
            fallbackFromUtc: displayFromUtc,
            fallbackToUtc: displayToUtc);
        _latestHistoryHeatmap = heatmap;

        // For 2-Week view: replace overload episodes for the last 7 days with more-accurate
        // data from the 300s _weekTimeline.  The 600s _twoWeekTimeline aggregates away
        // OverloadActive flags that the 300s timeline preserves, causing 2-Week to show
        // less overload than Week for the same dates.
        if (_historyPeriod == HistoryPeriod.TwoWeeks && _weekTimeline != null)
        {
            heatmap = OverlayWeekOverloadOnTwoWeekHeatmap(heatmap, _weekTimeline);
            _latestHistoryHeatmap = heatmap;
        }

        // For Week / 2-Week views, replace today's episodes with the accurate ones
        // from the 60s _todayTimeline (which has real OverloadActive flags).
        // This makes today's column in Week/2-Week match Day view exactly.
        if ((_historyPeriod == HistoryPeriod.Week || _historyPeriod == HistoryPeriod.TwoWeeks)
            && _todayTimeline != null)
        {
            heatmap = OverlayTodayTimeline(heatmap, _todayTimeline);
            _latestHistoryHeatmap = heatmap;
        }

        PopulateHistorySummary(heatmap);
        PopulateHistorySummaryNew(heatmap);
        RenderHistoryCoordinateHeatmap(heatmap);
        // Don't wipe the calendar blank while the week/2-week timeline is still loading.
        // The 2s timer calls PopulateTimeline on every tick; if the fetch hasn't completed
        // yet, timeline == null and we'd flash blank. Preserve the last render instead.
        bool preserveCalendar = timeline == null
            && (_historyPeriod == HistoryPeriod.Week || _historyPeriod == HistoryPeriod.TwoWeeks)
            && _calendarHeatmap != null;
        if (!preserveCalendar)
            RenderHistoryCalendarGrid(heatmap);

        int maxPoints = _historyRange switch
        {
            HistoryRange.Last1Hour => 360,
            HistoryRange.Last4Hours => 480,
            HistoryRange.AllTime => 720,
            _ => 600
        };
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline == null
            ? []
            : DownsampleSnapshots(timeline.Snapshots, maxPoints);

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            TimelineOverloadTrackPanel.Children.Add(BuildTrackPoint(snapshot, ResolvePressureLevel(snapshot), Color.FromRgb(239, 68, 68), "Pressure"));
            TimelineTrustTrackPanel.Children.Add(BuildTrackPoint(snapshot, snapshot.Confidence, Color.FromRgb(59, 130, 246), "Confidence"));
            double focusNormalized = Math.Clamp(ResolveFocusEpisodeSeconds(snapshot) / 3600d, 0d, 1d);
            TimelineFocusTrackPanel.Children.Add(BuildTrackPoint(snapshot, focusNormalized, Color.FromRgb(34, 197, 94), "Focus episode"));
            TimelineOverloadActiveOverlayPanel.Children.Add(BuildOverloadActiveMarker(snapshot));
        }

        if (snapshots.Count == 0)
        {
            TimelineOverloadTrackPanel.Children.Add(new TextBlock { Text = "Pressure snapshots will appear here when processed blocks are available.", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap });
            TimelineTrustTrackPanel.Children.Add(new TextBlock { Text = "Confidence snapshots will appear here when processed blocks are available.", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap });
            TimelineFocusTrackPanel.Children.Add(new TextBlock { Text = "Focus episodes will appear here when processed blocks are available.", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap });
            TimelineOverloadActiveOverlayPanel.Children.Add(new TextBlock { Text = "Overload-active blocks will appear here when processed history is available.", Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap });
        }

        IReadOnlyList<LocalApiEventResponse> interventions = timeline?.Interventions ?? [];
        if (interventions.Count == 0)
        {
            TimelineInterventionTrackPanel.Children.Add(new TextBlock
            {
                Text = "No interventions in this range.",
                Foreground = Brushes.Gainsboro
            });
        }
        else
        {
            foreach (LocalApiEventResponse intervention in interventions.OrderBy(static ev => ev.TimestampUtc))
            {
                TimelineInterventionTrackPanel.Children.Add(BuildInterventionMarker(intervention));
            }
        }

        LocalApiEventResponse? selectedIntervention = _selectedHistoryInterventionId.HasValue
            ? interventions.FirstOrDefault(evt => evt.Id == _selectedHistoryInterventionId.Value)
            : null;
        if (selectedIntervention != null)
        {
            SetTimelineDetail(null, selectedIntervention);
            return;
        }

        LocalApiCurrentSnapshotResponse? selectedSnapshot = _selectedHistorySnapshotTimestampUtc.HasValue
            ? snapshots.FirstOrDefault(s => s.TimestampUtc == _selectedHistorySnapshotTimestampUtc.Value)
            : null;
        if (selectedSnapshot != null)
        {
            SetTimelineDetail(selectedSnapshot, null);
            return;
        }

        // Do NOT call SetTimelineCellDetail here — PopulateHistoryEpisodeListNew owns the episode list.
        // Calling it would overwrite the full episode list with only the latest cell's episodes (count mismatch bug).
    }

    private void PopulateHistorySummary(HistoryFocusHeatmap heatmap)
    {
        HistoryGuideText.Text = "Each column is a day, each row is an hour, and 00:00 sits at the bottom.";

        if (!heatmap.HasAnyTracking)
        {
            HistorySummaryLeadText.Text = BuildHistoryEmptyLeadSentence();
            HistoryRangeSummaryText.Text = "0 episodes | 0m";
            HistoryFocusSummaryText.Text = "0 episodes | 0m";
            HistoryOverloadSummaryText.Text = "0 episodes | 0m";
            HistoryTrustSummaryText.Text = "0 segments | 0m";
            HistoryActionsSummaryText.Text = BuildHistoryRangeFootnote(heatmap);
            return;
        }

        HistorySummaryLeadText.Text = BuildHistoryLeadSentence(heatmap);
        HistoryRangeSummaryText.Text = BuildHistoryEpisodeMetricSummary(
            heatmap.FocusEpisodeCount,
            heatmap.Episodes
                .Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.FocusEpisode)
                .Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");
        HistoryFocusSummaryText.Text = BuildHistoryEpisodeMetricSummary(
            heatmap.DistractionEpisodeCount,
            heatmap.Episodes
                .Where(static episode => episode.Kind is
                    HistoryTimelineEpisodeKind.OffTaskDistraction or
                    HistoryTimelineEpisodeKind.FragmentationDistraction or
                    HistoryTimelineEpisodeKind.RollingMicroBurst or
                    HistoryTimelineEpisodeKind.ReturnLoopDistraction or
                    HistoryTimelineEpisodeKind.UnfocusedSession or
                    HistoryTimelineEpisodeKind.ShallowDwellDistraction or
                    HistoryTimelineEpisodeKind.FocusDepartureDistraction)
                .Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");
        HistoryOverloadSummaryText.Text = BuildHistoryEpisodeMetricSummary(
            heatmap.OverloadEpisodeCount,
            heatmap.Episodes
                .Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.OverloadEpisode)
                .Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");
        HistoryTrustSummaryText.Text = BuildHistoryEpisodeMetricSummary(
            heatmap.AwayEpisodeCount,
            heatmap.Episodes
                .Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.Away)
                .Sum(static episode => episode.DurationSeconds),
            singularLabel: "segment",
            pluralLabel: "segments");
        HistoryActionsSummaryText.Text = BuildHistoryRangeFootnote(heatmap);
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW HISTORY REDESIGN — summary cards, timeline bar, episode list
    // ═══════════════════════════════════════════════════════════════

    private void PopulateHistorySummaryNew(HistoryFocusHeatmap heatmap)
    {
        // Show which period the cards represent
        string periodLabel = _historyPeriod switch
        {
            HistoryPeriod.Day      => "Today",
            HistoryPeriod.Week     => "This week",
            HistoryPeriod.TwoWeeks => "Last 2 weeks",
            _                      => string.Empty
        };
        HistoryCardPeriodLabel.Text = periodLabel;

        double totalWork = heatmap.TotalTrackedSeconds > 0d ? heatmap.TotalTrackedSeconds : 1d;

        double focusSec = heatmap.Episodes
            .Where(static e => e.Kind == HistoryTimelineEpisodeKind.FocusEpisode)
            .Sum(static e => e.DurationSeconds);
        double distrSec = heatmap.Episodes
            .Where(static e => e.Kind is
                HistoryTimelineEpisodeKind.OffTaskDistraction or
                HistoryTimelineEpisodeKind.FragmentationDistraction or
                HistoryTimelineEpisodeKind.RollingMicroBurst or
                HistoryTimelineEpisodeKind.ReturnLoopDistraction or
                HistoryTimelineEpisodeKind.UnfocusedSession or
                HistoryTimelineEpisodeKind.ShallowDwellDistraction or
                HistoryTimelineEpisodeKind.FocusDepartureDistraction)
            .Sum(static e => e.DurationSeconds);
        double overSec = heatmap.Episodes
            .Where(static e => e.Kind == HistoryTimelineEpisodeKind.OverloadEpisode)
            .Sum(static e => e.DurationSeconds);
        double awaySec = heatmap.Episodes
            .Where(static e => e.Kind == HistoryTimelineEpisodeKind.Away)
            .Sum(static e => e.DurationSeconds);

        SetHistoryCard(HistoryFocusDuration, HistoryFocusSubLabel, HistoryFocusTrack, HistoryFocusBarFill,
            focusSec, heatmap.FocusEpisodeCount, "episode", "episodes", totalWork);
        SetHistoryCard(HistoryDistractionDuration, HistoryDistractionSubLabel, HistoryDistractionTrack, HistoryDistractionBarFill,
            distrSec, heatmap.DistractionEpisodeCount, "episode", "episodes", totalWork);
        SetHistoryCard(HistoryOverloadDuration, HistoryOverloadSubLabel, HistoryOverloadTrack, HistoryOverloadBarFill,
            overSec, heatmap.OverloadEpisodeCount, "episode", "episodes", totalWork);
        SetHistoryCard(HistoryAwayDuration, HistoryAwaySubLabel, HistoryAwayTrack, HistoryAwayBarFill,
            awaySec, heatmap.AwayEpisodeCount, "break", "breaks", totalWork);
    }

    private static void SetHistoryCard(
        TextBlock valueTb, TextBlock subLabelTb, Border track, Border fill,
        double seconds, int count, string singular, string plural, double totalWork)
    {
        if (seconds <= 0d || count == 0)
        {
            valueTb.Text = "—";
            valueTb.Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)); // muted grey when no data
            subLabelTb.Text = string.Empty;
            fill.Width = 0;
            return;
        }
        // Restore state colour from the fill bar background (in case card was previously in empty state)
        if (fill.Background is Brush stateBrush) valueTb.Foreground = stateBrush;

        valueTb.Text = FormatReadableDuration(seconds);
        subLabelTb.Text = count == 1 ? $"1 {singular}" : $"{count} {plural}";

        double fraction = Math.Clamp(seconds / totalWork, 0d, 1d);
        void ApplyFill(object? s, SizeChangedEventArgs e)
        {
            track.SizeChanged -= ApplyFill;
            fill.Width = fraction > 0d ? Math.Max(2d, track.ActualWidth * fraction) : 0d;
        }

        if (track.ActualWidth > 0d)
            fill.Width = Math.Max(2d, track.ActualWidth * fraction);
        else
            track.SizeChanged += ApplyFill;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CALENDAR GRID — Steps 2–11 of porta_history_calendar.md spec
    // ═══════════════════════════════════════════════════════════════════

    private HistoryFocusHeatmap? _calendarHeatmap;

    private sealed record CalendarColumn(DateTime Date, string Label, bool IsToday);

    private void RenderHistoryCalendarGrid(HistoryFocusHeatmap heatmap)
    {
        _calendarHeatmap = heatmap;
        // Defer to after layout so ActualWidth is accurate (avoids left-shift bug)
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            double w = HistoryCalendarContent.ActualWidth > 48
                ? HistoryCalendarContent.ActualWidth - 48d
                : 900d;
            ApplyCalendarGrid(heatmap, w);
        });
        // No auto-scroll — user scrolls freely.
    }

    private void HistoryCalendarCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // This fires when we call HistoryCalendarCanvas.Width = totalCanvasWidth (line 7091).
        // e.NewSize.Width is the CANVAS's own width we just set — NOT the container width.
        // Do NOT pass it to ApplyCalendarGrid; HistoryCalendarContent_SizeChanged owns that.
    }

    private void HistoryCalendarContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Fires when the container Grid gets (or changes) its actual layout width —
        // including the very first visible layout pass and window resizes.
        // e.NewSize.Width is the full container width; subtract 48px for the hour-label column.
        if (_calendarHeatmap == null || !e.WidthChanged) return;
        double w = e.NewSize.Width > 48 ? e.NewSize.Width - 48d : 900d;
        ApplyCalendarGrid(_calendarHeatmap, w);
    }

    private void ApplyCalendarGrid(HistoryFocusHeatmap heatmap, double canvasWidth)
    {
        // ── Clear previous render ──────────────────────────────────────
        HistoryCalendarCanvas.Children.Clear();
        HistoryCalendarHourLabels.Children.Clear();
        HistoryCalendarHeaderRow.Children.Clear();
        HistoryCalendarHeaderRow.ColumnDefinitions.Clear();

        // ── Determine columns (Step 4) ─────────────────────────────────
        var columns = GetCalendarColumns();
        int numCols = columns.Count;
        if (numCols == 0) return;

        double colW;
        double colOffsetX = 0d; // how far from the left edge the column starts (Day centering)
        if (numCols == 1)
        {
            // Day view: use 45% of available width, capped between 320px and 560px, then center it
            colW = Math.Clamp(canvasWidth * 0.45, 320d, 560d);
            colOffsetX = (canvasWidth - colW) / 2d; // center within the canvas
        }
        else
        {
            colW = Math.Max(60d, canvasWidth / numCols);
            colOffsetX = 0d;
        }
        double totalCanvasWidth = numCols == 1 ? canvasWidth : colW * numCols;
        HistoryCalendarCanvas.Width = totalCanvasWidth;

        // ── Determine hour range (Step 2 / Step 3) ────────────────────
        var allEps = heatmap.Episodes
            .Where(e => e.DurationSeconds >= 1d)
            .ToList();

        int startHour, endHour;
        if (allEps.Count > 0)
        {
            startHour = Math.Max(0, allEps.Min(e => e.StartUtc.ToLocalTime().Hour));
            // Allow up to midnight (24) so late-evening sessions are not cut off
            int rawEnd = allEps.Max(e =>
            {
                double h = e.EndUtc.ToLocalTime().TimeOfDay.TotalHours;
                // If episode ends at or after midnight (wraps to 0–1h), treat as 24
                return h < 1d ? 24 : (int)Math.Ceiling(h);
            });
            endHour = Math.Clamp(rawEnd, startHour + 1, 24);
        }
        else
        {
            startHour = 8;
            endHour   = Math.Max(startHour + 1, DateTime.Now.Hour + 1);
        }
        endHour = Math.Max(endHour, startHour + 2); // at least 2 hours

        int totalHours = endHour - startHour;
        const double rowH = 240d; // 240px per hour — more vertical space so short episodes don't overlap
        double totalH = totalHours * rowH;
        HistoryCalendarCanvas.Height = totalH;

        // ── Build column headers (Step 4) ────────────────────────────
        for (int ci = 0; ci < numCols; ci++)
        {
            HistoryCalendarHeaderRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(colW) });
            var col = columns[ci];
            var hdr = new TextBlock
            {
                Text = col.Label,
                FontSize = numCols == 1 ? 11d : 10d,
                Foreground = new SolidColorBrush(
                    col.IsToday && numCols > 1
                        ? Color.FromRgb(0xA5, 0xB4, 0xFC)
                        : numCols == 1
                            ? Color.FromRgb(0x94, 0xA3, 0xB8)
                            : Color.FromRgb(0x64, 0x74, 0x8B)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 4, 0, 4)
            };
            Grid.SetColumn(hdr, ci);
            HistoryCalendarHeaderRow.Children.Add(hdr);

        }

        // In Day view, shift the header grid to align with the centered column
        HistoryCalendarHeaderRow.Margin = numCols == 1
            ? new Thickness(48d + colOffsetX, 0, 0, 0)
            : new Thickness(48d, 0, 0, 0);

        // ── Hour labels (Step 3) ──────────────────────────────────────
        for (int h = startHour; h < endHour; h++)
        {
            HistoryCalendarHourLabels.Children.Add(new TextBlock
            {
                Text = $"{h:00}:00",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                TextAlignment = TextAlignment.Right,
                Height = rowH,
                Width = 44,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(0, 2, 4, 0)
            });
        }

        // ── Build each day column on the canvas (Steps 5–11) ─────────
        for (int ci = 0; ci < numCols; ci++)
        {
            var col = columns[ci];
            double colX = ci * colW + colOffsetX;
            int gridStartMin = startHour * 60;

            // Background clickable area (Step 8 – column click to Day view)
            if (numCols > 1)
            {
                var bg = new Border
                {
                    Width = colW - 1d,
                    Height = totalH,
                    Background = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                    ToolTip = $"View {col.Date:ddd d MMM}"
                };
                var capturedDate = col.Date;
                bg.MouseLeftButtonUp += (_, _) =>
                {
                    _ = capturedDate;
                    _historyPeriod = HistoryPeriod.Day;
                    _historyRange = HistoryRange.Today;
                    UpdateHistoryPeriodTabs();
                    PopulateTimeline(_todayTimeline ?? GetSelectedHistoryTimeline());
                };
                Canvas.SetLeft(bg, colX);
                Canvas.SetTop(bg, 0);
                HistoryCalendarCanvas.Children.Add(bg);
            }

            // Vertical column divider (Step 11)
            if (ci > 0)
            {
                var divVert = new Line
                {
                    X1 = 0, X2 = 0, Y1 = 0, Y2 = totalH,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(divVert, colX);
                Canvas.SetTop(divVert, 0);
                HistoryCalendarCanvas.Children.Add(divVert);
            }

            // Half-hour dashed dividers (Step 7)
            for (int h = 0; h < totalHours; h++)
            {
                // Half-hour dashed line
                var dash = new Line
                {
                    X1 = colX, X2 = colX + colW,
                    Y1 = h * rowH + 30d,
                    Y2 = h * rowH + 30d,
                    Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 3 }
                };
                HistoryCalendarCanvas.Children.Add(dash);

                // Hour solid line
                if (h > 0)
                {
                    var hourLine = new Line
                    {
                        X1 = colX, X2 = colX + colW,
                        Y1 = h * rowH, Y2 = h * rowH,
                        Stroke = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x2E)),
                        StrokeThickness = 1
                    };
                    HistoryCalendarCanvas.Children.Add(hourLine);
                }
            }

            // Episodes for this day (Step 5)
            var dayEps = heatmap.Episodes
                .Where(e =>
                {
                    // Include episodes that start on this day OR cross midnight into it
                    var epStartDate = e.StartUtc.ToLocalTime().Date;
                    var epEndDate   = e.EndUtc.ToLocalTime().Date;
                    return epStartDate == col.Date.Date
                        || (epEndDate == col.Date.Date && e.EndUtc.ToLocalTime().TimeOfDay.TotalMinutes > 5d);
                })
                .OrderBy(e => e.StartUtc)
                .ToList();

            if (dayEps.Count == 0)
            {
                // Empty column — "No data" (Step 9)
                var noData = new TextBlock
                {
                    Text = "No data",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Width = colW
                };
                Canvas.SetLeft(noData, colX);
                Canvas.SetTop(noData, totalH / 2d - 8d);
                HistoryCalendarCanvas.Children.Add(noData);
                continue;
            }

            // Track where the last drawn block ended (in minutes) so we can TRIM
            // the start of overlapping blocks — never push them down visually.
            // Trimming keeps every block at its real time position on the axis.
            double prevDrawnEndMin = double.MinValue;

            foreach (var ep in dayEps)
            {
                var localStart = ep.StartUtc.ToLocalTime();
                var localEnd   = ep.EndUtc.ToLocalTime();

                // Clamp to visible hour range (handle cross-midnight episodes)
                double epStartMin = localStart.Hour * 60d + localStart.Minute;
                double epEndMin   = localEnd.Hour * 60d + localEnd.Minute;
                if (epEndMin < epStartMin && ep.DurationSeconds > 60d)
                {
                    // Cross-midnight: clip to midnight in the start-day column, start from 0 in end-day
                    if (localStart.Date == col.Date.Date) epEndMin = 1440d;
                    else epStartMin = 0d;
                }
                double clampedStart = Math.Max(epStartMin, gridStartMin);
                double clampedEnd   = Math.Min(epEndMin, endHour * 60d);
                if (clampedEnd <= clampedStart) continue;

                // Overload episodes happen INSIDE focus — skip overlap trim so they render at real time
                bool isOverloadEp = ep.Kind == HistoryTimelineEpisodeKind.OverloadEpisode;
                if (!isOverloadEp && clampedStart < prevDrawnEndMin)
                    clampedStart = prevDrawnEndMin;

                // Skip if too short — 1 min minimum for all episode types so the calendar
                // matches the episode counts shown in the stat cards.
                double realDurationMin = clampedEnd - clampedStart;
                bool isDistractionEp = ep.Kind is
                    HistoryTimelineEpisodeKind.OffTaskDistraction or
                    HistoryTimelineEpisodeKind.FragmentationDistraction or
                    HistoryTimelineEpisodeKind.RollingMicroBurst or
                    HistoryTimelineEpisodeKind.ReturnLoopDistraction or
                    HistoryTimelineEpisodeKind.UnfocusedSession or
                    HistoryTimelineEpisodeKind.ShallowDwellDistraction or
                    HistoryTimelineEpisodeKind.FocusDepartureDistraction;
                if (realDurationMin < 1d) continue;

                double blockH = realDurationMin / 60d * rowH;

                // Only advance draw cursor for non-overload (overload sits on top)
                if (!isOverloadEp)
                    prevDrawnEndMin = clampedEnd;

                // Position is purely time-based — always in sync with the hour labels.
                double topOffset = (clampedStart - gridStartMin) / 60d * rowH;

                // Label content based on block height (120px/hr scale):
                // ≥60px (~30 min) → 3 lines: state + time range + duration
                // ≥36px (~18 min) → 2 lines: state + time range
                // ≥20px (~10 min) → 1 line:  state only
                // <20px           → no text (thin bar — too small)
                string stateLabel = GetCalendarEpisodeLabel(ep.Kind);
                string timeRange  = $"{localStart:HH:mm} → {localEnd:HH:mm}";
                string duration   = FormatReadableDuration(ep.DurationSeconds);

                UIElement? blockContent = null;
                if (blockH >= 60d)
                {
                    var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
                    panel.Children.Add(new TextBlock { Text = stateLabel, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    panel.Children.Add(new TextBlock { Text = timeRange,  FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                    panel.Children.Add(new TextBlock { Text = duration,   FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                    blockContent = panel;
                }
                else if (blockH >= 36d)
                {
                    var panel = new StackPanel { Margin = new Thickness(8, 5, 8, 5) };
                    panel.Children.Add(new TextBlock { Text = stateLabel, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                    panel.Children.Add(new TextBlock { Text = timeRange,  FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
                    blockContent = panel;
                }
                else if (blockH >= 20d)
                {
                    blockContent = new TextBlock { Text = stateLabel, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 3, 8, 3) };
                }

                                // Overload = narrow red stripe on right edge of column, rendered on top
                double blockWidth = isOverloadEp ? 6d : colW - 4d;
                double blockLeft  = isOverloadEp ? colX + colW - 8d : colX + 2d;

                var block = new Border
                {
                    Width       = blockWidth,
                    Height      = blockH,
                    Background  = new SolidColorBrush(GetCalendarEpisodeColor(ep.Kind)),
                    CornerRadius = isOverloadEp ? new CornerRadius(2) : new CornerRadius(3),
                    Opacity      = isOverloadEp ? 1.0 : 0.85,
                    ToolTip      = BuildCalendarTooltip(ep),
                    ClipToBounds = true,
                    Child        = isOverloadEp ? null : blockContent
                };
                if (isOverloadEp) Panel.SetZIndex(block, 10);

                Canvas.SetLeft(block, blockLeft);
                Canvas.SetTop(block, topOffset);
                HistoryCalendarCanvas.Children.Add(block);
            }


        }


        // ── Overload pass: added LAST so always renders on top (painter's algorithm) ──
        for (int ci2 = 0; ci2 < numCols; ci2++)
        {
            var ocol = columns[ci2];
            double ocolX = ci2 * colW + colOffsetX;
            var colOverload = heatmap.Episodes
                .Where(e => e.Kind == HistoryTimelineEpisodeKind.OverloadEpisode)
                .Where(e =>
                {
                    var s = e.StartUtc.ToLocalTime().Date;
                    var en = e.EndUtc.ToLocalTime().Date;
                    return s == ocol.Date.Date || (en == ocol.Date.Date && e.EndUtc.ToLocalTime().TimeOfDay.TotalMinutes > 5d);
                });
            foreach (var ep in colOverload)
            {
                var oStart = ep.StartUtc.ToLocalTime();
                var oEnd   = ep.EndUtc.ToLocalTime();
                double oSMin = oStart.Hour * 60d + oStart.Minute;
                double oEMin = oEnd.Hour * 60d + oEnd.Minute;
                if (oEMin < oSMin) { if (oStart.Date == ocol.Date.Date) oEMin = 1440d; else oSMin = 0d; }
                double oC0 = Math.Max(oSMin, startHour * 60d);
                double oC1 = Math.Min(oEMin, endHour * 60d);
                if (oC1 - oC0 < 1d) continue;
                double oTop = (oC0 - startHour * 60d) / 60d * rowH;
                double oH   = Math.Max((oC1 - oC0) / 60d * rowH, 8d);
                var ob = new Border
                {
                    Width  = colW - 4d, Height = oH,
                    Background  = new SolidColorBrush(Color.FromArgb(0xDD, 0xEF, 0x44, 0x44)),
                    CornerRadius = new CornerRadius(2),
                    ToolTip = $"Overload · {FormatReadableDuration(ep.DurationSeconds)}",
                    Child = oH >= 20d ? new TextBlock { Text = $"Overload · {FormatReadableDuration(ep.DurationSeconds)}", FontSize = 10, Foreground = Brushes.White, Padding = new Thickness(4,2,4,2) } : null
                };
                Canvas.SetLeft(ob, ocolX + 2d);
                Canvas.SetTop(ob, oTop);
                HistoryCalendarCanvas.Children.Add(ob);
            }
        }
    }

    private static HistoryFocusHeatmap OverlayTodayTimeline(
        HistoryFocusHeatmap heatmap,
        LocalApiTimelineResponse todayTimeline)
    {
        DateTime today = DateTime.Today;
        // Build accurate today episodes from the 60s timeline (real OverloadActive flags)
        IReadOnlyList<HistoryTimelineEpisode> todayEps =
            HistoryTimelineEpisodeBuilder.Build(todayTimeline);
        // Drop today's coarse-bucket episodes, substitute the accurate 60s-based ones
        List<HistoryTimelineEpisode> merged =
        [
            ..heatmap.Episodes.Where(e => e.StartUtc.ToLocalTime().Date != today),
            ..todayEps.Where(e => e.StartUtc.ToLocalTime().Date == today),
        ];
        merged.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return heatmap with { Episodes = merged };
    }

    /// <summary>
    /// In the 2-week view (600 s buckets) <c>OverloadActive</c> is frequently false even
    /// when the user was actually overloaded, because the API does not preserve the flag
    /// reliably at that coarse granularity.  The 7-day (300 s) week timeline captures
    /// overload far more accurately.  This method replaces any overload episodes that fall
    /// within the last 7 days with the ones rebuilt from the 300 s
    /// <paramref name="weekTimeline"/>, giving the 2-week view the same overload accuracy
    /// as the week view for the overlapping date range.
    /// </summary>
    private static HistoryFocusHeatmap OverlayWeekOverloadOnTwoWeekHeatmap(
        HistoryFocusHeatmap heatmap,
        LocalApiTimelineResponse weekTimeline)
    {
        DateTime today = DateTime.Today;
        // The 7-day week timeline spans the 6 days before today through today (inclusive).
        DateTime weekStart = today.AddDays(-6);

        // Build episodes from the more-accurate 300 s week timeline
        IReadOnlyList<HistoryTimelineEpisode> weekEps =
            HistoryTimelineEpisodeBuilder.Build(weekTimeline);

        // Overload episodes from the week timeline within the overlapping window
        IEnumerable<HistoryTimelineEpisode> weekOverload = weekEps.Where(e =>
            e.Kind == HistoryTimelineEpisodeKind.OverloadEpisode &&
            e.StartUtc.ToLocalTime().Date >= weekStart);

        List<HistoryTimelineEpisode> merged =
        [
            // All non-overload episodes from 2-week heatmap are unchanged
            ..heatmap.Episodes.Where(e => e.Kind != HistoryTimelineEpisodeKind.OverloadEpisode),
            // Overload episodes OLDER than the 7-day window stay from the 2-week heatmap
            ..heatmap.Episodes.Where(e =>
                e.Kind == HistoryTimelineEpisodeKind.OverloadEpisode &&
                e.StartUtc.ToLocalTime().Date < weekStart),
            // Replace overload for the last 7 days with the more-accurate 300 s data
            ..weekOverload,
        ];
        merged.Sort(static (a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return heatmap with { Episodes = merged };
    }

    private static string GetCalendarEpisodeLabel(HistoryTimelineEpisodeKind kind) => kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode                                                            => "Focus",
        HistoryTimelineEpisodeKind.Away                                                                    => "Away",
        HistoryTimelineEpisodeKind.ContextExcursion                                                     => "Excursion",
        HistoryTimelineEpisodeKind.OffTaskDistraction or
        HistoryTimelineEpisodeKind.FragmentationDistraction or
        HistoryTimelineEpisodeKind.RollingMicroBurst or
        HistoryTimelineEpisodeKind.ReturnLoopDistraction or
        HistoryTimelineEpisodeKind.UnfocusedSession or
        HistoryTimelineEpisodeKind.ShallowDwellDistraction or
        HistoryTimelineEpisodeKind.FocusDepartureDistraction   => "Distraction",
        HistoryTimelineEpisodeKind.OverloadEpisode                                                         => "Overload",
        _                                                                                                   => kind.ToString()
    };

    private static Color GetCalendarEpisodeColor(HistoryTimelineEpisodeKind kind) => kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode                                                   => Color.FromRgb(0x16, 0xC7, 0x84),
        HistoryTimelineEpisodeKind.Away                                                            => Color.FromRgb(0x3B, 0x82, 0xF6),
        HistoryTimelineEpisodeKind.ContextExcursion                                                => Color.FromRgb(0xF5, 0x9E, 0x0B),
        HistoryTimelineEpisodeKind.OffTaskDistraction or
        HistoryTimelineEpisodeKind.FragmentationDistraction or
        HistoryTimelineEpisodeKind.RollingMicroBurst or
        HistoryTimelineEpisodeKind.ReturnLoopDistraction or
        HistoryTimelineEpisodeKind.UnfocusedSession or
        HistoryTimelineEpisodeKind.ShallowDwellDistraction or
        HistoryTimelineEpisodeKind.FocusDepartureDistraction   => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.OverloadEpisode                                                => Color.FromRgb(0xEF, 0x44, 0x44),
        _                                                                                          => Color.FromRgb(0x47, 0x55, 0x69)
    };

    private static ToolTip BuildCalendarTooltip(HistoryTimelineEpisode ep)
    {
        string stateName = ep.Kind switch
        {
            HistoryTimelineEpisodeKind.FocusEpisode            => "Focus",
            HistoryTimelineEpisodeKind.Away                    => "Away",
            HistoryTimelineEpisodeKind.ContextExcursion        => "Excursion",
            HistoryTimelineEpisodeKind.OffTaskDistraction or
            HistoryTimelineEpisodeKind.FragmentationDistraction or
            HistoryTimelineEpisodeKind.RollingMicroBurst or
            HistoryTimelineEpisodeKind.ReturnLoopDistraction or
            HistoryTimelineEpisodeKind.UnfocusedSession or
            HistoryTimelineEpisodeKind.ShallowDwellDistraction or
            HistoryTimelineEpisodeKind.FocusDepartureDistraction => "Distraction",
            HistoryTimelineEpisodeKind.OverloadEpisode         => "Overload",
            _                                                  => "Unknown"
        };
        string duration = FormatReadableDuration(ep.DurationSeconds);
        string range = $"{ep.StartUtc.ToLocalTime():HH:mm} → {ep.EndUtc.ToLocalTime():HH:mm}";

        var panel = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        panel.Children.Add(new TextBlock { Text = stateName, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)) });
        panel.Children.Add(new TextBlock { Text = range,     FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)), Margin = new Thickness(0,1,0,0) });
        panel.Children.Add(new TextBlock { Text = duration,  FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), Margin = new Thickness(0,1,0,0) });

        return new ToolTip
        {
            Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            HasDropShadow = false,
            Padding = new Thickness(0)
        };
    }

    private List<CalendarColumn> GetCalendarColumns()
    {
        var cols = new List<CalendarColumn>();
        DateTimeOffset now = DateTimeOffset.Now;

        if (_historyPeriod == HistoryPeriod.Day)
        {
            cols.Add(new CalendarColumn(
                now.Date,
                now.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture),
                true));
        }
        else
        {
            int days = _historyPeriod == HistoryPeriod.Week ? 7 : 14;
            bool abbrev = days == 14;
            for (int i = days - 1; i >= 0; i--)
            {
                DateTime d = now.Date.AddDays(-i);
                bool isToday = d == now.Date;
                string label = abbrev
                    ? d.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture)[..Math.Min(4, d.ToString("ddd d").Length)] + d.Day
                    : d.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture);
                cols.Add(new CalendarColumn(d, label, isToday));
            }
        }
        return cols;
    }

    private void RenderHistoryTimelineNew(HistoryFocusHeatmap heatmap)
    {
        if (!heatmap.HasAnyTracking)
        {
            HistoryTimelineEmptyText.Visibility = Visibility.Visible;
            HistoryDayBarContainer.Visibility = Visibility.Collapsed;
            HistoryMultiDayPanel.Visibility = Visibility.Collapsed;
            HistoryTimeAxisLabel.Text = string.Empty;
            return;
        }

        HistoryTimelineEmptyText.Visibility = Visibility.Collapsed;

        if (_historyPeriod == HistoryPeriod.Day)
        {
            HistoryDayBarContainer.Visibility = Visibility.Visible;
            HistoryMultiDayPanel.Visibility = Visibility.Collapsed;
            RenderDayTimelineBar(heatmap.Episodes, HistoryDayTimelineGrid, HistoryTimeAxisLabel, height: 20d);
        }
        else
        {
            HistoryDayBarContainer.Visibility = Visibility.Collapsed;
            HistoryMultiDayPanel.Visibility = Visibility.Visible;
            HistoryMultiDayPanel.Children.Clear();
            HistoryTimeAxisLabel.Text = string.Empty;

            int days = _historyPeriod == HistoryPeriod.Week ? 7 : 14;
            DateTimeOffset now = DateTimeOffset.Now;

            for (int i = days - 1; i >= 0; i--)
            {
                DateTime day = now.Date.AddDays(-i);
                DateTimeOffset dayStart = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
                DateTimeOffset dayEnd = dayStart.AddDays(1);

                var dayEpisodes = heatmap.Episodes
                    .Where(e => e.StartUtc.ToLocalTime() < dayEnd && e.EndUtc.ToLocalTime() > dayStart)
                    .ToList();

                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dayLabel = new TextBlock
                {
                    Text = day.ToString("ddd d", System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dayLabel, 0);
                row.Children.Add(dayLabel);

                var barTrack = new Border
                {
                    Height = 12,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
                    ClipToBounds = true
                };
                var barGrid = new Grid();
                barTrack.Child = barGrid;
                Grid.SetColumn(barTrack, 1);
                row.Children.Add(barTrack);

                // Day-click handler: switch to Day view for that day
                int capturedIndex = i;
                barTrack.MouseLeftButtonUp += (_, _) =>
                {
                    _ = capturedIndex;
                    _historyPeriod = HistoryPeriod.Day;
                    _historyRange = HistoryRange.Today;
                    UpdateHistoryPeriodTabs();
                    PopulateTimeline(_todayTimeline ?? GetSelectedHistoryTimeline());
                };
                barTrack.Cursor = Cursors.Hand;

                // Dummy axis label for multi-day rows
                var axisLabel = new TextBlock { Text = string.Empty };
                RenderDayTimelineBar(dayEpisodes, barGrid, axisLabel, height: 12d);

                HistoryMultiDayPanel.Children.Add(row);
            }
        }
    }

    private static void RenderDayTimelineBar(
        IEnumerable<HistoryTimelineEpisode> episodeSource,
        Grid grid,
        TextBlock axisLabel,
        double height)
    {
        grid.ColumnDefinitions.Clear();
        grid.Children.Clear();

        var episodes = episodeSource.OrderBy(e => e.StartUtc).ToList();
        if (episodes.Count == 0)
        {
            return;
        }

        DateTimeOffset barStart = episodes[0].StartUtc.ToLocalTime();
        DateTimeOffset barEnd = episodes[^1].EndUtc.ToLocalTime();
        double totalSeconds = (barEnd - barStart).TotalSeconds;
        if (totalSeconds <= 0d) return;

        // Update axis label
        axisLabel.Text = $"{barStart:HH:mm} → {barEnd:HH:mm}";

        DateTimeOffset cursor = barStart;
        int colIdx = 0;

        foreach (var episode in episodes)
        {
            DateTimeOffset epStart = episode.StartUtc.ToLocalTime();
            DateTimeOffset epEnd = episode.EndUtc.ToLocalTime();

            // Gap before episode
            double gapSecs = Math.Max(0d, (epStart - cursor).TotalSeconds);
            if (gapSecs > 30d)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gapSecs, GridUnitType.Star) });
                var gapBorder = new Border { Background = Brushes.Transparent };
                Grid.SetColumn(gapBorder, colIdx++);
                grid.Children.Add(gapBorder);
            }

            // Episode segment
            double epSecs = Math.Max(1d, episode.DurationSeconds);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(epSecs, GridUnitType.Star) });
            var segBorder = new Border
            {
                Background = new SolidColorBrush(GetEpisodeTimelineColor(episode.Kind)),
                ToolTip = $"{GetEpisodeTimelineLabel(episode.Kind)} · {FormatReadableDuration(epSecs)}"
            };
            Grid.SetColumn(segBorder, colIdx++);
            grid.Children.Add(segBorder);

            cursor = epEnd;
        }

        // Trailing gap
        double trailSecs = (barEnd - cursor).TotalSeconds;
        if (trailSecs > 30d)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(trailSecs, GridUnitType.Star) });
            var trail = new Border { Background = Brushes.Transparent };
            Grid.SetColumn(trail, colIdx);
            grid.Children.Add(trail);
        }
    }

    private static string GetEpisodeTimelineLabel(HistoryTimelineEpisodeKind kind) => kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode     => "Focus",
        HistoryTimelineEpisodeKind.Away             => "Away",
        HistoryTimelineEpisodeKind.OverloadEpisode  => "Overload",
        HistoryTimelineEpisodeKind.ContextExcursion => "Excursion",
        HistoryTimelineEpisodeKind.OffTaskDistraction or
        HistoryTimelineEpisodeKind.FragmentationDistraction or
        HistoryTimelineEpisodeKind.RollingMicroBurst or
        HistoryTimelineEpisodeKind.ReturnLoopDistraction or
        HistoryTimelineEpisodeKind.UnfocusedSession or
        HistoryTimelineEpisodeKind.ShallowDwellDistraction or
        HistoryTimelineEpisodeKind.FocusDepartureDistraction => "Distraction",
        _ => "Episode"
    };

    private static Color GetEpisodeTimelineColor(HistoryTimelineEpisodeKind kind) => kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode               => Color.FromRgb(0x16, 0xC7, 0x84),
        HistoryTimelineEpisodeKind.OffTaskDistraction          => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.FragmentationDistraction    => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.ContextExcursion            => Color.FromRgb(0xF5, 0x9E, 0x0B),
        HistoryTimelineEpisodeKind.RollingMicroBurst           => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.ReturnLoopDistraction       => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.UnfocusedSession            => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.ShallowDwellDistraction     => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.FocusDepartureDistraction   => Color.FromRgb(0x8B, 0x5C, 0xF6),
        HistoryTimelineEpisodeKind.Away                        => Color.FromRgb(0x3B, 0x82, 0xF6),
        HistoryTimelineEpisodeKind.OverloadEpisode             => Color.FromRgb(0xEF, 0x44, 0x44),
        _                                                      => Color.FromRgb(0x47, 0x55, 0x69)
    };

    private void PopulateHistoryEpisodeListNew(HistoryFocusHeatmap heatmap)
    {
        TimelineEpisodeListPanel.Children.Clear();

        if (!heatmap.HasAnyTracking || heatmap.Episodes.Count == 0)
        {
            HistoryEpisodeListTitle.Text = string.Empty;
            return;
        }

        var episodes = heatmap.Episodes.OrderBy(static e => e.StartUtc).ToList();

        if (_historyPeriod == HistoryPeriod.Day)
        {
            HistoryEpisodeListTitle.Text = episodes.Count == 1 ? "1 EPISODE" : $"{episodes.Count} EPISODES";
            foreach (var ep in episodes)
            {
                TimelineEpisodeListPanel.Children.Add(BuildTimelineEpisodeCard(ep));
            }
        }
        else
        {
            // Group by local date
            HistoryEpisodeListTitle.Text = string.Empty;
            var grouped = episodes.GroupBy(e => e.StartUtc.ToLocalTime().Date).OrderBy(g => g.Key);
            foreach (var group in grouped)
            {
                var dayHeader = new TextBlock
                {
                    Text = group.Key.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
                    Margin = new Thickness(0, 12, 0, 6)
                };
                TimelineEpisodeListPanel.Children.Add(dayHeader);
                foreach (var ep in group.OrderBy(e => e.StartUtc))
                {
                    TimelineEpisodeListPanel.Children.Add(BuildTimelineEpisodeCard(ep));
                }
            }
        }
    }

    private void RenderHistoryCoordinateHeatmap(HistoryFocusHeatmap heatmap)
    {
        HistoryCoordinateCanvas.Children.Clear();
        PopulateHistoryHourLabels();
        HistoryCoordinateEmptyText.Text = BuildHistoryEmptyChartText();
        HistoryCoordinateEmptyText.Visibility = heatmap.HasAnyTracking ? Visibility.Collapsed : Visibility.Visible;
        HistoryCoordinateXAxisText.Text = BuildHistoryXAxisText(heatmap);

        double chartHeight = 24d * HistoryHeatmapCellHeight;
        double gridTop = HistoryHeatmapHeaderHeight;
        double gridBottom = gridTop + chartHeight;
        double canvasHeight = gridBottom + HistoryHeatmapFooterHeight + HistoryHeatmapCanvasPadding;
        double canvasWidth = Math.Max(
            320d,
            (HistoryHeatmapCanvasPadding * 2d) + (heatmap.Days.Count * HistoryHeatmapCellWidth));
        HistoryCoordinateCanvas.Height = canvasHeight;
        HistoryCoordinateCanvas.Width = canvasWidth;

        foreach (HistoryFocusHeatmapDay day in heatmap.Days)
        {
            double x = HistoryHeatmapCanvasPadding + (day.DayIndex * HistoryHeatmapCellWidth);

            var divider = new Line
            {
                X1 = x,
                X2 = x,
                Y1 = gridTop,
                Y2 = gridBottom,
                Stroke = new SolidColorBrush(Color.FromRgb(36, 48, 66)),
                StrokeThickness = 0.8d,
                SnapsToDevicePixels = true
            };
            HistoryCoordinateCanvas.Children.Add(divider);

            var dayLabel = new TextBlock
            {
                Text = heatmap.Days.Count > 8 ? day.ShortLabel : day.HeaderLabel,
                Foreground = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                Width = HistoryHeatmapCellWidth
            };
            Canvas.SetLeft(dayLabel, x);
            Canvas.SetTop(dayLabel, gridBottom + 6d);
            HistoryCoordinateCanvas.Children.Add(dayLabel);
        }

        double rightEdge = HistoryHeatmapCanvasPadding + (heatmap.Days.Count * HistoryHeatmapCellWidth);
        HistoryCoordinateCanvas.Children.Add(new Line
        {
            X1 = rightEdge,
            X2 = rightEdge,
            Y1 = gridTop,
            Y2 = gridBottom,
            Stroke = new SolidColorBrush(Color.FromRgb(36, 48, 66)),
            StrokeThickness = 0.8d,
            SnapsToDevicePixels = true
        });

        for (int hour = 0; hour <= 24; hour++)
        {
            double y = gridTop + (hour * HistoryHeatmapCellHeight);
            HistoryCoordinateCanvas.Children.Add(new Line
            {
                X1 = HistoryHeatmapCanvasPadding,
                X2 = rightEdge,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(hour is 0 or 24 ? Color.FromRgb(71, 85, 105) : Color.FromRgb(36, 48, 66)),
                StrokeThickness = hour is 0 or 24 ? 1d : 0.8d,
                SnapsToDevicePixels = true
            });
        }

        foreach (HistoryFocusHeatmapCell cell in heatmap.Cells)
        {
            double left = HistoryHeatmapCanvasPadding + (cell.DayIndex * HistoryHeatmapCellWidth) + 1d;
            double top = gridTop + (GetHistoryHeatmapRowIndex(cell.Hour) * HistoryHeatmapCellHeight) + 1d;
            bool selected = string.Equals(_selectedHistoryCellKey, cell.Key, StringComparison.Ordinal);

            var button = new Button
            {
                Width = HistoryHeatmapCellWidth - 2d,
                Height = HistoryHeatmapCellHeight - 2d,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(selected ? 1.5d : cell.HasTracking ? 1d : 0d),
                BorderBrush = selected
                    ? new SolidColorBrush(Color.FromRgb(99, 102, 241))
                    : cell.HasTracking
                        ? new SolidColorBrush(Color.FromRgb(44, 44, 70))
                        : Brushes.Transparent,
                Background = CreateHistoryHeatmapCellBackground(cell, heatmap.MaxFocusedSecondsPerHour),
                Content = cell.HasTracking ? BuildHistoryHeatmapCellContent(cell) : null,
                Tag = cell,
                Cursor = cell.HasTracking ? Cursors.Hand : Cursors.Arrow,
                Focusable = false,
                ToolTip = BuildHistoryHeatmapCellTooltip(cell)
            };
            button.Click += TimelineHeatmapCellButton_Click;

            Canvas.SetLeft(button, left);
            Canvas.SetTop(button, top);
            HistoryCoordinateCanvas.Children.Add(button);
        }
    }

    private void PopulateHistoryHourLabels()
    {
        HistoryHourLabelsPanel.Children.Clear();
        HistoryHourLabelsPanel.Children.Add(new Border { Height = HistoryHeatmapHeaderHeight });

        for (int hour = 23; hour >= 0; hour--)
        {
            HistoryHourLabelsPanel.Children.Add(new Border
            {
                Height = HistoryHeatmapCellHeight,
                Child = new TextBlock
                {
                    Text = $"{hour:00}",
                    Foreground = new SolidColorBrush(hour is 0 or 23 ? Color.FromRgb(148, 163, 184) : Color.FromRgb(100, 116, 139)),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right
                }
            });
        }
    }

    private static int GetHistoryHeatmapRowIndex(int hour) => 23 - Math.Clamp(hour, 0, 23);

    private (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ResolveHistoryDisplayRangeUtc(DateTimeOffset nowUtc)
    {
        // When viewing week/2-week period, the fallback range must cover the full period
        // even if _historyRange is Today -- otherwise null timelines render almost empty.
        if (_historyPeriod == HistoryPeriod.Week)
            return (nowUtc.AddDays(-7), nowUtc);
        if (_historyPeriod == HistoryPeriod.TwoWeeks)
            return (nowUtc.AddDays(-14), nowUtc);

        DateTimeOffset todayFromUtc = LocalTimelineRangeResolver.ResolveLocalDayStartUtc(nowUtc);
        return _historyRange switch
        {
            HistoryRange.Last1Hour  => (nowUtc.AddHours(-1), nowUtc),
            HistoryRange.Last4Hours => (nowUtc.AddHours(-4), nowUtc),
            HistoryRange.Today      => (todayFromUtc, nowUtc),
            HistoryRange.AllTime    => (nowUtc.AddDays(-Math.Max(1, Math.Min(_retention?.RetentionDays ?? 7, 7))), nowUtc),
            _                       => (todayFromUtc, nowUtc)
        };
    }

    private string BuildHistoryXAxisText(HistoryFocusHeatmap heatmap)
    {
        if (heatmap.Days.Count == 0)
        {
            return "Oldest day on the left, newest day on the right";
        }

        HistoryFocusHeatmapDay first = heatmap.Days[0];
        HistoryFocusHeatmapDay last = heatmap.Days[^1];
        return heatmap.Days.Count == 1
            ? $"{first.HeaderLabel} | single retained day"
            : $"{first.ShortLabel} on the left through {last.ShortLabel} on the right";
    }

    private string BuildHistoryLeadSentence(HistoryFocusHeatmap heatmap)
    {
        string rangeLabel = heatmap.Days.Count == 1
            ? heatmap.Days[0].HeaderLabel
            : $"{heatmap.Days[0].ShortLabel} through {heatmap.Days[^1].ShortLabel}";
        string focusSummary = BuildHistoryEpisodeMetricSummary(
            heatmap.FocusEpisodeCount,
            heatmap.Episodes.Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.FocusEpisode).Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");
        string distractionSummary = BuildHistoryEpisodeMetricSummary(
            heatmap.DistractionEpisodeCount,
            heatmap.Episodes.Where(static episode => episode.Kind is
                    HistoryTimelineEpisodeKind.OffTaskDistraction or
                    HistoryTimelineEpisodeKind.FragmentationDistraction or
                    HistoryTimelineEpisodeKind.RollingMicroBurst or
                    HistoryTimelineEpisodeKind.ReturnLoopDistraction or
                    HistoryTimelineEpisodeKind.UnfocusedSession or
                    HistoryTimelineEpisodeKind.ShallowDwellDistraction or
                    HistoryTimelineEpisodeKind.FocusDepartureDistraction).Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");
        string overloadSummary = BuildHistoryEpisodeMetricSummary(
            heatmap.OverloadEpisodeCount,
            heatmap.Episodes.Where(static episode => episode.Kind == HistoryTimelineEpisodeKind.OverloadEpisode).Sum(static episode => episode.DurationSeconds),
            singularLabel: "episode",
            pluralLabel: "episodes");

        return $"Showing {rangeLabel}. Focus {focusSummary}, distraction {distractionSummary}, overload {overloadSummary}.";
    }

    private static string BuildHistoryEpisodeMetricSummary(int count, double totalSeconds, string singularLabel, string pluralLabel)
    {
        string countLabel = count == 1 ? singularLabel : pluralLabel;
        return $"{count} {countLabel} | {FormatReadableDuration(totalSeconds)}";
    }

    private static string BuildHistoryRangeFootnote(HistoryFocusHeatmap heatmap)
    {
        if (heatmap.Days.Count == 0)
        {
            return "Retained days will appear here.";
        }

        return heatmap.Days.Count == 1
            ? $"Showing {heatmap.Days[0].HeaderLabel}"
            : $"Showing {heatmap.Days[0].ShortLabel} through {heatmap.Days[^1].ShortLabel}";
    }

    private string BuildHistoryBestHourSummary(HistoryFocusHeatmapCell? bestCell)
    {
        if (bestCell == null)
        {
            return "Best hour will appear here";
        }

        return $"{bestCell.LocalDate:ddd} {bestCell.Hour:00}:00 | {FormatReadableDuration(bestCell.FocusedSeconds)} focused";
    }

    private string BuildHistoryActionsSummary(HistoryFocusHeatmap heatmap)
    {
        string episodeLead = heatmap.Episodes.Count == 1
            ? "1 episode overlaps this range."
            : $"{heatmap.Episodes.Count} episodes overlap this range.";
        if (heatmap.ActionMarkerCount == 0)
        {
            return $"{episodeLead} No action markers landed in this range yet. Click any populated hour for details.";
        }

        string markerLabel = heatmap.ActionMarkerCount == 1 ? "marker" : "markers";
        return $"{episodeLead} {heatmap.ActionMarkerCount} action {markerLabel} landed in this range. Click a populated hour or marker for details.";
    }

    private string BuildHistoryHeatmapCellTooltip(HistoryFocusHeatmapCell cell)
    {
        string prefix = $"{cell.LocalDate:yyyy-MM-dd} {cell.HourLabel}";
        if (!cell.HasTracking)
        {
            return $"{prefix}{Environment.NewLine}No tracked work in this hour yet.";
        }

        return $"{prefix}{Environment.NewLine}Focus {FormatReadableDuration(cell.FocusEpisodeSeconds)} | Distraction {FormatReadableDuration(cell.DistractionEpisodeSeconds)} | Away {FormatReadableDuration(cell.AwayEpisodeSeconds)}{Environment.NewLine}Overload {FormatReadableDuration(cell.OverloadEpisodeSeconds)} | Pressure {cell.AverageOverloadPercent:F0}% | Confidence {cell.AverageConfidencePercent:F0}%";
    }

    private FrameworkElement BuildHistoryHeatmapCellContent(HistoryFocusHeatmapCell cell)
    {
        Grid root = new()
        {
            Margin = new Thickness(5, 3, 5, 3)
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(new TextBlock
        {
            Text = BuildHistoryCellValueText(cell),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateHistoryHeatmapCellTextBrush(cell),
            Margin = new Thickness(0, 0, 0, 3),
            TextAlignment = TextAlignment.Left
        });

        Border attentionTrack = new()
        {
            Height = 8,
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Child = BuildHistoryEpisodeTrackCanvas(
                cell,
                includeAttentionEpisodes: true,
                includeOverloadEpisodes: false,
                trackWidth: HistoryHeatmapCellWidth - 12d,
                trackHeight: 8d)
        };
        Grid.SetRow(attentionTrack, 1);
        root.Children.Add(attentionTrack);

        Border overloadTrack = new()
        {
            Height = 4,
            Margin = new Thickness(0, 3, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = BuildHistoryEpisodeTrackCanvas(
                cell,
                includeAttentionEpisodes: false,
                includeOverloadEpisodes: true,
                trackWidth: HistoryHeatmapCellWidth - 12d,
                trackHeight: 4d)
        };
        Grid.SetRow(overloadTrack, 2);
        root.Children.Add(overloadTrack);

        return root;
    }

    private Canvas BuildHistoryEpisodeTrackCanvas(
        HistoryFocusHeatmapCell cell,
        bool includeAttentionEpisodes,
        bool includeOverloadEpisodes,
        double trackWidth,
        double trackHeight)
    {
        Canvas canvas = new()
        {
            Width = trackWidth,
            Height = trackHeight,
            ClipToBounds = true,
            IsHitTestVisible = false
        };

        if (!cell.HasTracking)
        {
            return canvas;
        }

        foreach (HistoryTimelineEpisode episode in cell.Episodes.OrderBy(static episode => episode.StartUtc))
        {
            bool isOverload = episode.Kind == HistoryTimelineEpisodeKind.OverloadEpisode;
            if (isOverload != includeOverloadEpisodes)
            {
                continue;
            }

            if (!isOverload && !includeAttentionEpisodes)
            {
                continue;
            }

            if (isOverload && !includeOverloadEpisodes)
            {
                continue;
            }

            double overlapSeconds = episode.GetOverlapSeconds(cell.LocalDate, cell.Hour);
            if (overlapSeconds <= 0d)
            {
                continue;
            }

            double startOffsetSeconds = episode.GetStartOffsetSeconds(cell.LocalDate, cell.Hour);
            double left = Math.Clamp((startOffsetSeconds / 3600d) * trackWidth, 0d, trackWidth);
            if (left >= trackWidth)
            {
                continue;
            }

            double width = Math.Min(trackWidth - left, (overlapSeconds / 3600d) * trackWidth);
            if (width <= 0d)
            {
                continue;
            }

            width = Math.Max(2d, width);

            Border segment = new()
            {
                Width = width,
                Height = trackHeight,
                Background = CreateHistoryEpisodeSegmentBrush(episode.Kind),
                CornerRadius = new CornerRadius(trackHeight / 2d)
            };
            Canvas.SetLeft(segment, left);
            Canvas.SetTop(segment, 0d);
            canvas.Children.Add(segment);
        }

        return canvas;
    }

    private static Brush CreateHistoryEpisodeSegmentBrush(HistoryTimelineEpisodeKind kind) => kind switch
    {
        HistoryTimelineEpisodeKind.FocusEpisode           => new SolidColorBrush(Color.FromRgb(0x16, 0xC7, 0x84)),
        HistoryTimelineEpisodeKind.ContextExcursion       => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)), // amber
        HistoryTimelineEpisodeKind.OffTaskDistraction or
        HistoryTimelineEpisodeKind.FragmentationDistraction or
        HistoryTimelineEpisodeKind.RollingMicroBurst or
        HistoryTimelineEpisodeKind.ReturnLoopDistraction or
        HistoryTimelineEpisodeKind.UnfocusedSession or
        HistoryTimelineEpisodeKind.ShallowDwellDistraction or
        HistoryTimelineEpisodeKind.FocusDepartureDistraction => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
        HistoryTimelineEpisodeKind.Away                   => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        HistoryTimelineEpisodeKind.OverloadEpisode        => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _                                                 => new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69))
    };

    private static string BuildHistoryCellValueText(HistoryFocusHeatmapCell cell)
    {
        if (!cell.HasTracking)
        {
            return string.Empty;
        }

        int minutes = (int)Math.Round(Math.Max(0d, cell.FocusedSeconds) / 60d, MidpointRounding.AwayFromZero);
        return $"{minutes}m";
    }

    private static Brush CreateHistoryHeatmapCellTextBrush(HistoryFocusHeatmapCell cell)
    {
        if (!cell.HasTracking)
        {
            return new SolidColorBrush(Color.FromRgb(100, 116, 139));
        }

        return cell.FocusedSeconds >= 1800d
            ? new SolidColorBrush(Color.FromRgb(240, 253, 244))
            : new SolidColorBrush(Color.FromRgb(226, 232, 240));
    }

    private static Brush CreateHistoryHeatmapCellBackground(HistoryFocusHeatmapCell cell, double maxFocusedSecondsPerHour)
    {
        // Empty cells are plain dark rectangles — no gradient, no depth.
        return new SolidColorBrush(cell.HasTracking
            ? Color.FromRgb(0x1C, 0x1C, 0x2E)
            : Color.FromRgb(0x1C, 0x1C, 0x2E));
    }

    private void TimelineHeatmapCellButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is HistoryFocusHeatmapCell cell)
        {
            _selectedHistoryInterventionId = null;
            _selectedHistorySnapshotTimestampUtc = null;
            _selectedHistoryCellKey = cell.Key;
            SetTimelineCellDetail(cell);
        }
    }

    private void SetTimelineCellDetail(HistoryFocusHeatmapCell? cell)
    {
        if (cell == null)
        {
            TimelineDetailTimestamp.Text = "Select a cell to inspect that hour";
            TimelineDetailClassification.Text = string.Empty;
            PopulateTimelineEpisodeDrilldown([], "the selected hour");
            return;
        }

        // Clean header: "Wed 13 May · 18:00–19:00"
        TimelineDetailTimestamp.Text = $"{cell.LocalDate:ddd d MMM} · {cell.Hour:00}:00–{(cell.Hour + 1) % 24:00}:00";
        // Summary: episode composition as a readable sentence
        TimelineDetailClassification.Text = BuildHistoryCellWhatHappened(cell);
        PopulateTimelineEpisodeDrilldown(cell.Episodes, $"{cell.LocalDate:ddd MMM d} {cell.HourLabel}");
    }

    private static string BuildHistoryCellWhatHappened(HistoryFocusHeatmapCell cell)
    {
        if (!cell.HasTracking)
        {
            return "No tracked work landed in this hour.";
        }

        List<string> parts = new();
        if (cell.FocusEpisodeSeconds > 0d)
        {
            parts.Add($"Focus {FormatReadableDuration(cell.FocusEpisodeSeconds)}");
        }

        if (cell.DistractionEpisodeSeconds > 0d)
        {
            parts.Add($"Distraction {FormatReadableDuration(cell.DistractionEpisodeSeconds)}");
        }

        if (cell.ContextExcursionSeconds > 0d)
        {
            parts.Add($"Excursion {FormatReadableDuration(cell.ContextExcursionSeconds)}");
        }

        if (cell.AwayEpisodeSeconds > 0d)
        {
            parts.Add($"Away {FormatReadableDuration(cell.AwayEpisodeSeconds)}");
        }

        if (cell.OverloadEpisodeSeconds > 0d)
        {
            parts.Add($"Overload {FormatReadableDuration(cell.OverloadEpisodeSeconds)}");
        }

        return parts.Count == 0
            ? $"Tracked {FormatReadableDuration(cell.TrackedSeconds)} with no derived episodes."
            : string.Join(" | ", parts);
    }

    private string BuildHistoryCellMomentSummary(HistoryFocusHeatmapCell cell)
    {
        if (!cell.HasTracking)
        {
            return "No tracked work landed in this hour yet.";
        }

        return $"{BuildHistoryCellWhatHappened(cell)} | Tracked {FormatReadableDuration(cell.TrackedSeconds)}";
    }

    private string BuildHistoryCellSignalsText(HistoryFocusHeatmapCell cell)
    {
        if (cell.LatestSnapshot?.Contributors is { Count: > 0 } contributors)
        {
            string joined = string.Join(", ", contributors.Take(6));
            return _historyExplainMode
                ? $"Top reasons: {FormatTopReasons(joined, _historyExplainMode)}"
                : $"Main signals: {FormatTopReasons(joined, _historyExplainMode)}";
        }

        return _historyExplainMode
            ? "Top reasons: No contributor list is available for the latest processed block in this hour."
            : "Main signals: Ari did not expose a contributor list for the latest processed block in this hour.";
    }

    private string BuildHistoryCellTrackingQualityText(HistoryFocusHeatmapCell cell)
    {
        return _historyExplainMode
            ? $"Quality: avg confidence {cell.AverageConfidencePercent:F0}% across {cell.SnapshotCount} snapshots"
            : $"Tracking quality: Avg confidence {cell.AverageConfidencePercent:F0}% across {cell.SnapshotCount} processed snapshots.";
    }

    private string BuildHistoryCellExecutionText(HistoryFocusHeatmapCell cell)
    {
        if (cell.Interventions.Count == 0)
        {
            return _historyExplainMode
                ? "Decision / execution: No intervention landed in this hour."
                : "Interventions in that hour: none.";
        }

        LocalApiEventResponse latest = cell.Interventions[^1];
        string actionLabel = ToActionTitle(latest);
        string markerLabel = cell.Interventions.Count == 1 ? "intervention" : "interventions";
        return _historyExplainMode
            ? $"Decision / execution: {cell.Interventions.Count} {markerLabel} in this hour. Latest: {actionLabel}."
            : $"Interventions in that hour: {cell.Interventions.Count} {markerLabel}. Latest: {actionLabel}.";
    }

    private string BuildHistoryCellContextText(HistoryFocusHeatmapCell cell)
    {
        if (cell.LatestSnapshot == null)
        {
            return _historyExplainMode
                ? "Stabilization: No processed snapshot is available for this hour."
                : "Context: No processed snapshot is available for this hour.";
        }

        string prefix = _historyExplainMode
            ? $"Latest processed block: {cell.LatestSnapshot.TimestampUtc.ToLocalTime():HH:mm:ss}. "
            : $"Context: Latest processed block in this hour landed at {cell.LatestSnapshot.TimestampUtc.ToLocalTime():HH:mm:ss}. ";
        return prefix + BuildHistoryContextText(cell.LatestSnapshot.StateFlagsJson);
    }

    private void PopulateTimelineEpisodeDrilldown(
        IReadOnlyList<HistoryTimelineEpisode> episodes,
        string contextLabel)
    {
        _ = contextLabel;
        TimelineEpisodeListPanel.Children.Clear();

        if (episodes.Count == 0)
        {
            TimelineEpisodeSummaryText.Text = string.Empty;
            return;
        }

        TimelineEpisodeSummaryText.Text = string.Empty;

        foreach (HistoryTimelineEpisode episode in episodes
                     .OrderBy(static episode => episode.StartUtc))
        {
            TimelineEpisodeListPanel.Children.Add(BuildTimelineEpisodeCard(episode));
        }
    }

    private IReadOnlyList<HistoryTimelineEpisode> FindEpisodesAt(DateTimeOffset timestampUtc)
    {
        IReadOnlyList<HistoryTimelineEpisode> episodes = _latestHistoryHeatmap?.Episodes ?? [];
        if (episodes.Count == 0)
        {
            return [];
        }

        return episodes
            .Where(episode => episode.Contains(timestampUtc))
            .OrderByDescending(static episode => episode.DurationSeconds)
            .ThenBy(static episode => episode.StartUtc)
            .ToArray();
    }

    private Border BuildTimelineEpisodeCard(HistoryTimelineEpisode episode)
    {
        // Spec: type chip + time range + rectangular duration badge. No developer text.
        (Color accentColor, Color _, string kindLabel) = episode.Kind switch
        {
            HistoryTimelineEpisodeKind.FocusEpisode
                => (Color.FromRgb(0x16, 0xC7, 0x84), Color.FromArgb(0x20, 0x16, 0xC7, 0x84), "Focus"),
            HistoryTimelineEpisodeKind.ContextExcursion
                => (Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromArgb(0x20, 0xF5, 0x9E, 0x0B), "Excursion"),
            HistoryTimelineEpisodeKind.OffTaskDistraction or
            HistoryTimelineEpisodeKind.FragmentationDistraction or
            HistoryTimelineEpisodeKind.RollingMicroBurst or
            HistoryTimelineEpisodeKind.ReturnLoopDistraction or
            HistoryTimelineEpisodeKind.UnfocusedSession or
            HistoryTimelineEpisodeKind.ShallowDwellDistraction or
            HistoryTimelineEpisodeKind.FocusDepartureDistraction
                => (Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromArgb(0x20, 0x8B, 0x5C, 0xF6), "Distraction"),
            HistoryTimelineEpisodeKind.Away
                => (Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromArgb(0x20, 0x3B, 0x82, 0xF6), "Away"),
            HistoryTimelineEpisodeKind.OverloadEpisode
                => (Color.FromRgb(0xEF, 0x44, 0x44), Color.FromArgb(0x20, 0xEF, 0x44, 0x44), "Overload"),
            _ => (Color.FromRgb(0x64, 0x74, 0x8B), Color.FromArgb(0x20, 0x64, 0x74, 0x8B), "Unknown")
        };

        // State label — plain coloured text, NO border/background/box
        var typeChip = new TextBlock
        {
            Text = kindLabel,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accentColor),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };

        // Time range — muted secondary text
        var timeText = new TextBlock
        {
            Text = $"{episode.StartUtc.ToLocalTime():HH:mm} → {episode.EndUtc.ToLocalTime():HH:mm}",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        // Duration — plain coloured text, NO border/background/box
        var durationBadge = new TextBlock
        {
            Text = FormatReadableDuration(episode.DurationSeconds),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accentColor),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(typeChip, 0);
        Grid.SetColumn(timeText, 1);
        Grid.SetColumn(durationBadge, 2);
        row.Children.Add(typeChip);
        row.Children.Add(timeText);
        row.Children.Add(durationBadge);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x35)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Child = row
        };
    }

    private static IReadOnlyList<LocalApiCurrentSnapshotResponse> DownsampleSnapshots(
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots,
        int maxPoints)
    {
        if (snapshots.Count <= maxPoints)
        {
            return snapshots;
        }

        int step = Math.Max(1, (int)Math.Ceiling((double)snapshots.Count / maxPoints));
        var reduced = new List<LocalApiCurrentSnapshotResponse>(maxPoints + 1);
        for (int index = 0; index < snapshots.Count; index += step)
        {
            reduced.Add(snapshots[index]);
        }

        LocalApiCurrentSnapshotResponse last = snapshots[^1];
        if (!ReferenceEquals(reduced[^1], last))
        {
            reduced.Add(last);
        }

        return reduced;
    }

    private void RenderHistoryCoordinateChart(
        LocalApiTimelineResponse timeline,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
    {
        HistoryCoordinateCanvas.Children.Clear();
        HistoryCoordinateEmptyText.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCoordinateXAxisText.Text = BuildHistoryXAxisText(timeline);
        if (snapshots.Count == 0)
        {
            HistoryCoordinateCanvas.Width = 320d;
            return;
        }

        const double chartHeight = 220d;
        double xSpacing = _historyRange switch
        {
            HistoryRange.Last1Hour => 7d,
            HistoryRange.Last4Hours => 6d,
            HistoryRange.Today => 5.6d,
            HistoryRange.AllTime => 4.8d,
            _ => 6d
        };
        double chartWidth = Math.Max(320d, 16d + ((snapshots.Count - 1) * xSpacing) + 16d);
        HistoryCoordinateCanvas.Width = chartWidth;

        AddHistoryGridLine(0d, chartWidth, chartHeight, 0d, true);
        AddHistoryGridLine(0d, chartWidth, chartHeight, 25d);
        AddHistoryGridLine(0d, chartWidth, chartHeight, 50d);
        AddHistoryGridLine(0d, chartWidth, chartHeight, 75d);
        AddHistoryGridLine(0d, chartWidth, chartHeight, 100d, true);

        double retainedFocusMaxSeconds = GetRetainedFocusMaxSeconds();
        Polyline focusLine = CreateHistoryPolyline(Color.FromRgb(34, 197, 94));
        Polyline overloadLine = CreateHistoryPolyline(Color.FromRgb(239, 68, 68));
        Polyline trustLine = CreateHistoryPolyline(Color.FromRgb(59, 130, 246));

        for (int index = 0; index < snapshots.Count; index++)
        {
            LocalApiCurrentSnapshotResponse snapshot = snapshots[index];
            double x = 12d + (index * xSpacing);
            double focusPercent = retainedFocusMaxSeconds <= 0d
                ? 0d
                : Math.Clamp(snapshot.SustainedAttentionDurationSec / retainedFocusMaxSeconds, 0d, 1d) * 100d;
            double overloadPercent = ResolvePressureLevel(snapshot) * 100d;
            double trustPercent = Math.Clamp(snapshot.Confidence, 0d, 1d) * 100d;

            focusLine.Points.Add(new Point(x, ToHistoryCanvasY(focusPercent, chartHeight)));
            overloadLine.Points.Add(new Point(x, ToHistoryCanvasY(overloadPercent, chartHeight)));
            trustLine.Points.Add(new Point(x, ToHistoryCanvasY(trustPercent, chartHeight)));

            Button selectionButton = new()
            {
                Width = Math.Max(12d, xSpacing + 4d),
                Height = chartHeight,
                Tag = snapshot,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = BuildHistorySliceTooltip(snapshot, focusPercent)
            };
            selectionButton.Click += TimelineSnapshotButton_Click;
            Canvas.SetLeft(selectionButton, Math.Max(0d, x - (selectionButton.Width / 2d)));
            Canvas.SetTop(selectionButton, 0d);
            HistoryCoordinateCanvas.Children.Add(selectionButton);
        }

        HistoryCoordinateCanvas.Children.Add(overloadLine);
        HistoryCoordinateCanvas.Children.Add(trustLine);
        HistoryCoordinateCanvas.Children.Add(focusLine);
    }

    private void AddHistoryGridLine(
        double left,
        double width,
        double height,
        double percent,
        bool emphasize = false)
    {
        Line line = new()
        {
            X1 = left,
            X2 = width,
            Y1 = ToHistoryCanvasY(percent, height),
            Y2 = ToHistoryCanvasY(percent, height),
            Stroke = emphasize
                ? new SolidColorBrush(Color.FromRgb(71, 85, 105))
                : new SolidColorBrush(Color.FromRgb(36, 48, 66)),
            StrokeThickness = emphasize ? 1d : 0.8d,
            SnapsToDevicePixels = true
        };
        HistoryCoordinateCanvas.Children.Add(line);
    }

    private static Polyline CreateHistoryPolyline(Color color)
    {
        return new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.2d,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
    }

    private static double ToHistoryCanvasY(double percent, double height)
    {
        double clampedPercent = Math.Clamp(percent, 0d, 100d);
        return Math.Max(0d, Math.Min(height, height - ((clampedPercent / 100d) * height)));
    }

    private string BuildHistoryXAxisText(LocalApiTimelineResponse? timeline)
    {
        if (timeline == null)
        {
            return "Time runs left to right";
        }

        return _historyRange switch
        {
            HistoryRange.Last1Hour => "Last hour, earlier on the left and now on the right",
            HistoryRange.Last4Hours => "Last 4 hours, earlier on the left and now on the right",
            HistoryRange.Today => $"Today from {timeline.FromUtc.ToLocalTime():HH:mm} to {timeline.ToUtc.ToLocalTime():HH:mm}",
            HistoryRange.AllTime => _retention == null
                ? "Retained history, oldest on the left and newest on the right"
                : $"Retained {_retention.RetentionDays} days, oldest on the left and newest on the right",
            _ => "Time runs left to right"
        };
    }

    private double GetRetainedFocusMaxSeconds()
    {
        IReadOnlyList<LocalApiCurrentSnapshotResponse> retainedSnapshots = _allTimeTimeline?.Snapshots ?? [];
        if (retainedSnapshots.Count > 0)
        {
            return retainedSnapshots.Max(static snapshot => Math.Max(0d, snapshot.SustainedAttentionDurationSec));
        }

        IReadOnlyList<LocalApiCurrentSnapshotResponse> todaySnapshots = _todayTimeline?.Snapshots ?? [];
        if (todaySnapshots.Count > 0)
        {
            return todaySnapshots.Max(static snapshot => Math.Max(0d, snapshot.SustainedAttentionDurationSec));
        }

        return 0d;
    }

    private string BuildHistoryRangeLabel(
        LocalApiTimelineResponse timeline,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
    {
        if (_historyRange == HistoryRange.AllTime)
        {
            return _retention == null
                ? "Retained history"
                : $"Retained {_retention.RetentionDays} days";
        }

        LocalApiCurrentSnapshotResponse first = snapshots[0];
        LocalApiCurrentSnapshotResponse latest = snapshots[^1];
        return $"{first.TimestampUtc.ToLocalTime():HH:mm} to {latest.TimestampUtc.ToLocalTime():HH:mm}";
    }

    private string BuildHistoryFocusedSummary(HistoryPageInsights insights)
    {
        string focusedDuration = FormatReadableDuration(insights.FocusedSeconds);
        return _historyRange switch
        {
            HistoryRange.Last1Hour => $"You focused {focusedDuration} in the last hour",
            HistoryRange.Last4Hours => $"You focused {focusedDuration} in the last 4 hours",
            HistoryRange.Today => $"You focused {focusedDuration} today",
            HistoryRange.AllTime => $"You focused {focusedDuration} in retained history",
            _ => $"You focused {focusedDuration}"
        };
    }

    private string BuildHistoryLeadSentence(
        HistoryPageInsights insights,
        LocalApiTimelineResponse timeline,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
    {
        string rangeLabel = BuildHistoryRangeLabel(timeline, snapshots);
        string focusedDuration = FormatReadableDuration(insights.FocusedSeconds);
        string distractionDuration = FormatReadableDuration(insights.DistractionSeconds);

        if (insights.MostDistractedPeriodLabel != NotAvailableText)
        {
            return $"Across {rangeLabel}, you focused {focusedDuration}, spent {distractionDuration} in distraction episodes, and saw the most distraction in the {insights.MostDistractedPeriodLabel}.";
        }

        return $"Across {rangeLabel}, you focused {focusedDuration} and spent {distractionDuration} in distraction episodes.";
    }

    private string BuildHistoryEmptyLeadSentence()
    {
        return _historyRange switch
        {
            HistoryRange.Last1Hour => "No tracked focus episodes landed in the last hour yet. The hour map stays pinned here as new episodes arrive.",
            HistoryRange.Last4Hours => "No tracked focus episodes landed in the last 4 hours yet. The hour map stays pinned here as new episodes arrive.",
            HistoryRange.Today => "No tracked focus episodes from today are available yet. The hour map stays pinned here as new episodes arrive.",
            HistoryRange.AllTime => "No retained focus history is available yet. The hour map stays pinned here as new stored days arrive.",
            _ => "No tracked focus episodes are available in this range yet."
        };
    }

    private string BuildHistoryEmptyChartText()
    {
        return _historyRange switch
        {
            HistoryRange.Last1Hour => "No tracked focus episodes landed in the last hour yet.",
            HistoryRange.Last4Hours => "No tracked focus episodes landed in the last 4 hours yet.",
            HistoryRange.Today => "No tracked focus episodes from today are available yet.",
            HistoryRange.AllTime => "No retained focus history is available yet.",
            _ => "No tracked focus episodes are available in this range yet."
        };
    }

    private static string BuildHistoryActionsSummary(HistoryPageInsights insights)
    {
        if (insights.ActionMarkerCount == 0)
        {
            return "No action markers in this range yet.";
        }

        string markerLabel = insights.ActionMarkerCount == 1 ? "action marker" : "action markers";
        string suggestionLabel = insights.SuggestionCount == 1 ? "suggestion" : "suggestions";
        string changeLabel = insights.ChangeCount == 1 ? "change" : "changes";
        return $"{insights.ActionMarkerCount} {markerLabel} in this range, including {insights.SuggestionCount} {suggestionLabel} and {insights.ChangeCount} {changeLabel}.";
    }

    private string BuildHistorySliceTooltip(LocalApiCurrentSnapshotResponse snapshot, double focusPercent)
    {
        string time = snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (_historyExplainMode)
        {
            return $"{time}{Environment.NewLine}focus_norm={focusPercent:F1}%{Environment.NewLine}pressure={(ResolvePressureLevel(snapshot) * 100d):F1}%{Environment.NewLine}trust={(snapshot.Confidence * 100d):F1}%{Environment.NewLine}focus_reason={LocalAttentionSegmentAnalytics.ResolveFocusEpisodeReasonText(snapshot)}{Environment.NewLine}attention_reason={LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(snapshot)}";
        }

        return $"{time}{Environment.NewLine}Focus episode {FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot))}{Environment.NewLine}Pressure {(ResolvePressureLevel(snapshot) * 100d):F0}%{Environment.NewLine}Confidence {(snapshot.Confidence * 100d):F0}%{Environment.NewLine}Focus {LocalAttentionSegmentAnalytics.ResolveFocusEpisodeReasonText(snapshot)}{Environment.NewLine}Attention {LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(snapshot)}";
    }

    private UIElement BuildTrackPoint(
        LocalApiCurrentSnapshotResponse snapshot,
        double value,
        Color color,
        string label)
    {
        double clamped = Math.Clamp(value, 0d, 1d);

        var button = new Button
        {
            Width = 9,
            Height = Math.Max(8d, 64d * clamped),
            Margin = new Thickness(1.5, 0, 1.5, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Tag = snapshot,
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        };
        button.ToolTip = BuildTimelinePointTooltip(snapshot, label, clamped);
        button.Click += TimelineSnapshotButton_Click;
        return button;
    }

    private UIElement BuildOverloadActiveMarker(LocalApiCurrentSnapshotResponse snapshot)
    {
        bool active = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
        double pressure = ResolvePressureLevel(snapshot);
        double strength = active
            ? Math.Max(0.55d, pressure)
            : Math.Max(0.20d, pressure * 0.60d);
        byte alpha = (byte)Math.Round((active ? 120d : 60d) + (strength * (active ? 110d : 90d)));

        var button = new Button
        {
            Width = 9,
            Height = Math.Max(8d, 22d * strength),
            Margin = new Thickness(1.5, 0, 1.5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = snapshot,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(alpha, 220, 38, 38))
                : new SolidColorBrush(Color.FromArgb(alpha, 100, 116, 139)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
            BorderThickness = new Thickness(0.25)
        };
        button.ToolTip = _historyExplainMode
            ? (active
                ? $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | overload_active=1"
                : $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | overload_active=0")
            : (active
                ? $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | Overload was active at this moment."
                : $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | Overload was not active at this moment.");
        button.Click += TimelineSnapshotButton_Click;
        return button;
    }

    private UIElement BuildInterventionMarker(LocalApiEventResponse intervention)
    {
        string marker = intervention.Action.Contains("break", StringComparison.OrdinalIgnoreCase) ? "B" : "A";
        var button = new Button
        {
            Content = marker,
            Width = intervention.DurationSeconds.HasValue
                ? Math.Clamp(intervention.DurationSeconds.Value / 10d, 18d, 48d)
                : 20d,
            Height = 26,
            Margin = new Thickness(3, 0, 3, 0),
            Tag = intervention,
            Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.Bold,
            FontSize = 12
        };
        button.ToolTip = _historyExplainMode
            ? $"{intervention.TimestampUtc.ToLocalTime():HH:mm:ss} | {intervention.Action} | {BuildExecutionStateBadge(intervention)}"
            : $"{intervention.TimestampUtc.ToLocalTime():HH:mm:ss} | {ToActionTitle(intervention)} | {BuildExecutionStateBadge(intervention)}";
        button.Click += TimelineInterventionButton_Click;
        return button;
    }

    private string BuildTimelinePointTooltip(LocalApiCurrentSnapshotResponse snapshot, string label, double normalizedValue)
    {
        string time = snapshot.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (_historyExplainMode)
        {
            return $"{time} | {label}: {normalizedValue:F2}";
        }

        return label switch
        {
            "Pressure" => $"{time} | Pressure was {(ResolvePressureLevel(snapshot) * 100d):F0}%.",
            "Confidence" => $"{time} | Confidence was {(Math.Clamp(snapshot.Confidence, 0d, 1d) * 100d):F0}%.",
            "Focus episode" => $"{time} | Focus episode was {FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot))}.",
            _ => $"{time} | Value: {normalizedValue:F2}"
        };
    }

    private void TimelineSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiCurrentSnapshotResponse snapshot)
        {
            _selectedHistoryInterventionId = null;
            _selectedHistoryCellKey = null;
            _selectedHistorySnapshotTimestampUtc = snapshot.TimestampUtc;
            SetTimelineDetail(snapshot, null);
        }
    }

    private void TimelineInterventionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiEventResponse intervention)
        {
            _selectedHistoryCellKey = null;
            _selectedHistorySnapshotTimestampUtc = null;
            _selectedHistoryInterventionId = intervention.Id;
            SetTimelineDetail(null, intervention);
        }
    }

    private void SetTimelineDetail(LocalApiCurrentSnapshotResponse? snapshot, LocalApiEventResponse? intervention)
    {
        if (snapshot == null && intervention == null)
        {
            TimelineDetailTimestamp.Text = _historyExplainMode
                ? $"Timestamp: {NotAvailableText}"
                : "Hour: Select a heatmap cell, snapshot, or action marker to inspect a block of time.";
            TimelineDetailOverload.Text = _historyExplainMode
                ? $"Pressure: {NotAvailableText}"
                : "Tracked time in that hour: This will appear after you select a heatmap cell or snapshot.";
            TimelineDetailTrust.Text = _historyExplainMode
                ? $"Confidence: {NotAvailableText}"
                : "Confidence in that hour: This will appear after you select a heatmap cell or snapshot.";
            TimelineDetailFocus.Text = _historyExplainMode
                ? $"Focus episode: {NotAvailableText}"
                : "Focus episode in that hour: This will appear after you select a heatmap cell or snapshot.";
            TimelineDetailClassification.Text = _historyExplainMode
                ? "Moment type: Select a cell, snapshot, or marker to inspect raw details."
                : "What happened here: Select a heatmap hour, snapshot, or marker to see what Ari understood about it.";
            TimelineDetailExecution.Text = _historyExplainMode
                ? "Decision / execution: Select an action marker to inspect the exact recommendation or change."
                : "Action around this hour: Select an action marker to see what Ari suggested or changed.";
            TimelineDetailWhyHeader.Text = _historyExplainMode ? "Why? (Detailed)" : "What Ari saw";
            TimelineDetailWhyFiv.Text = _historyExplainMode
                ? "Clarity/Pressure/Jitter: Select a cell or snapshot to inspect the raw model breakdown."
                : "Focus / pressure / movement: Ari will explain this after you select an hour.";
            TimelineDetailWhyReasons.Text = _historyExplainMode
                ? "Top reasons: Select a cell or snapshot to inspect the strongest contributors."
                : "Main signals: Ari will list the strongest signals after you select an hour.";
            TimelineDetailWhyQuality.Text = _historyExplainMode
                ? "Quality: Select a cell or snapshot to inspect model quality and agreement."
                : "Tracking quality: This will appear with the selected hour.";
            TimelineDetailStabilization.Text = _historyExplainMode
                ? "Stabilization: Select a cell or snapshot to inspect stabilization flags."
                : "Context: Use this panel to understand what Ari saw around the selected hour.";
            PopulateTimelineEpisodeDrilldown([], "this block");
            return;
        }

        if (snapshot != null)
        {
            OverloadExplainView explain = ParseOverloadExplain(snapshot.ContributorsJson);
            bool overloadActive = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
            bool hasContributorsPayload = !string.IsNullOrWhiteSpace(snapshot.ContributorsJson)
                && explain.HasStructuredData
                && explain.Clarity.HasValue
                && explain.Pressure.HasValue
                && explain.Jitter.HasValue;
            TimelineDetailTimestamp.Text = _historyExplainMode
                ? $"Timestamp: {snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : $"Time: {snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            TimelineDetailOverload.Text = _historyExplainMode
                ? $"Pressure: {(ResolvePressureLevel(snapshot) * 100d):F0}% | Overload active: {(overloadActive ? "Yes" : "No")}"
                : $"Pressure at this moment: {(ResolvePressureLevel(snapshot) * 100d):F0}%";
            TimelineDetailTrust.Text = _historyExplainMode
                ? $"Confidence: {(snapshot.Confidence * 100d):F0}%"
                : $"Confidence in that moment: {(snapshot.Confidence * 100d):F0}%";
            TimelineDetailFocus.Text = _historyExplainMode
                ? $"Focus episode: {FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot))}"
                : $"Focus episode in that moment: {FormatFocusedTime(ResolveFocusEpisodeSeconds(snapshot))}";
            TimelineDetailClassification.Text = _historyExplainMode
                ? "Moment type: Activity snapshot"
                : BuildHistoryMomentSummary(snapshot, overloadActive);
            TimelineDetailExecution.Text = _historyExplainMode
                ? "Decision / execution: No direct action at this exact moment. Click an action marker to inspect a suggestion or change."
                : "Action around this moment: No direct action landed at this exact point. Click an action marker to inspect a nearby suggestion or change.";
            TimelineDetailWhyHeader.Text = _historyExplainMode
                ? "Why? (Detailed)"
                : "What Ari saw";
            TimelineDetailWhyFiv.Text = hasContributorsPayload
                ? (_historyExplainMode
                    ? $"F (Clarity): {explain.Clarity!.Value:F3}{Environment.NewLine}I (Pressure): {explain.Pressure!.Value:F3}{Environment.NewLine}V (Jitter): {explain.Jitter!.Value:F3}"
                    : BuildHumanFivSummary(explain))
                : (_historyExplainMode
                    ? "No detailed explanation is available for this moment."
                    : "Ari did not have a detailed breakdown for this moment.");
            TimelineDetailWhyReasons.Text = _historyExplainMode
                ? $"Top reasons: {FormatTopReasons(explain.TopReasons, _historyExplainMode)}"
                : $"Main signals: {FormatTopReasons(explain.TopReasons, _historyExplainMode)}";
            TimelineDetailWhyQuality.Text = hasContributorsPayload
                ? (_historyExplainMode
                    ? $"Quality: {explain.Quality} | Agreement: {explain.Agreement}"
                    : $"Tracking quality: {explain.Quality} | Signal agreement: {explain.Agreement}")
                : (_historyExplainMode
                    ? $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}"
                    : $"Tracking quality: {NotAvailableText}");
            TimelineDetailStabilization.Text = _historyExplainMode
                ? BuildStabilizationText(snapshot.StateFlagsJson, _historyExplainMode)
                : BuildHistoryContextText(snapshot.StateFlagsJson);
            PopulateTimelineEpisodeDrilldown(FindEpisodesAt(snapshot.TimestampUtc), $"{snapshot.TimestampUtc.ToLocalTime():MMM d HH:mm:ss}");
            return;
        }

        TimelineDetailTimestamp.Text = _historyExplainMode
            ? $"Timestamp: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"Time: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TimelineDetailOverload.Text = _historyExplainMode
            ? $"Action trigger: {intervention.TriggerState}={intervention.TriggerValue:F2}"
            : $"What triggered this: {HumanizeHistoryTrigger(intervention.TriggerState, intervention.TriggerValue)}";
        TimelineDetailTrust.Text = _historyExplainMode
            ? $"Confidence: {(intervention.Confidence * 100d):F0}%"
            : $"Confidence in that moment: {(intervention.Confidence * 100d):F0}%";
        TimelineDetailFocus.Text = _historyExplainMode
            ? $"Focus episode: {NotAvailableText} (action marker)"
            : $"Focus episode in that moment: {NotAvailableText} (action marker)";
        TimelineDetailClassification.Text = _historyExplainMode
            ? $"Moment type: {BuildActionClassificationText(intervention)}"
            : $"What happened here: {BuildActionClassificationText(intervention)}";
        TimelineDetailExecution.Text = _historyExplainMode
            ? $"{BuildDecisionStateText(intervention)}{Environment.NewLine}{BuildExecutionStateText(intervention)}"
            : $"Action around this moment: {BuildExecutionStateText(intervention)}";
        TimelineDetailWhyHeader.Text = _historyExplainMode
            ? "Why? (Detailed)"
            : "What Ari saw";
        TimelineDetailWhyFiv.Text = string.IsNullOrWhiteSpace(intervention.Reason)
            ? (_historyExplainMode ? $"Reason: {NotAvailableText}" : "Focus / pressure / movement: Not available")
            : (_historyExplainMode ? $"Reason: {intervention.Reason}" : $"Focus / pressure / movement: {intervention.Reason}");
        TimelineDetailWhyReasons.Text = intervention.Contributors.Count > 0
            ? (_historyExplainMode
                ? $"Top reasons: {FormatTopReasons(string.Join(", ", intervention.Contributors.Take(6)), _historyExplainMode)}"
                : $"Main signals: {FormatTopReasons(string.Join(", ", intervention.Contributors.Take(6)), _historyExplainMode)}")
            : (_historyExplainMode
                ? $"Top reasons: {NotAvailableText}"
                : $"Main signals: {NotAvailableText}");
        TimelineDetailWhyQuality.Text = _historyExplainMode
            ? $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}"
            : $"Tracking quality: {NotAvailableText}";
        TimelineDetailStabilization.Text = _historyExplainMode
            ? BuildStabilizationText(intervention.StateFlagsJson, _historyExplainMode)
            : BuildHistoryContextText(intervention.StateFlagsJson);
        PopulateTimelineEpisodeDrilldown(FindEpisodesAt(intervention.TimestampUtc), $"{intervention.TimestampUtc.ToLocalTime():MMM d HH:mm:ss}");
    }

    private static string BuildHistoryMomentSummary(LocalApiCurrentSnapshotResponse snapshot, bool overloadActive)
    {
        double focus = Math.Clamp(snapshot.FocusStrength, 0d, 1d);
        double pressure = ResolvePressureLevel(snapshot);
        if (overloadActive || pressure >= 0.70d)
        {
            return "What happened here: Pressure was elevated and Ari saw a higher overload risk.";
        }

        if (focus >= 0.75d && pressure <= 0.35d)
        {
            return "What happened here: Focus looked steady and this moment stayed relatively calm.";
        }

        if (focus <= 0.35d)
        {
            return "What happened here: Focus looked scattered and Ari saw more distraction in this moment.";
        }

        return "What happened here: Ari saw a mixed moment with some focus and some distraction.";
    }

    private static string HumanizeHistoryTrigger(string? triggerState, double triggerValue)
    {
        string label = string.IsNullOrWhiteSpace(triggerState)
            ? "a tracked state"
            : triggerState.Trim().Replace('_', ' ');
        return $"{label} reached {triggerValue:F2}";
    }

    private static string BuildHistoryContextText(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return "Context: Ari smooths brief spikes before changing its view of the moment.";
        }

        return "Context: Ari was smoothing this moment before locking in a state change.";
    }

    private static string BuildStabilizationText(string? stateFlagsJson, bool explainMode)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return explainMode
                ? "Stabilization: detailed flags missing"
                : $"Stabilization: ON timer {NotAvailableText} / 120s";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            double dwellOnSeconds = 0d;
            long dwellOnMs = 0;
            long dwellOffMs = 0;
            double gapSeconds = 0d;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("dwell_on_ms", out JsonElement dwellOnElem)
                && dwellOnElem.ValueKind == JsonValueKind.Number
                && dwellOnElem.TryGetDouble(out double parsedDwellOnMs))
            {
                dwellOnSeconds = Math.Max(0d, parsedDwellOnMs / 1000d);
                dwellOnMs = (long)Math.Max(0d, parsedDwellOnMs);
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("dwell_off_ms", out JsonElement dwellOffElem)
                && dwellOffElem.ValueKind == JsonValueKind.Number
                && dwellOffElem.TryGetInt64(out long parsedDwellOffMs))
            {
                dwellOffMs = Math.Max(0, parsedDwellOffMs);
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("gap_s", out JsonElement gapElem)
                && gapElem.ValueKind == JsonValueKind.Number
                && gapElem.TryGetDouble(out double parsedGap))
            {
                gapSeconds = Math.Max(0d, parsedGap);
            }

            bool active = false;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("overload_active", out JsonElement activeElem))
            {
                if (activeElem.ValueKind == JsonValueKind.True)
                {
                    active = true;
                }
                else if (activeElem.ValueKind == JsonValueKind.Number
                    && activeElem.TryGetInt32(out int activeInt))
                {
                    active = activeInt != 0;
                }
            }

            if (explainMode)
            {
                return $"Stabilization:{Environment.NewLine}overload_active={(active ? 1 : 0)}{Environment.NewLine}dwell_on_ms={dwellOnMs}{Environment.NewLine}dwell_off_ms={dwellOffMs}{Environment.NewLine}gap_s={gapSeconds:F1}";
            }

            return $"Stabilization: ON timer {dwellOnSeconds:F0}s / 120s{(active ? " (active)" : string.Empty)}";
        }
        catch
        {
            return explainMode
                ? "Stabilization: detailed flags unavailable"
                : $"Stabilization: ON timer {NotAvailableText} / 120s";
        }
    }

    private static string FormatPreviousValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NotAvailableText;
        }

        string compact = raw.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return compact.Length <= 180 ? compact : $"{compact[..180]}...";
    }

    private void SelectPrivacyLevel(string privacyLevel)
    {
        string target = string.IsNullOrWhiteSpace(privacyLevel) ? "Standard" : privacyLevel;
        foreach (object item in PrivacyLevelComboBox.Items)
        {
            if (item is ComboBoxItem combo
                && string.Equals(combo.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                PrivacyLevelComboBox.SelectedItem = combo;
                return;
            }
        }

        PrivacyLevelComboBox.SelectedIndex = 1;
    }

    private string GetSelectedPrivacyLevel()
    {
        if (PrivacyLevelComboBox.SelectedItem is ComboBoxItem combo
            && !string.IsNullOrWhiteSpace(combo.Content?.ToString()))
        {
            return combo.Content!.ToString()!;
        }

        return "Standard";
    }

    private static string FormatMetric(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("F2", CultureInfo.InvariantCulture)
            : NotAvailableText;
    }

    private void UpdateEngineHealthCards(
        ServiceProbeResult serviceProbe,
        bool apiReachable,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh,
        int? statusHttpCode)
    {
        EngineHealthView view = BuildEngineHealthView(serviceProbe, apiReachable, _latestStatus, snapshot, statusFresh, snapshotFresh, statusHttpCode);
        var statusBrush = new SolidColorBrush(view.StatusColor);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        HealthEngineStatusValue.Text = view.StatusLabel;
        HealthEngineStatusValue.Foreground = statusBrush;
        HealthServiceValue.Text = view.ServiceLabel;
        HealthDatabaseValue.Text = view.DatabaseLabel;
        HealthSnapshotValue.Text = view.SnapshotLabel;
        HealthPolicyStateValue.Text = view.PolicyLabel;
        HealthApiValue.Text = view.ApiLabel;
        HealthLastUpdateValue.Text = view.LastUpdateLabel;
        HealthLastErrorValue.Text = view.LastErrorLabel;
        HealthLastErrorValue.Foreground = detailBrush;
        ApplyStatusBadge(HealthEngineBadge, HealthEngineBadgeText, HealthEngineDot, view.StatusLabel);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = ["KB", "MB", "GB", "TB"];
        double size = bytes / 1024d;
        int unit = 0;
        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    private static ServiceProbeResult ProbeServiceStatus()
    {
        try
        {
            using var service = new ServiceController(AgentServiceName);
            ServiceControllerStatus status = service.Status;
            return new ServiceProbeResult(
                Exists: true,
                Running: status == ServiceControllerStatus.Running,
                StatusText: status.ToString(),
                ErrorText: null);
        }
        catch (InvalidOperationException ex)
        {
            return new ServiceProbeResult(
                Exists: false,
                Running: false,
                StatusText: "NotInstalled",
                ErrorText: ex.Message);
        }
        catch (Exception ex)
        {
            return new ServiceProbeResult(
                Exists: true,
                Running: false,
                StatusText: "Unknown",
                ErrorText: ex.Message);
        }
    }

    private static RestartResult RestartServiceInternal()
    {
        try
        {
            using var service = new ServiceController(AgentServiceName);
            _ = service.Status;
            service.Refresh();

            if (service.Status != ServiceControllerStatus.Stopped
                && service.Status != ServiceControllerStatus.StopPending)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return new RestartResult(true, "AriAgent service restarted successfully.");
        }
        catch (Exception ex)
        {
            return new RestartResult(
                false,
                "Failed to restart AriAgent service. Run Control Center as Administrator and try again.\n\n"
                + $"Details: {ex.Message}");
        }
    }

    private void UpdateTopBar(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        LocalApiStatusResponse? status,
        NowTruth nowTruth,
        PortaVisualState visualState)
    {
        string runState = status == null
            ? (serviceProbe.Running ? "Running" : "Paused")
            : (status.Paused ? "Paused" : "Running");
        StatusPillText.Text = runState;
        SetBadgeVisual(StatusPillBorder, isPositive: runState == "Running");
        ApplyModeTheme(visualState);

        bool consentUnavailable = status == null || !status.LocalApiAuthReady;
        if (consentUnavailable)
        {
            ConsentBadgeText.Text = "Consent: Unavailable";
            SetNeutralBadgeVisual(ConsentBadgeBorder);
        }
        else if (status is LocalApiStatusResponse statusValue)
        {
            bool consentOn = statusValue.ConsentActive;
            ConsentBadgeText.Text = $"Consent: {(consentOn ? "On" : "Needed")}";
            SetBadgeVisual(ConsentBadgeBorder, isPositive: consentOn);
        }

        bool calibrationUnavailable = nowTruth.Snapshot == null;
        if (calibrationUnavailable)
        {
            CalibrationBadgeText.Text = "Learning your pattern: Unavailable";
            SetNeutralBadgeVisual(CalibrationBadgeBorder);
        }
        else
        {
            LocalApiCurrentSnapshotResponse snapshotValue = nowTruth.Snapshot!;
            string calibrationStage = NormalizeCalibrationStage(
                snapshotValue.CalibrationStage ?? status?.CalibrationStage,
                snapshotValue.BaselineReady,
                snapshotValue.ColdStart);
            // Cold-start (Launch 2.5): while still learning, show an explicit "day N of ~5"
            // countdown instead of a bare stage label, so a new user understands the product is
            // actively calibrating (not inert). Day is derived from baseline-metrics readiness
            // mapped onto the ~5-day calibration window.
            if (string.Equals(calibrationStage, CalibrationStageCalibrated, StringComparison.Ordinal))
            {
                CalibrationBadgeText.Text = "Learning your pattern: Calibrated";
            }
            else
            {
                int calibrationDay = snapshotValue.BaselineReadyPct.HasValue
                    ? Math.Clamp((int)Math.Ceiling(Math.Clamp(snapshotValue.BaselineReadyPct.Value, 0d, 1d) * 5d), 1, 5)
                    : 1;
                CalibrationBadgeText.Text = $"Learning your patterns — day {calibrationDay} of ~5";
            }
            SetCalibrationBadgeVisual(calibrationStage);
        }

        string confidenceLabel = visualState.ConfidenceLabel;
        TrustBadgeText.Text = $"Confidence: {confidenceLabel}";
        SetTrustBadgeVisual(confidenceLabel);
        UpdateHeaderStatusChip(runState, confidenceLabel);
        SectionTitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
        if (_activeSection == UiSection.Overview)
        {
            SectionTitleText.Text = "Today";
        }

        AgentVersionText.Text = status?.AgentVersion ?? NotAvailableText;
        UiVersionText.Text = _uiVersion;
        SchemaVersionText.Text = status?.SchemaVersion ?? NotAvailableText;
    }

    private static void SetBadgeVisual(Border border, bool isPositive)
    {
        border.Background = isPositive
            ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
            : new SolidColorBrush(Color.FromRgb(127, 29, 29));
    }

    private static void SetNeutralBadgeVisual(Border border)
    {
        border.Background = new SolidColorBrush(Color.FromRgb(51, 65, 85));
    }

    private void ApplyModeTheme(PortaVisualState visualState)
    {
        PortaModeTheme theme = visualState.Theme;
        var accentBrush = new SolidColorBrush(theme.AccentColor);
        var badgeBrush = new SolidColorBrush(theme.BadgeColor);
        var statusChipBrush = new SolidColorBrush(theme.StatusChipColor);
        var tintBrush = new SolidColorBrush(theme.BackgroundTint);

        StatusPillBorder.BorderBrush = badgeBrush;
        StatusPillBorder.BorderThickness = new Thickness(1);
        if (StatusPillText.Text == "Running")
        {
            StatusPillBorder.Background = statusChipBrush;
        }

        ConsentBadgeBorder.BorderBrush = badgeBrush;
        ConsentBadgeBorder.BorderThickness = new Thickness(1);
        CalibrationBadgeBorder.BorderBrush = badgeBrush;
        CalibrationBadgeBorder.BorderThickness = new Thickness(1);
        TrustBadgeBorder.BorderBrush = badgeBrush;
        TrustBadgeBorder.BorderThickness = new Thickness(1);

        NowRecoveryBannerBorder.BorderBrush = badgeBrush;
        NowRecoveryBannerText.Foreground = accentBrush;
        NowDeepWorkStreakText.Foreground = accentBrush;
        OverviewPanel.Background = new SolidColorBrush(Colors.Transparent);
        WhyDrawerPanel.BorderBrush = badgeBrush;
        WhyDrawerToggleButton.BorderBrush = badgeBrush;
    }

    private void SetTrustBadgeVisual(string trustLabel)
    {
        TrustBadgeBorder.Background = trustLabel switch
        {
            "High" => new SolidColorBrush(Color.FromRgb(6, 95, 70)),
            "Medium" => new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            NotAvailableText => new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            _ => new SolidColorBrush(Color.FromRgb(127, 29, 29))
        };
    }

    private void SetCalibrationBadgeVisual(string calibrationStage)
    {
        CalibrationBadgeBorder.Background = calibrationStage switch
        {
            CalibrationStageCalibrated => new SolidColorBrush(Color.FromRgb(6, 95, 70)),
            CalibrationStageProvisional => new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            _ => new SolidColorBrush(Color.FromRgb(51, 65, 85))
        };
    }

    private static string NormalizeCalibrationStage(string? calibrationStage, bool? baselineReady, bool? coldStart)
    {
        if (!string.IsNullOrWhiteSpace(calibrationStage))
        {
            return calibrationStage.Trim().ToLowerInvariant() switch
            {
                CalibrationStageCalibrated => CalibrationStageCalibrated,
                CalibrationStageProvisional => CalibrationStageProvisional,
                _ => CalibrationStageCold
            };
        }

        if (baselineReady == true)
        {
            return CalibrationStageCalibrated;
        }

        return coldStart == true ? CalibrationStageCold : CalibrationStageProvisional;
    }

    private static string NormalizeTrustTier(string? trustTier, double? confidence)
    {
        if (!string.IsNullOrWhiteSpace(trustTier))
        {
            return trustTier.Trim().ToLowerInvariant() switch
            {
                TrustTierHigh => TrustTierHigh,
                TrustTierMedium => TrustTierMedium,
                _ => TrustTierLow
            };
        }

        if (!confidence.HasValue)
        {
            return TrustTierLow;
        }

        if (confidence.Value >= 0.75d)
        {
            return TrustTierHigh;
        }

        return confidence.Value >= 0.50d ? TrustTierMedium : TrustTierLow;
    }

    private static string ToConfidenceLabel(string trustTier)
    {
        return NormalizeTrustTier(trustTier, null) switch
        {
            TrustTierHigh => "High",
            TrustTierMedium => "Medium",
            _ => "Low"
        };
    }

    private static string ToCalibrationStageLabel(string calibrationStage)
    {
        return NormalizeCalibrationStage(calibrationStage, null, null) switch
        {
            CalibrationStageCalibrated => "Ready",
            CalibrationStageProvisional => "Provisional",
            _ => "Bootstrapping"
        };
    }

    private static string ToCalibrationStageSentence(string calibrationStage)
    {
        return NormalizeCalibrationStage(calibrationStage, null, null) switch
        {
            CalibrationStageCalibrated => "fully calibrated",
            CalibrationStageProvisional => "provisional calibration",
            _ => "bootstrapping calibration"
        };
    }

    private static string? BuildTopBarHint(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        int? statusHttpCode,
        bool apiReachable)
    {
        if (status == null)
        {
            return BuildLocalApiConnectionExplanation(serviceProbe, runtimeSettings, statusHttpCode, apiReachable);
        }

        if (!status.LocalApiAuthReady)
        {
            return "API auth mismatch";
        }

        if (!status.ServiceRunning)
        {
            return "Service offline";
        }

        if (!statusFresh)
        {
            return BuildFreshnessExplanation(status, snapshot);
        }

        if (!status.ConsentActive)
        {
            return "Not collecting: Consent needed";
        }

        if (IsMigrationFailure(status.LastError))
        {
            return "Not updating: Database migration failed";
        }

        string? captureHint = BuildCaptureContextHint(status);
        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return captureHint;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
            {
                return match;
            }

            child = GetParentObject(child);
        }

        return null;
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, ancestor))
            {
                return true;
            }

            child = GetParentObject(child);
        }

        return false;
    }

    private static DependencyObject? GetParentObject(DependencyObject? child)
    {
        if (child == null)
        {
            return null;
        }

        if (child is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement)
                ?? (contentElement as FrameworkContentElement)?.Parent
                ?? (contentElement as FrameworkContentElement)?.TemplatedParent;
        }

        if (child is FrameworkElement frameworkElement && frameworkElement.Parent != null)
        {
            return frameworkElement.Parent;
        }

        return child is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);
    }

    private static NowTruth BuildNowTruth(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh,
        bool snapshotFresh,
        int? statusHttpCode,
        bool apiReachable)
    {
        if (status == null)
        {
            if (snapshot != null)
            {
                return new NowTruth(
                    Snapshot: snapshot,
                    Explanation: snapshotFresh
                        ? "Service alive, using current snapshot while status refresh retries."
                        : BuildFreshnessExplanation(status, snapshot));
            }

            return new NowTruth(
                Snapshot: null,
                Explanation: BuildLocalApiConnectionExplanation(serviceProbe, runtimeSettings, statusHttpCode, apiReachable));
        }

        if (!status.LocalApiAuthReady)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: "API auth mismatch");
        }

        if (!status.ServiceRunning)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: "Service offline");
        }

        if (!statusFresh)
        {
            return new NowTruth(
                Snapshot: snapshot,
                Explanation: BuildFreshnessExplanation(status, snapshot));
        }

        if (snapshot == null)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: "Service alive, waiting for current snapshot.");
        }

        if (!snapshotFresh)
        {
            return new NowTruth(
                Snapshot: snapshot,
                Explanation: BuildFreshnessExplanation(status, snapshot));
        }

        string? captureHint = BuildCaptureContextHint(status);
        return new NowTruth(
            Snapshot: snapshot,
            Explanation: captureHint ?? string.Empty);
    }

    private static bool IsSnapshotStale(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        DateTimeOffset nowUtc)
    {
        _ = nowUtc;
        if (snapshot == null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.freshness_state))
        {
            return !IsFreshState(snapshot.freshness_state);
        }

        if (status != null && !string.IsNullOrWhiteSpace(status.freshness_state))
        {
            return !IsFreshState(status.freshness_state);
        }

        return false;
    }

    private static string? BuildCaptureContextHint(LocalApiStatusResponse status)
    {
        string? ownershipDetail = DescribeOwnershipWarning(status);
        if (!string.IsNullOrWhiteSpace(ownershipDetail))
        {
            return $"Ownership mismatch: {ownershipDetail}";
        }

        string? detail = DescribeCaptureFailureReason(status.CaptureFailureReason);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            return $"Not collecting: {detail}";
        }

        string? signalWaitDetail = BuildMissingSignalHint(status);
        return string.IsNullOrWhiteSpace(signalWaitDetail)
            ? null
            : $"Waiting for live capture: {signalWaitDetail}";
    }

    private static string? DescribeCaptureFailureReason(string? captureFailureReason)
    {
        return captureFailureReason switch
        {
            "USER_AGENT_HEARTBEAT_STALE" => "user-agent heartbeat stopped updating",
            "USER_AGENT_SESSION_SWITCHED" => "capture is still attached to a previous Windows session",
            "USER_AGENT_INSTANCE_MISMATCH" => "the current heartbeat owner does not match the last accepted input owner",
            "USER_AGENT_INPUT_SELF_TEST_FAILED" => "capture self-test failed after real user activity",
            "USER_AGENT_KEYBOARD_SIGNAL_MISSING" => "keyboard input is missing from live capture",
            "USER_AGENT_MOUSE_SIGNAL_MISSING" => "mouse input is missing from live capture",
            "CAPTURE_CONTEXT_SERVICE_SESSION" => "capture is running in a service session",
            "CAPTURE_CONTEXT_UNKNOWN" => "user-session capture is not available",
            _ => null
        };
    }

    private static string? DescribeOwnershipWarning(LocalApiStatusResponse? status)
    {
        if (status == null || string.IsNullOrWhiteSpace(status.OwnershipWarning))
        {
            return null;
        }

        return status.OwnershipWarning switch
        {
            "USER_AGENT_HEARTBEAT_MISSING" =>
                $"last input owner {status.LastInputAgentInstanceId ?? "unknown"} ({status.LastInputSessionId ?? "unknown session"}) has no fresh user-agent heartbeat",
            "USER_AGENT_INSTANCE_MISMATCH" =>
                $"heartbeat owner {status.UserAgentInstanceId ?? "unknown"} does not match last input owner {status.LastInputAgentInstanceId ?? "unknown"}",
            "USER_AGENT_SESSION_MISMATCH" =>
                $"heartbeat session {status.UserAgentSessionId ?? "unknown"} does not match last input session {status.LastInputSessionId ?? "unknown"}",
            _ => null
        };
    }

    private static string? BuildMissingSignalHint(LocalApiStatusResponse? status)
    {
        if (status == null
            || !string.Equals(status.CaptureContext, "UserSession", StringComparison.Ordinal))
        {
            return null;
        }

        bool keyboardActive = status.KeyboardSignalActive;
        bool mouseActive = status.MouseSignalActive;

        if (keyboardActive && mouseActive)
        {
            return null;
        }

        string missingSignals = BuildMissingSignalList(keyboardActive, mouseActive);
        return status.LastInputEventUtc is null
            ? $"waiting for first {missingSignals} signal"
            : $"no recent {missingSignals} signal";
    }

    private void UpdateDebugFooter(NowTruth nowTruth, bool statusFresh)
    {
#if DEBUG
        string statusCode = _apiClient.LastStatusHttpCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        string snapshotCode = _apiClient.LastSnapshotHttpCode?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
        string statusAt = _apiClient.LastStatusProbeUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";
        string snapshotAt = _apiClient.LastSnapshotProbeUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";
        string snapshotState = nowTruth.Snapshot != null ? "yes" : "no";
        string freshness = statusFresh ? "fresh" : "stale";
        DebugFooterText.Text =
            $"DEBUG /v1/status={statusCode} @{statusAt} | /v1/snapshot/current={snapshotCode} @{snapshotAt} | status={freshness} | snapshot={snapshotState}";
        DebugFooterText.Visibility = Visibility.Visible;
#else
        DebugFooterText.Visibility = Visibility.Collapsed;
#endif
    }

    private static bool IsMigrationFailure(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText))
        {
            return false;
        }

        return errorText.Contains("db_migration_failed", StringComparison.OrdinalIgnoreCase)
            || errorText.Contains("migration", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFreshState(string? freshnessState)
    {
        return string.Equals(freshnessState, "fresh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStatusFresh(LocalApiStatusResponse? status)
    {
        return status != null && IsFreshState(status.freshness_state);
    }

    private static bool IsSnapshotFresh(LocalApiCurrentSnapshotResponse? snapshot)
    {
        return snapshot != null && IsFreshState(snapshot.freshness_state);
    }

    private static bool IsDbBusyRetrying(LocalApiStatusResponse? status, LocalApiCurrentSnapshotResponse? snapshot)
    {
        return string.Equals(status?.runtime_state_refresh_error, "db_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot?.runtime_state_refresh_error, "db_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status?.degraded_reason, "db_busy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot?.degraded_reason, "db_busy", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFreshnessExplanation(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (status == null)
        {
            return "Local API status unavailable.";
        }

        bool hasSnapshot = snapshot != null;
        bool dbBusy = IsDbBusyRetrying(status, snapshot);

        if (hasSnapshot)
        {
            return dbBusy
                ? "Service alive, using cached snapshot. DB busy, retrying."
                : "Service alive, using cached snapshot.";
        }

        return dbBusy
            ? "Service alive, status stale. DB busy, retrying."
            : "Service alive, status stale.";
    }

    private static string BuildOfflineErrorMessage(
        ServiceProbeResult serviceProbe,
        LocalApiEndpointSettings? endpoint,
        LocalApiRuntimeSettings? runtimeSettings,
        int? statusHttpCode,
        bool apiReachable)
    {
        _ = endpoint;
        return BuildLocalApiConnectionExplanation(serviceProbe, runtimeSettings, statusHttpCode, apiReachable);
    }

    private static string BuildLocalApiConnectionExplanation(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        int? statusHttpCode,
        bool apiReachable)
    {
        if (runtimeSettings is { LocalApiAuthReady: false } || statusHttpCode is 401 or 403)
        {
            return "API auth mismatch";
        }

        if (statusHttpCode == 503)
        {
            return serviceProbe.Running
                ? "Service alive, waiting for local API status."
                : "Service offline";
        }

        if (statusHttpCode.HasValue)
        {
            return $"Service alive, local API returned HTTP {statusHttpCode.Value}.";
        }

        if (apiReachable && serviceProbe.Running)
        {
            return "Service alive, waiting for local API status.";
        }

        return serviceProbe.Running
            ? "Local API unreachable"
            : "Service offline";
    }

    private static string BuildOnlineErrorMessage(
        LocalApiStatusResponse status,
        LocalApiCurrentSnapshotResponse? snapshot,
        ServiceProbeResult serviceProbe,
        bool statusFresh)
    {
        if (!status.ServiceRunning || !serviceProbe.Running)
        {
            return "Service offline";
        }

        if (!status.LocalApiAuthReady)
        {
            return "API auth mismatch";
        }

        if (!statusFresh || (snapshot != null && !IsSnapshotFresh(snapshot)))
        {
            return BuildFreshnessExplanation(status, snapshot);
        }

        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            return status.LastError!;
        }

        return "Service alive, status fresh.";
    }

    private string BuildDiagnosticsSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PORTA Diagnostics Summary");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Engine status: {HealthEngineStatusValue.Text}");
        sb.AppendLine($"Service running: {ServiceRunningValue.Text}");
        sb.AppendLine($"Tray running: {TrayRunningValue.Text}");
        sb.AppendLine($"Consent active: {ConsentActiveValue.Text}");
        sb.AppendLine($"Backend connected: {BackendConnectedValue.Text}");
        sb.AppendLine($"Database: {HealthDatabaseValue.Text}");
        sb.AppendLine($"Snapshot state: {HealthSnapshotValue.Text}");
        sb.AppendLine($"Policy state: {HealthPolicyStateValue.Text}");
        sb.AppendLine($"Last event: {LastEventValue.Text}");
        sb.AppendLine($"Policy version: {PolicyVersionValue.Text}");
        sb.AppendLine($"Schema version: {SchemaVersionValue.Text}");
        sb.AppendLine($"Snapshot confidence: {SnapshotConfidenceValue.Text}");
        sb.AppendLine($"Overload active: {OverloadActiveValue.Text}");
        sb.AppendLine($"Sustained attention (s): {SustainedAttentionValue.Text}");
        sb.AppendLine($"Idle fragmentation index: {IdleFragmentationValue.Text}");
        if (_latestStatus != null)
        {
            sb.AppendLine($"Backend instance: {_latestStatus.BackendInstanceId}");
            sb.AppendLine($"Backend process: {_latestStatus.BackendProcessId}");
            sb.AppendLine($"Capture context: {_latestStatus.CaptureContext}");
            sb.AppendLine($"UserAgent instance: {_latestStatus.UserAgentInstanceId ?? "None"}");
            sb.AppendLine($"UserAgent process: {_latestStatus.UserAgentProcessId?.ToString(CultureInfo.InvariantCulture) ?? "None"}");
            sb.AppendLine($"UserAgent session: {_latestStatus.UserAgentSessionId ?? "None"}");
            sb.AppendLine($"Observed heartbeat: {_latestStatus.UserAgentObservedHeartbeatUpdatedAtUtc?.ToString("O") ?? "None"}");
            sb.AppendLine($"Observed capture context: {_latestStatus.UserAgentObservedCaptureContext ?? "None"}");
            sb.AppendLine($"Observed session: {_latestStatus.UserAgentObservedSessionId ?? "None"}");
            sb.AppendLine($"Startup source: {FormatStartupSourceLabel(_latestStatus.UserAgentStartupSource)}");
            sb.AppendLine($"Last launch reason: {FormatLaunchReasonLabel(_latestStatus.UserAgentLaunchReason)}");
            sb.AppendLine($"Recovery state: {BuildRecoveryStateLabel(_latestStatus)}");
            sb.AppendLine($"Heartbeat write failures: {_latestStatus.UserAgentHeartbeatWriteFailureCount?.ToString(CultureInfo.InvariantCulture) ?? "0"}");
            sb.AppendLine($"Capture self-test state: {_latestStatus.CaptureSelfTestState ?? "None"}");
            sb.AppendLine($"Capture self-test error: {_latestStatus.CaptureSelfTestErrorCode ?? "None"}");
            sb.AppendLine($"Capture self-test remediation count: {_latestStatus.CaptureSelfTestRemediationCount?.ToString(CultureInfo.InvariantCulture) ?? "0"}");
            sb.AppendLine($"Capture failure: {DescribeCaptureFailureReason(_latestStatus.CaptureFailureReason) ?? "None"}");
            sb.AppendLine($"Keyboard signal: {( _latestStatus.KeyboardSignalActive ? "Active" : "No recent signal")}");
            sb.AppendLine($"Mouse signal: {( _latestStatus.MouseSignalActive ? "Active" : "No recent signal")}");
            sb.AppendLine($"Last input: {_latestStatus.LastInputEventUtc?.ToString("O") ?? "None"}");
            sb.AppendLine($"Last accepted input age (s): {_latestStatus.last_accepted_input_age_seconds?.ToString("F1", CultureInfo.InvariantCulture) ?? "None"}");
            sb.AppendLine($"Last input owner: {_latestStatus.LastInputAgentInstanceId ?? "None"}");
            sb.AppendLine($"Last input session: {_latestStatus.LastInputSessionId ?? "None"}");
            sb.AppendLine($"Ownership warning: {DescribeOwnershipWarning(_latestStatus) ?? "None"}");
        }
        sb.AppendLine($"Agent version: {AgentVersionValue.Text}");
        sb.AppendLine($"Local API reachable: {ApiReachableValue.Text}");
        sb.AppendLine($"Local API port: {ApiPortValue.Text}");
        sb.AppendLine($"Last error: {LastErrorValue.Text}");
        sb.AppendLine($"Status pill: {StatusPillText.Text}");
        sb.AppendLine($"Updated: {UpdatedValue.Text}");
        return sb.ToString();
    }

    private sealed record ServiceProbeResult(bool Exists, bool Running, string StatusText, string? ErrorText);
    private sealed record RestartResult(bool Success, string Message);
    private sealed record EngineHealthView(
        string StatusLabel,
        string ServiceLabel,
        string DatabaseLabel,
        string SnapshotLabel,
        string PolicyLabel,
        string ApiLabel,
        string LastUpdateLabel,
        string LastErrorLabel,
        string WarningText,
        Color StatusColor,
        Color DetailColor);
    private sealed record CaptureHealthView(
        string StatusLabel,
        string ContextLabel,
        string KeyboardSignalLabel,
        string MouseSignalLabel,
        string LastInputLabel,
        string WarningText,
        Color StatusColor,
        Color DetailColor);
    private sealed record SessionTruthView(
        string StatusLabel,
        string ContextLabel,
        string HeartbeatLabel,
        string ObservedHeartbeatLabel,
        string AgentLabel,
        string SessionLabel,
        string StartupSourceLabel,
        string LaunchReasonLabel,
        string RecoveryStateLabel,
        string WarningText,
        Color StatusColor,
        Color DetailColor);
    private sealed record InputTruthView(
        string StatusLabel,
        string KeyboardSignalLabel,
        string MouseSignalLabel,
        string LastInputLabel,
        string LastInputAgeLabel,
        string InputOwnerLabel,
        string OwnershipReasonLabel,
        string WarningText,
        Color StatusColor,
        Color DetailColor);
    private sealed record StateTruthView(
        string ContinuityLabel,
        string ConfidenceLabel,
        string CalibrationLabel,
        string BreakReadinessLabel,
        string ModesLabel,
        string WarningText,
        Color DetailColor);
    private sealed record ActionTruthView(
        string TitleLabel,
        string TypeLabel,
        string DecisionLabel,
        string ExecutionLabel,
        string WhenLabel,
        string WarningText,
        Color DetailColor);
    private sealed record NowTruth(
        // Source: GET /v1/snapshot/current -> full snapshot payload (null if service/auth/freshness checks fail)
        LocalApiCurrentSnapshotResponse? Snapshot,
        // Source: computed from GET /v1/status + runtime/service checks
        string Explanation);
    private sealed record OverloadExplainView(
        string FivSummary,
        string TopReasons,
        string Quality,
        string Agreement,
        double? Clarity,
        double? Pressure,
        double? Jitter,
        bool HasStructuredData)
    {
        public static OverloadExplainView Unavailable =>
            new(
                "Explain not available (backend field missing).",
                "Explain not available (backend field missing).",
                NotAvailableText,
                NotAvailableText,
                null,
                null,
                null,
                false);
    }

    private sealed record SuggestionRow(
        string Title,
        string Time,
        string Why,
        LocalApiEventResponse Source);

    private sealed record ChangeRow(
        string WhatChanged,
        string When,
        string Why,
        LocalApiEventResponse Source);

    private record AlertRow(string Title, string When, string Reason, LocalApiEventResponse Source);
}