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
using Porta.Agent.Common;
using Porta.Agent.LocalApi;
using Porta.ControlCenter.Identity;

namespace Porta.ControlCenter;

public partial class MainWindow : Window
{
    private const string AgentServiceName = "AriAgent";
    private const string NotAvailableText = "Not available";
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
    private const double WheelScrollPixelsPerLine = 18d;
    private const double StartupWindowHorizontalMargin = 36d;
    private const double StartupWindowTopMargin = 24d;
    private const double StartupWindowBottomMargin = 28d;
    private const double StartupWindowTargetWidth = 1280d;
    private const double StartupWindowTargetHeight = 820d;
    private const double ActionCategoryBarWidth = 170d;

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
    private HistoryRange _historyRange = HistoryRange.Last1Hour;
    private bool _historyExplainMode;
    private ActionsView _actionsView = ActionsView.Suggestions;
    private readonly DispatcherTimer _settingsAutoSaveTimer;
    private string _settingsAutoSaveReason = "settings_auto";
    private DateTimeOffset _settingsDirtyUntilUtc = DateTimeOffset.MinValue;
    private bool _settingsSaveInFlight;
    private static readonly TimeSpan HistorySelectionHold = TimeSpan.FromSeconds(12);
    private DateTimeOffset? _selectedHistorySnapshotTimestampUtc;
    private long? _selectedHistoryInterventionId;
    private DateTimeOffset? _historySelectionExpiresUtc;
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

    private enum UiSection
    {
        Overview,
        Timeline,
        Interventions,
        Inputs,
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

        ShowSection(UiSection.Overview);
        UpdateHistoryControls();
        StateChanged += (_, _) => UpdateWindowChromeButtons();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => await RefreshDiagnosticsAsync().ConfigureAwait(true);
        Loaded += async (_, _) =>
        {
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
            case ControlCenterActivationTarget.Actions:
                section = UiSection.Interventions;
                return true;
            case ControlCenterActivationTarget.Settings:
                section = UiSection.Settings;
                return true;
            case ControlCenterActivationTarget.Health:
                section = UiSection.Diagnostics;
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
                CurrentSessionUserAgentLauncher.EnsureHealthy(force: true);
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
                    CurrentSessionUserAgentLauncher.EnsureHealthy(force: true);
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
            DateTimeOffset fromUtc = DateTimeOffset.UtcNow.Date;
            _todayTimeline = await _apiClient.GetTimelineAsync(fromUtc, DateTimeOffset.UtcNow, 60).ConfigureAwait(true);
            _hourTimeline = await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 5).ConfigureAwait(true);
            _fourHourTimeline = await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow, 15).ConfigureAwait(true);
            _retention = await _apiClient.GetDataRetentionAsync().ConfigureAwait(true);
            if (ShouldRefreshAllTimeTimeline(forceSlowRefresh))
            {
                _allTimeTimeline = await GetAllTimeTimelineAsync(DateTimeOffset.UtcNow).ConfigureAwait(true);
                _lastAllTimeHistoryRefreshUtc = DateTimeOffset.UtcNow;
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
            PopulateInputs(null, null, null);
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
            OverloadValue.Text = truthSnapshot.OverloadLikelihood.ToString("F2", CultureInfo.InvariantCulture);
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
        PopulateInputs(inputSummary, _retention, truthSnapshot);
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

    private void NowOpenActionsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Interventions);
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Overview);
    }

    private void TimelineNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Timeline);
        PopulateTimeline(GetSelectedHistoryTimeline());
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

    private void InterventionsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Interventions);
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

    private void InputsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Inputs);
    }

    private void StatesNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.States);
    }

    private void SystemAdvancedButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.States);
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Settings);
    }

    private void DiagnosticsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Diagnostics);
    }

    private void ActivateNowDetailCard(string? cardKey)
    {
        switch (cardKey)
        {
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
        ScrollIntoViewDeferred(TimelineFocusScrollViewer);
    }

    private void OpenNowCalibrationDetail()
    {
        ShowSection(UiSection.States);
        ScrollIntoViewDeferred(CalibrationStatusPill);
    }

    private void OpenNowDataQualityDetail()
    {
        ShowSection(UiSection.States);
        ScrollIntoViewDeferred(SignalsStatusPill);
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
            "Delete all local PORTA data now? This removes SQLite state, logs, and stored consent.",
            "Delete Local Data",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool success = await _apiClient.DeleteLocalDataAsync().ConfigureAwait(true);
        if (!success)
        {
            ControlCenterFileLog.Error("delete_local_data_failed", null);
            MessageBox.Show(
                "Delete local data failed. Ensure the service is running and try again.",
                "PORTA Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        ControlCenterFileLog.Info("delete_local_data_success");
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
        MessageBox.Show(
            "Local data deleted. Consent is reset and collection remains off until you grant consent again.",
            "PORTA Control Center",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        OverviewPanel.Visibility = section == UiSection.Overview ? Visibility.Visible : Visibility.Collapsed;
        TimelinePanel.Visibility = section == UiSection.Timeline ? Visibility.Visible : Visibility.Collapsed;
        InterventionsPanel.Visibility = section == UiSection.Interventions ? Visibility.Visible : Visibility.Collapsed;
        InputsPanel.Visibility = section == UiSection.Inputs ? Visibility.Visible : Visibility.Collapsed;
        StatesPanel.Visibility = section == UiSection.States ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = section == UiSection.Account ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == UiSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section == UiSection.Diagnostics ? Visibility.Visible : Visibility.Collapsed;

        SectionTitleText.Text = section switch
        {
            UiSection.Overview => "Today",
            UiSection.Timeline => "History",
            UiSection.Interventions => "Actions",
            UiSection.Inputs => "Tracking",
            UiSection.States => "System > Advanced",
            UiSection.Account => "Account",
            UiSection.Settings => "Settings",
            UiSection.Diagnostics => "System",
            _ => "Today"
        };

        ApplyNavState(OverviewNavButton, section == UiSection.Overview);
        ApplyNavState(TimelineNavButton, section == UiSection.Timeline);
        ApplyNavState(InterventionsNavButton, section == UiSection.Interventions);
        ApplyNavState(InputsNavButton, section == UiSection.Inputs);
        ApplyNavState(StatesNavButton, section == UiSection.States);
        ApplyNavState(AccountNavButton, section == UiSection.Account);
        ApplyNavState(SettingsNavButton, section == UiSection.Settings);
        ApplyNavState(DiagnosticsNavButton, section == UiSection.Diagnostics);

        if (section != UiSection.Overview && WhyDrawerPanel.Visibility == Visibility.Visible)
        {
            SetWhyDrawerVisible(false);
        }

        if (section == UiSection.Interventions)
        {
            UpdateActionsTabControls();
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
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(26, 31, 38))
            : new SolidColorBrush(Color.FromRgb(17, 21, 26));
        button.Foreground = new SolidColorBrush(Color.FromRgb(230, 233, 237));
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(42, 47, 54))
            : new SolidColorBrush(Color.FromRgb(38, 43, 51));
    }

    private LocalApiTimelineResponse? GetSelectedHistoryTimeline()
    {
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
        if (_retention == null || _retention.RetentionDays <= 0)
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
        if (_retention == null || _retention.RetentionDays <= 0)
        {
            return null;
        }

        int retentionDays = Math.Max(1, _retention.RetentionDays);
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

    private void PopulateOverview(
        NowTruth nowTruth,
        LocalApiLiveSessionStateResponse? liveSession,
        LocalApiInputsSummaryResponse? inputSummary,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
        PopulateNowCards(nowTruth, liveSession, recentEvents, _latestVisualState);
        LocalApiCurrentSnapshotResponse? snapshot = nowTruth.Snapshot;

        if (snapshot == null)
        {
            SetStateCard(FocusCardValue, FocusCardConfidence, FocusCardTrend, FocusCardBand, null, null, "focus", null);
            SetStateCard(OverloadCardValue, OverloadCardConfidence, OverloadCardTrend, OverloadCardBand, null, null, "overload", null);
            SetStateCard(FatigueCardValue, FatigueCardConfidence, FatigueCardTrend, FatigueCardBand, null, null, "fatigue", null);
            SetStateCard(StabilityCardValue, StabilityCardConfidence, StabilityCardTrend, StabilityCardBand, null, null, "stability", null);

            RenderRecentActions(recentEvents);
            TodayInterventionsValue.Text = "0";
            TodayAvgFocusValue.Text = "0%";
            TodayPeakOverloadValue.Text = "0%";
            TodayIdleFragmentationValue.Text = inputSummary?.IdleFragmentationIndex.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00";
            TodayMostVolatileHourValue.Text = NotAvailableText;
            RenderHourlyChart(null);
            return;
        }

        double confidence = snapshot.Confidence;
        SetStateCard(
            FocusCardValue,
            FocusCardConfidence,
            FocusCardTrend,
            FocusCardBand,
            snapshot.analysis_ready ? snapshot.FocusStrength : null,
            snapshot.analysis_ready ? confidence : null,
            "focus",
            snapshot.analysis_ready ? previousSnapshot : null);
        SetStateCard(
            OverloadCardValue,
            OverloadCardConfidence,
            OverloadCardTrend,
            OverloadCardBand,
            snapshot.analysis_ready ? snapshot.OverloadLikelihood : null,
            snapshot.analysis_ready ? confidence : null,
            "overload",
            snapshot.analysis_ready ? previousSnapshot : null);
        SetStateCard(FatigueCardValue, FatigueCardConfidence, FatigueCardTrend, FatigueCardBand, snapshot.FatigueProbability, confidence, "fatigue", previousSnapshot);
        SetStateCard(StabilityCardValue, StabilityCardConfidence, StabilityCardTrend, StabilityCardBand, snapshot.CognitiveStability, confidence, "stability", previousSnapshot);

        RenderRecentActions(recentEvents);
        UpdateTodaySummary(inputSummary);
    }

    private void PopulateNowCards(
        NowTruth nowTruth,
        LocalApiLiveSessionStateResponse? liveSession,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        PortaVisualState visualState)
    {
        // Metric contract:
        // - Current focus session is live-first from /v1/session/live.
        // - Focused today and distraction time today remain processed-history metrics.
        // - Overload, confidence, learning state, tracking quality, history, and recent actions remain snapshot/event driven.
        LocalApiCurrentSnapshotResponse? snapshot = nowTruth.Snapshot;
        if (snapshot == null)
        {
            NowOverloadValueText.Text = $"Overload: {NotAvailableText}";
            NowOverloadActiveText.Text = $"Active: {NotAvailableText}";
            NowOverloadTrustText.Text = $"Confidence: {NotAvailableText}";
            NowOverloadSummaryText.Text = BuildNowOverloadSummary(null);
            NowFocusStreakValueText.Text = visualState.CurrentFocusSessionText;
            NowDeepWorkStreakText.Visibility = Visibility.Collapsed;
            NowFocusChoppinessText.Text = $"Choppiness: {NotAvailableText}";
            NowFocusSummaryText.Text = BuildNowFocusSummary(null, liveSession);
            NowFocusFootnoteText.Text = "Resets after 2+ minutes away.";
            NowFocusStatusText.Text = BuildLiveSessionFocusStatus(liveSession) ?? visualState.FocusStatusText;
            NowFocusStatusText.Visibility = Visibility.Visible;
            NowOverloadFivText.Text = "Explain not available (backend field missing).";
            NowOverloadTopReasonsText.Text = $"Top reasons: {visualState.WhySummary}";
            NowOverloadQualityAgreementText.Text = $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}";

            NowCalibrationStatusText.Text = "Bootstrapping";
            NowCalibrationProgressText.Text = $"Progress: {NotAvailableText}";
            NowCalibrationQualityText.Text = $"Recent tracking quality: {NotAvailableText} (last 2 min)";
            NowCalibrationColdStartText.Text = $"Stage: {NotAvailableText}";
            NowCalibrationSummaryText.Text = BuildNowCalibrationSummary(null);
            NowCalibrationTrustCapText.Visibility = Visibility.Visible;

            NowDataQualityScoreText.Text = $"Tracking quality: {NotAvailableText}";
            NowDataQualityPartialText.Text = $"Partial data: {NotAvailableText}";
            NowDataQualitySummaryText.Text = BuildNowDataQualitySummary(null);
            NowDataQualityMissingText.Visibility = Visibility.Collapsed;

            NowLastActionTitleText.Text = "No actions recently";
            NowLastActionTimeText.Text = $"Time: {NotAvailableText}";
            NowOpenActionsButton.IsEnabled = false;

            NowWhatPortaDoingText.Text = BuildCurrentStatusSentence(liveSession) ?? visualState.OperatingSentence;
            NowWhatPortaDoingText.Foreground = new SolidColorBrush(visualState.Theme.AccentColor);
            _activeRecoveryEventId = 0;
            NowRecoveryBannerBorder.Visibility = Visibility.Collapsed;
            NowWaitingDataText.Text = string.IsNullOrWhiteSpace(nowTruth.Explanation)
                ? "Waiting for service data"
                : nowTruth.Explanation;
            NowWaitingDataText.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(nowTruth.Explanation))
        {
            NowWaitingDataText.Visibility = Visibility.Collapsed;
        }
        else
        {
            NowWaitingDataText.Text = nowTruth.Explanation;
            NowWaitingDataText.Visibility = Visibility.Visible;
        }
        if (visualState.SnapshotAnalysisReady)
        {
            AnimateTopMetric(
                metricKey: "now_overload",
                targetValue: Math.Clamp(snapshot.OverloadLikelihood * 100d, 0d, 100d),
                applyValue: value => NowOverloadValueText.Text = $"Overload {value:F0}%");
            NowOverloadActiveText.Text = $"Active: {(visualState.OverloadActive ? "Yes" : "No")}";
            AnimateTopMetric(
                metricKey: "now_trust",
                targetValue: Math.Clamp(snapshot.Confidence * 100d, 0d, 100d),
                applyValue: value => NowOverloadTrustText.Text = $"Confidence: {value:F0}%");
        }
        else
        {
            NowOverloadValueText.Text = "Overload analyzing";
            NowOverloadActiveText.Text = "Active: assessing";
            NowOverloadTrustText.Text = $"Confidence: {NotAvailableText}";
        }

        NowOverloadSummaryText.Text = BuildNowOverloadSummary(snapshot);

        double? focusDisplaySeconds = visualState.CurrentFocusSessionSeconds;
        if (focusDisplaySeconds.HasValue)
        {
            AnimateTopMetric(
                metricKey: "now_focus",
                targetValue: Math.Max(0d, focusDisplaySeconds.Value / 60d),
                applyValue: value => NowFocusStreakValueText.Text = $"Current focus session: {FormatFocusedTime(value * 60d)}");
        }
        else
        {
            NowFocusStreakValueText.Text = visualState.CurrentFocusSessionText;
        }

        if (visualState.ShowDeepWorkStreak)
        {
            NowDeepWorkStreakText.Text = visualState.DeepWorkStreakText;
            NowDeepWorkStreakText.Visibility = Visibility.Visible;
        }
        else
        {
            NowDeepWorkStreakText.Visibility = Visibility.Collapsed;
        }

        NowFocusChoppinessText.Text = visualState.SnapshotAnalysisReady
            ? $"Choppiness: {MapChoppiness(snapshot.IdleFragmentationIndex)} ({snapshot.IdleFragmentationIndex:F2})"
            : $"Choppiness: {NotAvailableText}";
        NowFocusSummaryText.Text = BuildNowFocusSummary(snapshot, liveSession);
        NowFocusFootnoteText.Text = visualState.SnapshotAnalysisReady
            ? "Short pauses and quick app checks do not break the streak."
            : "Live focus updates first; history and modeled streaks catch up after processed buckets arrive.";
        string focusStatus = BuildLiveSessionFocusStatus(liveSession) ?? visualState.FocusStatusText;
        NowFocusStatusText.Text = focusStatus;
        NowFocusStatusText.Visibility = string.IsNullOrWhiteSpace(focusStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (visualState.SnapshotAnalysisReady)
        {
            OverloadExplainView explain = ParseOverloadExplain(snapshot.ContributorsJson);
            NowOverloadFivText.Text = explain.FivSummary;
            string topReasons = explain.HasStructuredData ? explain.TopReasons : visualState.WhySummary;
            NowOverloadTopReasonsText.Text = $"Top reasons: {topReasons}";
            NowOverloadQualityAgreementText.Text = $"Quality: {explain.Quality} | Agreement: {explain.Agreement}";
        }
        else
        {
            NowOverloadFivText.Text = "Explain will appear once analyzed overload state is ready.";
            NowOverloadTopReasonsText.Text = "Top reasons: Waiting for analyzed overload state.";
            NowOverloadQualityAgreementText.Text = $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}";
        }

        // Source: GET /v1/snapshot/current -> baseline_ready
        string calibrationStage = NormalizeCalibrationStage(snapshot.CalibrationStage, snapshot.BaselineReady, snapshot.ColdStart);
        bool calibrationReady = string.Equals(calibrationStage, CalibrationStageCalibrated, StringComparison.Ordinal);
        NowCalibrationStatusText.Text = ToCalibrationStageLabel(calibrationStage);
        // Source: GET /v1/snapshot/current -> baseline_ready_pct
        NowCalibrationProgressText.Text = snapshot.BaselineReadyPct.HasValue
            ? $"Progress: {(snapshot.BaselineReadyPct.Value * 100d):F0}% metrics ready"
            : $"Progress: {NotAvailableText}";
        // Source: GET /v1/snapshot/current -> avg_quality_2m
        NowCalibrationQualityText.Text = snapshot.AvgQuality2m.HasValue
            ? $"Recent tracking quality: {snapshot.AvgQuality2m.Value:F2} (last 2 min)"
            : $"Recent tracking quality: {NotAvailableText} (last 2 min)";
        // Source: GET /v1/snapshot/current -> cold_start
        NowCalibrationColdStartText.Text = $"Stage: {ToCalibrationStageLabel(calibrationStage)} | Cold start: {MapOnOff(snapshot.ColdStart)}";
        NowCalibrationSummaryText.Text = BuildNowCalibrationSummary(snapshot);
        NowCalibrationTrustCapText.Text = string.Equals(calibrationStage, CalibrationStageProvisional, StringComparison.Ordinal)
            ? "Suggestions are active while stronger system actions remain gated."
            : "Confidence is capped until enough baseline data accumulates.";
        NowCalibrationTrustCapText.Visibility = calibrationReady ? Visibility.Collapsed : Visibility.Visible;

        // Source: GET /v1/snapshot/current -> current_quality_score
        NowDataQualityScoreText.Text = snapshot.CurrentQualityScore.HasValue
            ? $"Tracking quality: {snapshot.CurrentQualityScore.Value:F2}"
            : $"Tracking quality: {NotAvailableText}";
        // Source: GET /v1/snapshot/current -> partial_data
        NowDataQualityPartialText.Text = $"Partial data: {MapYesNo(snapshot.PartialData)}";
        NowDataQualitySummaryText.Text = BuildNowDataQualitySummary(snapshot);
        // Source: GET /v1/snapshot/current -> missing_major_channels[]
        if (snapshot.MissingMajorChannels != null && snapshot.MissingMajorChannels.Count > 0)
        {
            string missing = string.Join(" / ", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            NowDataQualityMissingText.Text = $"Missing: {missing}";
            NowDataQualityMissingText.Visibility = Visibility.Visible;
        }
        else
        {
            NowDataQualityMissingText.Visibility = Visibility.Collapsed;
        }

        LocalApiEventResponse? lastActionEvent = recentEvents.FirstOrDefault();
        if (lastActionEvent == null)
        {
            NowLastActionTitleText.Text = "No actions recently";
            NowLastActionTimeText.Text = $"Time: {NotAvailableText}";
            NowOpenActionsButton.IsEnabled = false;
        }
        else
        {
            NowLastActionTitleText.Text = ToActionTitle(lastActionEvent);
            NowLastActionTimeText.Text = $"Time: {lastActionEvent.TsUtc.ToLocalTime():HH:mm:ss}";
            NowOpenActionsButton.IsEnabled = true;
        }

        NowWhatPortaDoingText.Text = BuildCurrentStatusSentence(liveSession) ?? visualState.OperatingSentence;
        NowWhatPortaDoingText.Foreground = new SolidColorBrush(visualState.Theme.AccentColor);
        if (visualState.ShowRecoveryBanner
            && visualState.RecoveryEventId > 0
            && visualState.RecoveryEventId != _dismissedRecoveryEventId)
        {
            _activeRecoveryEventId = visualState.RecoveryEventId;
            NowRecoveryBannerText.Text = visualState.RecoveryBannerText;
            NowRecoveryBannerBorder.Visibility = Visibility.Visible;
        }
        else
        {
            _activeRecoveryEventId = 0;
            NowRecoveryBannerBorder.Visibility = Visibility.Collapsed;
        }
    }

    private string BuildNowOverloadSummary(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot is { analysis_ready: false })
        {
            return "Waiting for analyzed overload state. Click for Why once the next processed bucket lands.";
        }

        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshots.Count == 0)
        {
            return snapshot == null
                ? "Open Why for current contributors as soon as live overload data arrives."
                : $"Current level: {(Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d) * 100d):F0}%. Click for Why.";
        }

        string peakText = $"{(snapshots.Max(s => Math.Clamp(s.OverloadLikelihood, 0d, 1d)) * 100d):F0}%";
        string volatileHour = ComputeMostVolatileHour(snapshots);
        return volatileHour == NotAvailableText
            ? $"Today peak: {peakText}. Click for Why."
            : $"Today peak: {peakText} | Volatile hour: {volatileHour}. Click for Why.";
    }

    private string BuildNowFocusSummary(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiLiveSessionStateResponse? liveSession)
    {
        double? liveFocusSeconds = VisualStateAdapter.ResolveCurrentFocusSessionSeconds(snapshot, liveSession);
        if (IsLiveSessionFresh(liveSession)
            && liveFocusSeconds.HasValue
            && snapshot is { analysis_ready: false })
        {
            return $"Current focus session: {FormatFocusedTime(liveFocusSeconds.Value)}. Click for History once processed buckets catch up.";
        }

        if (snapshot is { analysis_ready: false })
        {
            return "Watching your current focus session now. History will fill in after processed buckets arrive.";
        }

        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = _todayTimeline?.Snapshots ?? [];
        if (snapshots.Count == 0)
        {
            return snapshot == null
                ? "Open History once live focus buckets arrive."
                : $"Current focus session: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}. Click for History.";
        }

        double maxStreakSeconds = snapshots.Max(s => Math.Max(0d, s.SustainedAttentionDurationSec));
        double averageFocusPercent = snapshots.Average(s => Math.Clamp(s.FocusStrength, 0d, 1d)) * 100d;
        return $"Best focus block today: {FormatFocusedTime(maxStreakSeconds)} | Avg focus: {averageFocusPercent:F0}%. Click for History.";
    }

    private string BuildNowCalibrationSummary(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot == null)
        {
            return "Open System > Advanced for live calibration blockers.";
        }

        string confidenceLabel = ToConfidenceLabel(NormalizeTrustTier(snapshot.TrustTier, snapshot.ModelConfidence ?? snapshot.Confidence));
        string systemActions = snapshot.SystemActionsEnabled == true ? "enabled" : "gated";
        string progressText = snapshot.BaselineReadyPct.HasValue
            ? $"{(Math.Clamp(snapshot.BaselineReadyPct.Value, 0d, 1d) * 100d):F0}% ready"
            : "progress pending";
        return $"Confidence: {confidenceLabel} | System actions: {systemActions} | {progressText}. Click for System > Advanced.";
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
            return "Open System > Advanced once live tracking-quality buckets arrive.";
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
            return $"Focus streak paused: missing {missing} signals.";
        }

        if (snapshot.PartialData == true)
        {
            return "Focus streak is conservative while data is partial.";
        }

        if (snapshot.SustainedAttentionDurationSec <= 0.5d)
        {
            return "No continuous focus streak detected yet.";
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

        string focusText = !string.IsNullOrWhiteSpace(liveSession.focus_app_hash)
            ? "focused now"
            : liveSession.user_active
                ? "user active"
                : "watching live session";
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

        string focusText = !string.IsNullOrWhiteSpace(liveSession.focus_app_hash)
            ? "focused now"
            : liveSession.user_active
                ? "user active"
                : "watching";
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

        string focusText = !string.IsNullOrWhiteSpace(liveSession.focus_app_hash)
            ? "Focused now"
            : liveSession.user_active
                ? "User active"
                : "Watching live";
        string liveStreakText = liveSession.focus_elapsed_seconds.HasValue
            ? $"live streak {FormatAgeLabel(liveSession.focus_elapsed_seconds)}"
            : "live streak warming";
        return $"{focusText} | {liveStreakText} | typing {(liveSession.keyboard_active ? "active" : "idle")} | mouse {(liveSession.mouse_active ? "active" : "idle")}";
    }

    private static string? BuildCurrentStatusSentence(LocalApiLiveSessionStateResponse? liveSession)
    {
        if (!IsLiveSessionFresh(liveSession))
        {
            return null;
        }

        if (liveSession!.idle)
        {
            return $"Watching live session while you are idle ({FormatAgeLabel(liveSession.idle_seconds)}).";
        }

        if (liveSession.keyboard_active && liveSession.mouse_active)
        {
            return liveSession.focus_elapsed_seconds.HasValue
                ? $"Watching live focus ({FormatAgeLabel(liveSession.focus_elapsed_seconds)}) with typing and mouse activity."
                : "Watching live typing and mouse activity.";
        }

        if (liveSession.keyboard_active)
        {
            return liveSession.focus_elapsed_seconds.HasValue
                ? $"Watching live focus ({FormatAgeLabel(liveSession.focus_elapsed_seconds)}) with typing activity."
                : "Watching live typing activity.";
        }

        if (liveSession.mouse_active)
        {
            return "Watching live mouse activity.";
        }

        if (liveSession.user_active)
        {
            return "Watching live session activity.";
        }

        return "Watching live session.";
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

    private static string FormatStartupSourceLabel(string? startupSource)
    {
        return startupSource?.Trim().ToLowerInvariant() switch
        {
            "run-key" => "Windows Run key",
            "windows-restart" => "Windows restart recovery",
            "control-center-recovery" => "Control Center recovery",
            "" or null => NotAvailableText,
            _ => startupSource!
        };
    }

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
                WarningText: $"Live capture is not trusted: {captureFailureDetail}. Start or restart Porta.UserAgent in the interactive session.",
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
                WarningText: "Waiting for a fresh user-agent heartbeat from the interactive session.",
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
        string continuitySeconds = FormatFocusedTime(snapshot.TaskContinuitySeconds);
        string continuityLabel =
            $"{continuityState} | {continuitySeconds} continuity | score {snapshot.TaskContinuityScore:F2} | confidence {(snapshot.ContinuityConfidence ?? 0d):F2}";

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
            $"Overload {(snapshot.OverloadActive ? "ON" : "OFF")} | Deep work {(snapshot.DeepWorkActive ? "ON" : "OFF")} | Notifications {(snapshot.NotificationsSuppressed ? "suppressed" : "normal")}";

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
            string actionTitle = HumanizeActionName(snapshot.BreakPendingAction);
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

        if (snapshot.BreakMicrobreakEffectiveFocusSeconds.HasValue || snapshot.BreakShortBreakEffectiveFocusSeconds.HasValue)
        {
            string microbreak = snapshot.BreakMicrobreakEffectiveFocusSeconds.HasValue
                ? FormatFocusedTime(snapshot.BreakMicrobreakEffectiveFocusSeconds.Value)
                : NotAvailableText;
            string shortBreak = snapshot.BreakShortBreakEffectiveFocusSeconds.HasValue
                ? FormatFocusedTime(snapshot.BreakShortBreakEffectiveFocusSeconds.Value)
                : NotAvailableText;
            return $"Microbreak window: {microbreak} | Short-break window: {shortBreak}";
        }

        return "No break suggestion is currently pending.";
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
        if (string.Equals(actionEvent.Type, "BREAK_SUGGESTION", StringComparison.OrdinalIgnoreCase)
            || string.Equals(actionEvent.Action, "BREAK_SUGGESTION", StringComparison.OrdinalIgnoreCase))
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
        CaptureHealthView view = BuildCaptureHealthView(status, liveSession, statusFresh);
        var statusBrush = new SolidColorBrush(view.StatusColor);
        var detailBrush = new SolidColorBrush(view.DetailColor);

        NowCaptureStatusText.Text = $"Status: {view.StatusLabel}";
        NowCaptureStatusText.Foreground = statusBrush;
        NowCaptureContextText.Text = $"Context: {view.ContextLabel}";
        NowCaptureKeyboardText.Text = $"Keyboard signal: {view.KeyboardSignalLabel}";
        NowCaptureMouseText.Text = $"Mouse signal: {view.MouseSignalLabel}";
        NowCaptureLastInputText.Text = $"Last input: {view.LastInputLabel}";
        NowCaptureWarningText.Text = view.WarningText;
        NowCaptureWarningText.Foreground = detailBrush;
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
        HealthSessionWarningValue.Text = view.WarningText;
        HealthSessionWarningValue.Foreground = detailBrush;
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
        HealthStateWarningValue.Text = view.WarningText;
        HealthStateWarningValue.Foreground = detailBrush;
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

    private void SetStateCard(
        TextBlock valueText,
        TextBlock confidenceText,
        TextBlock trendText,
        Border intensityBand,
        double? value,
        double? confidence,
        string metricKey,
        LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
        if (!value.HasValue)
        {
            valueText.Text = NotAvailableText;
            confidenceText.Text = $"Confidence: {NotAvailableText}";
            trendText.Text = "Trend: -";
            intensityBand.Background = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            return;
        }

        valueText.Text = value.Value.ToString("F2", CultureInfo.InvariantCulture);
        confidenceText.Text = confidence.HasValue
            ? $"Confidence: {Math.Clamp(confidence.Value * 100d, 0d, 100d):F0}%"
            : $"Confidence: {NotAvailableText}";

        trendText.Text = $"Trend: {ComputeTrend(metricKey, value.Value, previousSnapshot)}";
        intensityBand.Background = BuildIntensityBrush(value.Value);
    }

    private static string ComputeTrend(string metricKey, double current, LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
        if (previousSnapshot == null)
        {
            return "Stable";
        }

        double previous = metricKey switch
        {
            "focus" => previousSnapshot.FocusStrength,
            "overload" => previousSnapshot.OverloadLikelihood,
            "fatigue" => previousSnapshot.FatigueProbability,
            "stability" => previousSnapshot.CognitiveStability,
            _ => current
        };

        const double threshold = 0.01d;
        if (current - previous > threshold)
        {
            return "Up";
        }

        if (previous - current > threshold)
        {
            return "Down";
        }

        return "Stable";
    }

    private static Brush BuildIntensityBrush(double value)
    {
        double bounded = Math.Clamp(value, 0d, 1d);
        if (bounded < 0.34d)
        {
            return new SolidColorBrush(Color.FromRgb(34, 197, 94));
        }

        if (bounded < 0.67d)
        {
            return new SolidColorBrush(Color.FromRgb(245, 158, 11));
        }

        return new SolidColorBrush(Color.FromRgb(239, 68, 68));
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

    private void RenderRecentActions(IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        RecentActionsPanel.Children.Clear();
        if (recentEvents.Count == 0)
        {
            RecentActionsPanel.Children.Add(new TextBlock
            {
                Text = "No recent actions yet.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184))
            });
            return;
        }

        foreach (LocalApiEventResponse evt in recentEvents.Take(5))
        {
            var card = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Background = new SolidColorBrush(Color.FromRgb(15, 23, 42))
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = evt.Action,
                FontWeight = FontWeights.SemiBold
            });

            string duration = evt.DurationSeconds.HasValue
                ? TimeSpan.FromSeconds(Math.Max(0, evt.DurationSeconds.Value)).ToString(@"mm\:ss", CultureInfo.InvariantCulture)
                : NotAvailableText;
            stack.Children.Add(new TextBlock
            {
                Text = $"Duration: {duration} | Trigger: {evt.TriggerState} {evt.TriggerValue:F2} | Confidence: {(evt.Confidence * 100d):F0}%",
                Foreground = new SolidColorBrush(Color.FromRgb(191, 219, 254))
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Why: {evt.Reason}",
                TextWrapping = TextWrapping.Wrap
            });

            if (evt.Contributors.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"Contributors: {string.Join(", ", evt.Contributors.Take(4))}",
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            stack.Children.Add(new TextBlock
            {
                Text = evt.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184))
            });

            card.Child = stack;
            RecentActionsPanel.Children.Add(card);
        }
    }

    private void UpdateTodaySummary(LocalApiInputsSummaryResponse? inputSummary)
    {
        LocalApiTimelineResponse? timeline = _todayTimeline;
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = timeline?.Snapshots ?? [];

        TodayInterventionsValue.Text = (timeline?.Interventions.Count ?? 0).ToString(CultureInfo.InvariantCulture);
        TodayAvgFocusValue.Text = snapshots.Count > 0
            ? $"{(snapshots.Average(s => s.FocusStrength) * 100d):F0}%"
            : "0%";
        TodayPeakOverloadValue.Text = snapshots.Count > 0
            ? $"{(snapshots.Max(s => s.OverloadLikelihood) * 100d):F0}%"
            : "0%";
        TodayIdleFragmentationValue.Text = inputSummary?.IdleFragmentationIndex.ToString("F2", CultureInfo.InvariantCulture) ?? "0.00";
        TodayMostVolatileHourValue.Text = ComputeMostVolatileHour(snapshots);
        RenderHourlyChart(timeline);
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
                score += Math.Abs(current.OverloadLikelihood - previous.OverloadLikelihood);
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

    private void RenderHourlyChart(LocalApiTimelineResponse? timeline)
    {
        TodayHourlyChartPanel.Children.Clear();
        if (timeline == null || timeline.Snapshots.Count == 0)
        {
            TodayHourlyChartPanel.Children.Add(new TextBlock
            {
                Text = "No timeline data yet.",
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184))
            });
            return;
        }

        Dictionary<int, double> perHourValues = timeline.Snapshots
            .GroupBy(s => s.TimestampUtc.ToLocalTime().Hour)
            .ToDictionary(
                g => g.Key,
                g => g.Average(x => x.FocusStrength));

        double max = perHourValues.Values.DefaultIfEmpty(0.01d).Max();
        max = max <= 0d ? 0.01d : max;

        for (int hour = 0; hour < 24; hour++)
        {
            double value = perHourValues.TryGetValue(hour, out double v) ? v : 0d;
            double normalized = value / max;
            double barHeight = Math.Max(4d, normalized * 92d);

            var wrapper = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 0)
            };

            wrapper.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 14,
                Height = barHeight,
                Fill = BuildIntensityBrush(1d - Math.Clamp(value, 0d, 1d)),
                RadiusX = 2,
                RadiusY = 2
            });

            wrapper.Children.Add(new TextBlock
            {
                Text = hour.ToString("00", CultureInfo.InvariantCulture),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Margin = new Thickness(0, 4, 0, 0)
            });

            TodayHourlyChartPanel.Children.Add(wrapper);
        }
    }

    private void PopulateInputs(
        LocalApiInputsSummaryResponse? inputsSummary,
        LocalApiRetentionResponse? retention,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (inputsSummary == null)
        {
            LiveKeyboardMeter.Value = 0;
            LiveMouseMeter.Value = 0;
            LiveSwitchMeter.Value = 0;
            LiveIdleMeter.Value = 0;
            LiveKeyboardValue.Text = NotAvailableText;
            LiveMouseValue.Text = NotAvailableText;
            LiveSwitchValue.Text = NotAvailableText;
            LiveIdleValue.Text = NotAvailableText;
        }
        else
        {
            double keyboardNorm = Math.Clamp(inputsSummary.TypingBurstIntensity, 0d, 1d);
            double mouseNorm = Math.Clamp(inputsSummary.ScrollVelocityIndex, 0d, 1d);
            double switchesNorm = Math.Clamp(inputsSummary.FocusSwitchRate, 0d, 1d);
            double idleNorm = Math.Clamp(inputsSummary.IdleFragmentationIndex, 0d, 1d);
            double idleSeconds = Math.Clamp(idleNorm * inputsSummary.WindowSeconds, 0d, inputsSummary.WindowSeconds);

            LiveKeyboardMeter.Value = keyboardNorm * 100d;
            LiveMouseMeter.Value = mouseNorm * 100d;
            LiveSwitchMeter.Value = switchesNorm * 100d;
            LiveIdleMeter.Value = idleNorm * 100d;

            LiveKeyboardValue.Text = $"{LiveKeyboardMeter.Value:F0}%";
            LiveMouseValue.Text = $"{LiveMouseMeter.Value:F0}%";
            LiveSwitchValue.Text = $"{LiveSwitchMeter.Value:F0}%";
            LiveIdleValue.Text = $"{idleSeconds:F0}s";
        }

        if (snapshot == null)
        {
            LiveQualityValue.Text = $"Tracking quality: {NotAvailableText}";
            LivePartialValue.Text = $"Partial data: {NotAvailableText}";
            LiveMissingChannelsValue.Text = $"Missing: {NotAvailableText}";
        }
        else
        {
            LiveQualityValue.Text = snapshot.CurrentQualityScore.HasValue
                ? $"Tracking quality: {snapshot.CurrentQualityScore.Value:F2}"
                : $"Tracking quality: {NotAvailableText}";
            LivePartialValue.Text = snapshot.PartialData.HasValue
                ? $"Partial data: {(snapshot.PartialData.Value ? "Yes" : "No")}"
                : $"Partial data: {NotAvailableText}";
            LiveMissingChannelsValue.Text = snapshot.MissingMajorChannels is { Count: > 0 }
                ? $"Missing: {string.Join(" / ", snapshot.MissingMajorChannels)}"
                : "Missing: None";
        }

        if (retention == null)
        {
            RetentionWindowValue.Text = $"Retention window: {NotAvailableText}";
            RetentionSizeValue.Text = $"Local data size: {NotAvailableText}";
            return;
        }

        RetentionWindowValue.Text = $"Retention window: {retention.RetentionDays} days";
        RetentionSizeValue.Text = $"Local data size: {FormatBytes(retention.TotalBytes)} (db {FormatBytes(retention.DatabaseBytes)}, logs {FormatBytes(retention.LogBytes)})";
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
                BuildActionClassificationText(e),
                BuildDecisionStateBadge(e),
                BuildExecutionStateBadge(e),
                e))
            .ToList();

        IReadOnlyList<ChangeRow> changes = ordered
            .Where(static e => IsSystemChangeEvent(e))
            .Select(e => new ChangeRow(
                WhatChanged: string.IsNullOrWhiteSpace(e.WhatChanged) ? HumanizeActionName(e.Action) : e.WhatChanged,
                When: e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Why: BuildChangeShortWhy(e),
                Undo: BuildUndoBadge(e),
                Classification: BuildActionClassificationText(e),
                Decision: BuildDecisionStateBadge(e),
                Execution: BuildExecutionStateBadge(e),
                Source: e))
            .ToList();

        SuggestionsListView.ItemsSource = suggestions;
        ChangesListView.ItemsSource = changes;
        SuggestionsCountText.Text = suggestions.Count.ToString(CultureInfo.InvariantCulture);
        ChangesCountText.Text = changes.Count.ToString(CultureInfo.InvariantCulture);
        PopulateActionsOverview(ordered, suggestions.Count, changes.Count);

        SuggestionsEmptyStateText.Visibility = suggestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SuggestionsListView.Visibility = suggestions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangesEmptyStateText.Visibility = changes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChangesListView.Visibility = changes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionsTabControls();

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

    private void PopulateActionsOverview(
        IReadOnlyList<LocalApiEventResponse> ordered,
        int suggestionCount,
        int changeCount)
    {
        ActionsTotalCountText.Text = ordered.Count.ToString(CultureInfo.InvariantCulture);
        ActionsLastSeenText.Text = ordered.Count == 0
            ? "No recent actions"
            : BuildActionLastSeenText(ordered[0]);

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
        return $"{BuildActionTimelineTitle(ev)} · {BuildRelativeTimeLabel(ev.TimestampUtc)}";
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
        if (action.Contains("break", StringComparison.Ordinal))
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
        if (sender is not ListView listView)
        {
            return;
        }

        ScrollViewer? scrollViewer = FindDescendant<ScrollViewer>(listView);
        if (scrollViewer == null)
        {
            return;
        }

        if (TryScrollViewerByMouseWheel(scrollViewer, e.Delta))
        {
            e.Handled = true;
        }
    }

    private void InitializeUnifiedScrollBehavior()
    {
        RegisterUnifiedScroll(OverviewPanel);
        RegisterUnifiedScroll(InputsPanel);
        RegisterUnifiedScroll(InterventionsPanel);
        RegisterUnifiedScroll(ActionDetailsScrollViewer);
        RegisterUnifiedScroll(SettingsPanel);
        RegisterUnifiedScroll(StatesPanel);
        RegisterUnifiedScroll(TimelinePanel);
        RegisterUnifiedScroll(AccountPanel);
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
            : Math.Max(42d, SystemParameters.WheelScrollLines * WheelScrollPixelsPerLine);
        return baseDistance * multiplier;
    }

    private void UpdateInterventionDrawer(LocalApiEventResponse? item)
    {
        if (item == null)
        {
            ActionDrawerTitle.Text = "Choose one suggestion or change";
            ActionDrawerWhen.Text = "Timing: Pick a row on the left.";
            ActionDrawerClassification.Text = $"Type: {NotAvailableText}";
            ActionDrawerDecisionState.Text = $"Decision: {NotAvailableText}";
            ActionDrawerExecutionState.Text = $"Execution: {NotAvailableText}";
            ActionDrawerShortWhy.Text = "Why it happened: Pick a row on the left to see the reason in plain language.";
            ActionDrawerCooldown.Text = $"Cooldown remaining: {NotAvailableText}";
            ActionDrawerRepeat.Text = string.Empty;
            ActionDrawerRepeat.Visibility = Visibility.Collapsed;
            ActionDrawerWhatChanged.Text = "What happened: Suggestions are ideas. Changes are settings Ari actually changed for you.";
            ActionDrawerUndo.Text = "Undo: Choose a change to see whether it can be reversed.";
            ActionDrawerWhyJson.Text = NotAvailableText;
            ActionDrawerUndoButton.IsEnabled = false;
            ActionDrawerUndoButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool isSuggestion = IsSuggestionEvent(item.Action);
        ActionDrawerTitle.Text = isSuggestion
            ? BuildSuggestionTitle(item.Action)
            : (string.IsNullOrWhiteSpace(item.WhatChanged) ? HumanizeActionName(item.Action) : item.WhatChanged);
        ActionDrawerWhen.Text = isSuggestion
            ? $"Suggested at: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : $"Changed at: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        ActionDrawerClassification.Text = BuildActionClassificationText(item);
        ActionDrawerDecisionState.Text = BuildDecisionStateText(item);
        ActionDrawerExecutionState.Text = BuildExecutionStateText(item);
        ActionDrawerShortWhy.Text = isSuggestion
            ? $"Why Ari suggested it: {BuildSuggestionShortWhy(item)}"
            : $"Why Ari changed it: {BuildChangeShortWhy(item)}";
        ActionDrawerCooldown.Text = $"Cooldown remaining: {FormatCooldownRemaining(item.CooldownEndTimestampUtc)}";
        ActionDrawerRepeat.Text = isSuggestion
            ? BuildSuggestionRepeatLine(item)
            : string.Empty;
        ActionDrawerRepeat.Visibility = isSuggestion ? Visibility.Visible : Visibility.Collapsed;
        ActionDrawerWhatChanged.Text = isSuggestion
            ? "What happens if you ignore it: nothing changes right away. Ari may suggest it again later if the pattern continues."
            : $"Change made: {(string.IsNullOrWhiteSpace(item.WhatChanged) ? HumanizeActionName(item.Action) : item.WhatChanged)}";
        ActionDrawerUndo.Text = isSuggestion
            ? "Undo: Not needed. This was only a suggestion."
            : $"Undo: {BuildUndoDetails(item)}";
        ActionDrawerUndo.Visibility = Visibility.Visible;
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

    private static string BuildSuggestionTitle(string? action)
    {
        string normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "break_suggestion" => "Take a break",
            "focus_lock_in_suggestion" => "Lock in focus",
            "stretch_break_suggestion" => "Take a stretch break",
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

        return "Triggered by current focus and overload pattern.";
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
            return "Policy intent only. No desktop executor is implemented for this action yet.";
        }

        if (IsUnsupportedAction(ev.Action))
        {
            return "Unsupported on this device/build. No desktop change was applied.";
        }

        string executionState = NormalizeExecutionState(ev.ExecutionState);
        if (!IsRealDesktopAction(ev))
        {
            return "Recorded by current regulation policy. No desktop executor was involved.";
        }

        if (executionState == "failed")
        {
            return "Execution failed in the user session. No desktop change was applied.";
        }

        if (executionState == "decided")
        {
            return "Decision recorded and waiting for desktop execution acknowledgement.";
        }

        return "Applied according to current regulation policy.";
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

    private static string BuildUndoBadge(LocalApiEventResponse ev)
    {
        if (ev.Reversible)
        {
            return "Available";
        }

        if (!string.IsNullOrWhiteSpace(ev.HowToUndo) || HasManualUndoHint(ev.Action))
        {
            return "Manual";
        }

        return "Info only";
    }

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
        double focusedMinutes = Math.Max(0d, snapshot.SustainedAttentionDurationSec / 60d);
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

        if (snapshot.SustainedAttentionDurationSec < UiBreakFocusThresholdSeconds)
        {
            decisionBlockers.Add("building task continuity");
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
        DecisionsCurrentText.Text = $"Current status: overload {(overload * 100d):F0}%, confidence {ToConfidenceLabel(trustTier)} ({(trust * 100d):F0}%), current focus session {focusedMinutes:F0} min, suggestions {(suggestionsEnabled ? "enabled" : "gated")}, system actions {(systemActionsEnabled ? "enabled" : "gated")}.";
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
        HistoryCoordinateCanvas.Children.Clear();
        TimelineOverloadTrackPanel.Children.Clear();
        TimelineTrustTrackPanel.Children.Clear();
        TimelineFocusTrackPanel.Children.Clear();
        TimelineInterventionTrackPanel.Children.Clear();
        TimelineOverloadActiveOverlayPanel.Children.Clear();

        if (timeline == null || timeline.Snapshots.Count == 0)
        {
            HistoryCoordinateCanvas.Width = 320d;
            HistoryCoordinateEmptyText.Visibility = Visibility.Visible;
            HistoryCoordinateXAxisText.Text = BuildHistoryXAxisText(null);
            TimelineOverloadTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineTrustTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineFocusTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineInterventionTrackPanel.Children.Add(new TextBlock { Text = "No action markers.", Foreground = Brushes.Gainsboro });
            TimelineOverloadActiveOverlayPanel.Children.Add(new TextBlock { Text = "No overload-active data.", Foreground = Brushes.Gainsboro });
            PopulateHistorySummary(null, []);
            SetTimelineDetail(null, null);
            return;
        }

        int maxPoints = _historyRange switch
        {
            HistoryRange.Last1Hour => 360,
            HistoryRange.Last4Hours => 480,
            HistoryRange.AllTime => 720,
            _ => 600
        };
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = DownsampleSnapshots(timeline.Snapshots, maxPoints);
        PopulateHistorySummary(timeline, snapshots);
        RenderHistoryCoordinateChart(timeline, snapshots);

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            TimelineOverloadTrackPanel.Children.Add(BuildTrackPoint(snapshot, snapshot.OverloadLikelihood, Color.FromRgb(239, 68, 68), "Overload"));
            TimelineTrustTrackPanel.Children.Add(BuildTrackPoint(snapshot, snapshot.Confidence, Color.FromRgb(59, 130, 246), "Confidence"));
            double focusNormalized = Math.Clamp(snapshot.SustainedAttentionDurationSec / 3600d, 0d, 1d);
            TimelineFocusTrackPanel.Children.Add(BuildTrackPoint(snapshot, focusNormalized, Color.FromRgb(34, 197, 94), "Current focus session"));
            TimelineOverloadActiveOverlayPanel.Children.Add(BuildOverloadActiveMarker(snapshot));
        }

        if (timeline.Interventions.Count == 0)
        {
            TimelineInterventionTrackPanel.Children.Add(new TextBlock
            {
                Text = "No action markers in this range.",
                Foreground = Brushes.Gainsboro
            });
        }
        else
        {
            foreach (LocalApiEventResponse intervention in timeline.Interventions.OrderBy(static ev => ev.TimestampUtc))
            {
                TimelineInterventionTrackPanel.Children.Add(BuildInterventionMarker(intervention));
            }
        }

        if (_historySelectionExpiresUtc.HasValue
            && DateTimeOffset.UtcNow >= _historySelectionExpiresUtc.Value)
        {
            _selectedHistorySnapshotTimestampUtc = null;
            _selectedHistoryInterventionId = null;
            _historySelectionExpiresUtc = null;
        }

        LocalApiEventResponse? selectedIntervention = _selectedHistoryInterventionId.HasValue
            ? timeline.Interventions.FirstOrDefault(evt => evt.Id == _selectedHistoryInterventionId.Value)
            : null;
        if (selectedIntervention != null)
        {
            SetTimelineDetail(null, selectedIntervention);
            return;
        }

        LocalApiCurrentSnapshotResponse? selectedSnapshot = _selectedHistorySnapshotTimestampUtc.HasValue
            ? snapshots.FirstOrDefault(s => s.TimestampUtc == _selectedHistorySnapshotTimestampUtc.Value)
            : null;

        SetTimelineDetail(selectedSnapshot ?? snapshots.LastOrDefault(), null);
    }

    private void PopulateHistorySummary(
        LocalApiTimelineResponse? timeline,
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots)
    {
        if (timeline == null || snapshots.Count == 0)
        {
            HistoryRangeSummaryText.Text = "No data yet";
            HistoryFocusSummaryText.Text = "No data yet";
            HistoryOverloadSummaryText.Text = "No data yet";
            HistoryTrustSummaryText.Text = "No data yet";
            HistoryActionsSummaryText.Text = "No actions yet";
            HistoryGuideText.Text = "This view fills in once Ari has live timeline data for the selected range.";
            return;
        }

        LocalApiCurrentSnapshotResponse latest = snapshots[^1];
        double peakOverload = snapshots.Max(static s => Math.Clamp(s.OverloadLikelihood, 0d, 1d));
        double averageTrust = snapshots.Average(static s => Math.Clamp(s.Confidence, 0d, 1d));
        int rangeBestFocusSeconds = (int)Math.Round(
            snapshots.Max(static s => Math.Max(0d, s.SustainedAttentionDurationSec)),
            MidpointRounding.AwayFromZero);
        int retainedBestFocusSeconds = (int)Math.Round(
            GetRetainedFocusMaxSeconds(),
            MidpointRounding.AwayFromZero);
        int overloadActiveMoments = snapshots.Count(static s => s.OverloadActive || s.ActivationWindowOpen);
        int suggestions = timeline.Interventions.Count(static ev => IsSuggestionEvent(ev.Action));
        int changes = timeline.Interventions.Count(static ev => IsSystemChangeEvent(ev));

        string rangeLabel = BuildHistoryRangeLabel(timeline, snapshots);
        HistoryRangeSummaryText.Text = rangeLabel;
        HistoryFocusSummaryText.Text = _historyRange == HistoryRange.AllTime
            ? $"Now {FormatFocusedTime(latest.SustainedAttentionDurationSec)} | Best retained {FormatFocusedTime(retainedBestFocusSeconds)}"
            : $"Now {FormatFocusedTime(latest.SustainedAttentionDurationSec)} | Range best {FormatFocusedTime(rangeBestFocusSeconds)} | Retained best {FormatFocusedTime(retainedBestFocusSeconds)}";
        HistoryOverloadSummaryText.Text = $"Now {(latest.OverloadLikelihood * 100d):F0}% | Peak {(peakOverload * 100d):F0}%";
        HistoryTrustSummaryText.Text = $"Confidence {(latest.Confidence * 100d):F0}% | Avg {(averageTrust * 100d):F0}%";
        HistoryActionsSummaryText.Text = $"{suggestions} suggestions | {changes} changes | {overloadActiveMoments} active moments";
        HistoryGuideText.Text = _historyExplainMode
            ? "Technical view is on. Click any point or action marker to inspect the raw explanation for that moment."
            : "Click any point or action marker to inspect one exact moment in plain language.";
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
            double overloadPercent = Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d) * 100d;
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
            return "Oldest on the left, newest on the right";
        }

        return _historyRange switch
        {
            HistoryRange.Last1Hour => "Last hour, left to right",
            HistoryRange.Last4Hours => "Last 4 hours, left to right",
            HistoryRange.Today => $"Today from {timeline.FromUtc.ToLocalTime():HH:mm} to {timeline.ToUtc.ToLocalTime():HH:mm}",
            HistoryRange.AllTime => _retention == null
                ? "Retained history, oldest on the left"
                : $"Retained history ({_retention.RetentionDays} days), oldest on the left",
            _ => "Oldest on the left, newest on the right"
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

    private string BuildHistorySliceTooltip(LocalApiCurrentSnapshotResponse snapshot, double focusPercent)
    {
        string time = snapshot.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (_historyExplainMode)
        {
            return $"{time}{Environment.NewLine}focus_norm={focusPercent:F1}%{Environment.NewLine}overload={(snapshot.OverloadLikelihood * 100d):F1}%{Environment.NewLine}trust={(snapshot.Confidence * 100d):F1}%";
        }

        return $"{time}{Environment.NewLine}Current focus session {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}{Environment.NewLine}Overload {(snapshot.OverloadLikelihood * 100d):F0}%{Environment.NewLine}Confidence {(snapshot.Confidence * 100d):F0}%";
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
        double overload = Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d);
        double strength = active
            ? Math.Max(0.55d, overload)
            : Math.Max(0.20d, overload * 0.60d);
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
            "Overload" => $"{time} | Overload was {(Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d) * 100d):F0}%.",
            "Confidence" => $"{time} | Confidence was {(Math.Clamp(snapshot.Confidence, 0d, 1d) * 100d):F0}%.",
            "Current focus session" => $"{time} | Current focus session was {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}.",
            _ => $"{time} | Value: {normalizedValue:F2}"
        };
    }

    private void TimelineSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiCurrentSnapshotResponse snapshot)
        {
            _selectedHistoryInterventionId = null;
            _selectedHistorySnapshotTimestampUtc = snapshot.TimestampUtc;
            _historySelectionExpiresUtc = DateTimeOffset.UtcNow.Add(HistorySelectionHold);
            SetTimelineDetail(snapshot, null);
        }
    }

    private void TimelineInterventionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiEventResponse intervention)
        {
            _selectedHistorySnapshotTimestampUtc = null;
            _selectedHistoryInterventionId = intervention.Id;
            _historySelectionExpiresUtc = DateTimeOffset.UtcNow.Add(HistorySelectionHold);
            SetTimelineDetail(null, intervention);
        }
    }

    private void SetTimelineDetail(LocalApiCurrentSnapshotResponse? snapshot, LocalApiEventResponse? intervention)
    {
        if (snapshot == null && intervention == null)
        {
            TimelineDetailTimestamp.Text = $"Timestamp: {NotAvailableText}";
            TimelineDetailOverload.Text = $"Overload: {NotAvailableText}";
            TimelineDetailTrust.Text = $"Confidence: {NotAvailableText}";
            TimelineDetailFocus.Text = $"Focus streak: {NotAvailableText}";
            TimelineDetailClassification.Text = $"Moment type: {NotAvailableText}";
            TimelineDetailExecution.Text = $"Decision / execution: {NotAvailableText}";
            TimelineDetailWhyHeader.Text = $"Why: {NotAvailableText}";
            TimelineDetailWhyFiv.Text = $"Clarity/Pressure/Jitter: {NotAvailableText}";
            TimelineDetailWhyReasons.Text = $"Top reasons: {NotAvailableText}";
            TimelineDetailWhyQuality.Text = $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}";
            TimelineDetailStabilization.Text = $"Stabilization: {NotAvailableText}";
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
            TimelineDetailTimestamp.Text = $"Timestamp: {snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            TimelineDetailOverload.Text = $"Overload: {(snapshot.OverloadLikelihood * 100d):F0}% | Active: {(overloadActive ? "Yes" : "No")}";
            TimelineDetailTrust.Text = $"Confidence: {(snapshot.Confidence * 100d):F0}%";
            TimelineDetailFocus.Text = $"Focus streak: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}";
            TimelineDetailClassification.Text = "Moment type: Activity snapshot";
            TimelineDetailExecution.Text = "Decision / execution: No direct action at this exact moment. Click an action marker to inspect a suggestion or change.";
            TimelineDetailWhyHeader.Text = _historyExplainMode
                ? "Why? (Detailed)"
                : "Why?";
            TimelineDetailWhyFiv.Text = hasContributorsPayload
                ? (_historyExplainMode
                    ? $"F (Clarity): {explain.Clarity!.Value:F3}{Environment.NewLine}I (Pressure): {explain.Pressure!.Value:F3}{Environment.NewLine}V (Jitter): {explain.Jitter!.Value:F3}"
                    : BuildHumanFivSummary(explain))
                : "No detailed explanation is available for this moment.";
            TimelineDetailWhyReasons.Text = $"Top reasons: {FormatTopReasons(explain.TopReasons, _historyExplainMode)}";
            TimelineDetailWhyQuality.Text = hasContributorsPayload
                ? $"Quality: {explain.Quality} | Agreement: {explain.Agreement}"
                : $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}";
            TimelineDetailStabilization.Text = BuildStabilizationText(snapshot.StateFlagsJson, _historyExplainMode);
            return;
        }

        TimelineDetailTimestamp.Text = $"Timestamp: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TimelineDetailOverload.Text = $"Action trigger: {intervention.TriggerState}={intervention.TriggerValue:F2}";
        TimelineDetailTrust.Text = $"Confidence: {(intervention.Confidence * 100d):F0}%";
        TimelineDetailFocus.Text = $"Focus streak: {NotAvailableText} (event marker)";
        TimelineDetailClassification.Text = $"Moment type: {BuildActionClassificationText(intervention)}";
        TimelineDetailExecution.Text = $"{BuildDecisionStateText(intervention)}{Environment.NewLine}{BuildExecutionStateText(intervention)}";
        TimelineDetailWhyHeader.Text = _historyExplainMode
            ? "Why? (Detailed)"
            : "Why?";
        TimelineDetailWhyFiv.Text = string.IsNullOrWhiteSpace(intervention.Reason)
            ? $"Reason: {NotAvailableText}"
            : $"Reason: {intervention.Reason}";
        TimelineDetailWhyReasons.Text = intervention.Contributors.Count > 0
            ? $"Top reasons: {FormatTopReasons(string.Join(", ", intervention.Contributors.Take(6)), _historyExplainMode)}"
            : $"Top reasons: {NotAvailableText}";
        TimelineDetailWhyQuality.Text = $"Quality: {NotAvailableText} | Agreement: {NotAvailableText}";
        TimelineDetailStabilization.Text = BuildStabilizationText(intervention.StateFlagsJson, _historyExplainMode);
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

        NowEngineStatusText.Text = $"Status: {view.StatusLabel}";
        NowEngineStatusText.Foreground = statusBrush;
        NowEngineServiceText.Text = $"Service: {view.ServiceLabel}";
        NowEngineDatabaseText.Text = $"Database: {view.DatabaseLabel}";
        NowEngineSnapshotText.Text = $"Snapshot: {view.SnapshotLabel}";
        NowEnginePolicyText.Text = $"Policy: {view.PolicyLabel}";
        NowEngineWarningText.Text = view.WarningText;
        NowEngineWarningText.Foreground = detailBrush;

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
            CalibrationBadgeText.Text = $"Learning your pattern: {ToCalibrationStageLabel(calibrationStage)}";
            SetCalibrationBadgeVisual(calibrationStage);
        }

        string confidenceLabel = visualState.ConfidenceLabel;
        TrustBadgeText.Text = $"Confidence: {confidenceLabel}";
        SetTrustBadgeVisual(confidenceLabel);
        SectionTitleText.Foreground = new SolidColorBrush(visualState.Theme.AccentColor);
        if (_activeSection == UiSection.Overview)
        {
            SectionTitleText.Text = $"Today ({visualState.ModeLabel})";
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
        OverviewPanel.Background = tintBrush;
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
        string Classification,
        string Decision,
        string Execution,
        LocalApiEventResponse Source);

    private sealed record ChangeRow(
        string WhatChanged,
        string When,
        string Why,
        string Undo,
        string Classification,
        string Decision,
        string Execution,
        LocalApiEventResponse Source);
}

