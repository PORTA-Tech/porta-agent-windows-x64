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
using System.Windows.Media;
using System.Windows.Threading;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

public partial class MainWindow : Window
{
    private const string AgentServiceName = "AriAgent";
    private static readonly TimeSpan SlowRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManualRefreshThrottle = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan StatusFreshnessWindow = TimeSpan.FromSeconds(12);
    private const string EventCursorFileName = "control-center.events.cursor";

    private readonly LocalApiClient _apiClient = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly string _uiVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "n/a";
    private LocalApiStatusResponse? _latestStatus;
    private LocalApiCurrentSnapshotResponse? _lastSnapshot;
    private IReadOnlyList<LocalApiEventResponse> _recentEvents = [];
    private IReadOnlyList<LocalApiEventResponse> _allRecentEvents = [];
    private LocalApiTimelineResponse? _todayTimeline;
    private LocalApiTimelineResponse? _hourTimeline;
    private LocalApiTimelineResponse? _fourHourTimeline;
    private LocalApiSettingsResponse? _effectiveSettings;
    private LocalApiRetentionResponse? _retention;
    private bool _suppressSettingsEvents;
    private LocalApiEventResponse? _selectedIntervention;
    private DateTimeOffset _lastSlowRefreshUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _manualRefreshPending;
    private long _lastSeenEventId;
    private DateTimeOffset _lastManualRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastStatusSuccessUtc;
    private HistoryRange _historyRange = HistoryRange.Last1Hour;
    private bool _historyExplainMode;

    private enum UiSection
    {
        Overview,
        Timeline,
        Interventions,
        Inputs,
        States,
        Settings,
        Diagnostics
    }

    private enum HistoryRange
    {
        Last1Hour,
        Last4Hours,
        Today
    }

    public MainWindow()
    {
        InitializeComponent();
        _lastSeenEventId = LoadLastSeenEventId();
        UiVersionMenuItem.Header = $"UI: {_uiVersion}";

        ShowSection(UiSection.Overview);
        UpdateHistoryControls();
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
            _apiClient.Dispose();
            _refreshGate.Dispose();
        };
    }

    private async Task RefreshDiagnosticsAsync(bool manual = false)
    {
        if (manual)
        {
            Interlocked.Exchange(ref _manualRefreshPending, 1);
            UpdatedValue.Text = "Last refresh: manual refresh requested...";
        }

        // Prevent overlapping timer/manual refreshes from piling up and freezing the UI.
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true))
        {
            if (manual)
            {
                ControlCenterFileLog.Info("manual_refresh_queued");
                UpdatedValue.Text = "Last refresh: manual refresh queued...";
            }
            return;
        }

        try
        {
            if (manual)
            {
                RefreshButton.IsEnabled = false;
                RefreshButton.Content = "Refreshing...";
            }
            do
            {
                Interlocked.Exchange(ref _manualRefreshPending, 0);
                await RefreshDiagnosticsCoreAsync().ConfigureAwait(true);
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

    private async Task RefreshDiagnosticsCoreAsync()
    {
        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        LocalApiRuntimeSettings? runtimeSettings = LocalApiRuntimeSettingsStore.Load();
        ServiceProbeResult serviceProbe = ProbeServiceStatus();
        _latestStatus = await _apiClient.GetStatusAsync().ConfigureAwait(true);
        if (_latestStatus != null)
        {
            _lastStatusSuccessUtc = DateTimeOffset.UtcNow;
        }

        bool statusFresh = _latestStatus != null
            && _lastStatusSuccessUtc.HasValue
            && (DateTimeOffset.UtcNow - _lastStatusSuccessUtc.Value) <= StatusFreshnessWindow
            && (DateTimeOffset.UtcNow - _latestStatus.LastUpdatedUtc) <= StatusFreshnessWindow;

        bool apiReachable = _latestStatus != null;
        LocalApiCurrentSnapshotResponse? previousSnapshot = _lastSnapshot;
        LocalApiCurrentSnapshotResponse? snapshot = apiReachable
            ? await _apiClient.GetCurrentSnapshotAsync().ConfigureAwait(true)
            : null;
        LocalApiInputsSummaryResponse? inputSummary = apiReachable
            ? await _apiClient.GetInputsSummaryAsync(60).ConfigureAwait(true)
            : null;
        if (apiReachable)
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
        else
        {
            _allRecentEvents = [];
        }

        _recentEvents = _allRecentEvents.Take(5).ToArray();
        _lastSnapshot = snapshot;
        NowTruth nowTruth = BuildNowTruth(serviceProbe, runtimeSettings, _latestStatus, snapshot, statusFresh);

        bool needsSlowRefresh = apiReachable
            && (DateTimeOffset.UtcNow - _lastSlowRefreshUtc) >= SlowRefreshInterval;
        if (needsSlowRefresh)
        {
            DateTimeOffset fromUtc = DateTimeOffset.UtcNow.Date;
            _todayTimeline = await _apiClient.GetTimelineAsync(fromUtc, DateTimeOffset.UtcNow, 60).ConfigureAwait(true);
            _hourTimeline = await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 5).ConfigureAwait(true);
            _fourHourTimeline = await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-4), DateTimeOffset.UtcNow, 15).ConfigureAwait(true);
            _effectiveSettings = await _apiClient.GetEffectiveSettingsAsync().ConfigureAwait(true);
            _retention = await _apiClient.GetDataRetentionAsync().ConfigureAwait(true);
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

        if (_latestStatus == null)
        {
            ConsentActiveValue.Text = "Unknown";
            BackendConnectedValue.Text = "Unknown";
            FocusStrengthValue.Text = "Unknown";
            OverloadValue.Text = "Unknown";
            LastActionValue.Text = "Unknown";
            LastEventValue.Text = "Unknown";
            PolicyVersionValue.Text = "Unknown";
            SchemaVersionValue.Text = "Unknown";
            AgentVersionValue.Text = "Unknown";
            ConsentVersionValue.Text = "Unknown";
            ConsentTimestampValue.Text = "Unknown";
            ConsentHashValue.Text = "Unknown";
            LastErrorValue.Text = BuildOfflineErrorMessage(serviceProbe, endpoint);
            SnapshotConfidenceValue.Text = "Unknown";
            OverloadActiveValue.Text = "Unknown";
            SustainedAttentionValue.Text = "Unknown";
            IdleFragmentationValue.Text = "Unknown";
            QuickPauseResumeButton.Content = "Pause";
            QuickPauseResumeButton.IsEnabled = false;
            ConsentOverlay.Visibility = Visibility.Collapsed;
            UpdateTopBar(serviceProbe, runtimeSettings, null, nowTruth);
            PopulateOverview(nowTruth, null, [], previousSnapshot);
            PopulateInputs(null, null);
            PopulateInterventions([], null);
            PopulateSettings(null);
            PopulateStates(null);
            PopulateTimeline(null);
            UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
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
            FocusStrengthValue.Text = "Unknown";
            OverloadValue.Text = "Unknown";
            LastActionValue.Text = "Unknown";
        }

        LastEventValue.Text = _latestStatus.LastEventTimestampUtc?.ToString("O") ?? "n/a";
        PolicyVersionValue.Text = _latestStatus.PolicyVersion;
        SchemaVersionValue.Text = _latestStatus.SchemaVersion;
        AgentVersionValue.Text = _latestStatus.AgentVersion;
        ConsentVersionValue.Text = _latestStatus.ConsentVersion;
        ConsentTimestampValue.Text = _latestStatus.ConsentTimestampUtc?.ToString("O") ?? "n/a";
        ConsentHashValue.Text = _latestStatus.ConsentDisclosureHash;
        LastErrorValue.Text = string.IsNullOrWhiteSpace(nowTruth.Explanation)
            ? BuildOnlineErrorMessage(_latestStatus.LastError, serviceProbe, statusFresh)
            : nowTruth.Explanation;
        SnapshotConfidenceValue.Text = truthSnapshot != null
            ? truthSnapshot.Confidence.ToString("F2", CultureInfo.InvariantCulture)
            : "n/a";
        OverloadActiveValue.Text = truthSnapshot != null
            ? (truthSnapshot.OverloadActive ? "Yes" : "No")
            : "n/a";
        SustainedAttentionValue.Text = truthSnapshot != null
            ? truthSnapshot.SustainedAttentionDurationSec.ToString("F0", CultureInfo.InvariantCulture)
            : "n/a";
        IdleFragmentationValue.Text = truthSnapshot != null
            ? truthSnapshot.IdleFragmentationIndex.ToString("F2", CultureInfo.InvariantCulture)
            : "n/a";
        QuickPauseResumeButton.Content = _latestStatus.Paused ? "Resume" : "Pause";
        QuickPauseResumeButton.IsEnabled = true;
        ConsentOverlay.Visibility = consentMissing ? Visibility.Visible : Visibility.Collapsed;
        UpdateTopBar(serviceProbe, runtimeSettings, _latestStatus, nowTruth);

        PopulateOverview(nowTruth, inputSummary, _recentEvents, previousSnapshot);
        PopulateInputs(inputSummary, _retention);
        PopulateInterventions(_allRecentEvents, _effectiveSettings);
        PopulateSettings(_effectiveSettings);
        PopulateStates(truthSnapshot);
        PopulateTimeline(GetSelectedHistoryTimeline());
        UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
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
        ControlCenterFileLog.Info($"manual_refresh_completed api_reachable={(_latestStatus != null).ToString().ToLowerInvariant()}");
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

    private void HistoryExplainModeCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _historyExplainMode = HistoryExplainModeCheckBox.IsChecked == true;
        PopulateTimeline(GetSelectedHistoryTimeline());
    }

    private void InterventionsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Interventions);
    }

    private void InputsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Inputs);
    }

    private void StatesNavButton_Click(object sender, RoutedEventArgs e)
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
            || Process.GetProcessesByName("Porta.Tray").Length > 0;
    }

    private void ShowSection(UiSection section)
    {
        OverviewPanel.Visibility = section == UiSection.Overview ? Visibility.Visible : Visibility.Collapsed;
        TimelinePanel.Visibility = section == UiSection.Timeline ? Visibility.Visible : Visibility.Collapsed;
        InterventionsPanel.Visibility = section == UiSection.Interventions ? Visibility.Visible : Visibility.Collapsed;
        InputsPanel.Visibility = section == UiSection.Inputs ? Visibility.Visible : Visibility.Collapsed;
        StatesPanel.Visibility = section == UiSection.States ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == UiSection.Settings ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPanel.Visibility = section == UiSection.Diagnostics ? Visibility.Visible : Visibility.Collapsed;

        SectionTitleText.Text = section switch
        {
            UiSection.Overview => "Now",
            UiSection.Timeline => "History",
            UiSection.Interventions => "Actions",
            UiSection.Inputs => "What I Track",
            UiSection.States => "How It Decides",
            UiSection.Settings => "Controls",
            UiSection.Diagnostics => "Health",
            _ => "Now"
        };

        ApplyNavState(OverviewNavButton, section == UiSection.Overview);
        ApplyNavState(TimelineNavButton, section == UiSection.Timeline);
        ApplyNavState(InterventionsNavButton, section == UiSection.Interventions);
        ApplyNavState(InputsNavButton, section == UiSection.Inputs);
        ApplyNavState(StatesNavButton, section == UiSection.States);
        ApplyNavState(SettingsNavButton, section == UiSection.Settings);
        ApplyNavState(DiagnosticsNavButton, section == UiSection.Diagnostics);
    }

    private static void ApplyNavState(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
            : new SolidColorBrush(Color.FromRgb(31, 41, 55));
        button.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(147, 197, 253))
            : new SolidColorBrush(Color.FromRgb(51, 65, 85));
    }

    private LocalApiTimelineResponse? GetSelectedHistoryTimeline()
    {
        return _historyRange switch
        {
            HistoryRange.Last1Hour => _hourTimeline,
            HistoryRange.Last4Hours => _fourHourTimeline,
            HistoryRange.Today => _todayTimeline,
            _ => _hourTimeline
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
    }

    private static void ApplyHistoryButtonState(Button button, bool selected)
    {
        if (button == null)
        {
            return;
        }

        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
            : new SolidColorBrush(Color.FromRgb(31, 41, 55));
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(147, 197, 253))
            : new SolidColorBrush(Color.FromRgb(51, 65, 85));
        button.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240));
    }

    private void PopulateOverview(
        NowTruth nowTruth,
        LocalApiInputsSummaryResponse? inputSummary,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
        PopulateNowCards(nowTruth, recentEvents);
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
            TodayMostVolatileHourValue.Text = "n/a";
            RenderHourlyChart(null);
            return;
        }

        double confidence = snapshot.Confidence;
        SetStateCard(FocusCardValue, FocusCardConfidence, FocusCardTrend, FocusCardBand, snapshot.FocusStrength, confidence, "focus", previousSnapshot);
        SetStateCard(OverloadCardValue, OverloadCardConfidence, OverloadCardTrend, OverloadCardBand, snapshot.OverloadLikelihood, confidence, "overload", previousSnapshot);
        SetStateCard(FatigueCardValue, FatigueCardConfidence, FatigueCardTrend, FatigueCardBand, snapshot.FatigueProbability, confidence, "fatigue", previousSnapshot);
        SetStateCard(StabilityCardValue, StabilityCardConfidence, StabilityCardTrend, StabilityCardBand, snapshot.CognitiveStability, confidence, "stability", previousSnapshot);

        RenderRecentActions(recentEvents);
        UpdateTodaySummary(inputSummary);
    }

    private void PopulateNowCards(NowTruth nowTruth, IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        LocalApiCurrentSnapshotResponse? snapshot = nowTruth.Snapshot;
        if (snapshot == null)
        {
            NowOverloadValueText.Text = "Overload: --";
            NowOverloadActiveText.Text = "Active: --";
            NowOverloadTrustText.Text = "Trust level: --";
            NowFocusStreakValueText.Text = "Focused time: --";
            NowFocusChoppinessText.Text = "Choppiness: --";
            NowFocusFootnoteText.Text = "Resets after 2+ minutes away.";
            NowFocusStatusText.Text = nowTruth.Explanation;
            NowFocusStatusText.Visibility = string.IsNullOrWhiteSpace(nowTruth.Explanation)
                ? Visibility.Collapsed
                : Visibility.Visible;
            NowOverloadFivText.Text = "Explain not available (backend field missing).";
            NowOverloadTopReasonsText.Text = "Top reasons: --";
            NowOverloadQualityAgreementText.Text = "Quality: -- | Agreement: --";

            NowCalibrationStatusText.Text = "Calibrating...";
            NowCalibrationProgressText.Text = "Progress: --";
            NowCalibrationQualityText.Text = "Recent data quality: -- (last 2 min)";
            NowCalibrationColdStartText.Text = "Cold start: --";
            NowCalibrationTrustCapText.Visibility = Visibility.Visible;

            NowDataQualityScoreText.Text = "Quality score: --";
            NowDataQualityPartialText.Text = "Partial data: --";
            NowDataQualityMissingText.Visibility = Visibility.Collapsed;

            NowLastActionTitleText.Text = "No actions recently";
            NowLastActionTimeText.Text = "Time: --";
            NowOpenActionsButton.IsEnabled = false;

            NowWhatPortaDoingText.Text = nowTruth.Explanation;
            return;
        }

        NowOverloadValueText.Text = $"Overload {(snapshot.OverloadLikelihood * 100d):F0}%";
        NowOverloadActiveText.Text = $"Active: {(snapshot.OverloadActive ? "Yes" : "No")}";
        NowOverloadTrustText.Text = $"Trust level: {(snapshot.Confidence * 100d):F0}%";
        NowFocusStreakValueText.Text = $"Focused time: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}";
        NowFocusChoppinessText.Text = $"Choppiness: {MapChoppiness(snapshot.IdleFragmentationIndex)} ({snapshot.IdleFragmentationIndex:F2})";
        NowFocusFootnoteText.Text = "Resets after 2+ minutes away.";
        string focusStatus = BuildFocusStatusMessage(snapshot);
        NowFocusStatusText.Text = focusStatus;
        NowFocusStatusText.Visibility = string.IsNullOrWhiteSpace(focusStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;

        OverloadExplainView explain = ParseOverloadExplain(snapshot.ContributorsJson);
        NowOverloadFivText.Text = explain.FivSummary;
        NowOverloadTopReasonsText.Text = $"Top reasons: {explain.TopReasons}";
        NowOverloadQualityAgreementText.Text = $"Quality: {explain.Quality} | Agreement: {explain.Agreement}";

        bool calibrationReady = snapshot.BaselineReady == true;
        NowCalibrationStatusText.Text = calibrationReady ? "Ready" : "Calibrating...";
        NowCalibrationProgressText.Text = snapshot.BaselineReadyPct.HasValue
            ? $"Progress: {(snapshot.BaselineReadyPct.Value * 100d):F0}% metrics ready"
            : "Progress: n/a";
        NowCalibrationQualityText.Text = snapshot.AvgQuality2m.HasValue
            ? $"Recent data quality: {snapshot.AvgQuality2m.Value:F2} (last 2 min)"
            : "Recent data quality: n/a (last 2 min)";
        NowCalibrationColdStartText.Text = $"Cold start: {MapOnOff(snapshot.ColdStart)}";
        NowCalibrationTrustCapText.Visibility = calibrationReady ? Visibility.Collapsed : Visibility.Visible;

        NowDataQualityScoreText.Text = snapshot.CurrentQualityScore.HasValue
            ? $"Quality score: {snapshot.CurrentQualityScore.Value:F2}"
            : "Quality score: n/a";
        NowDataQualityPartialText.Text = $"Partial data: {MapYesNo(snapshot.PartialData)}";
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
            NowLastActionTimeText.Text = "Time: n/a";
            NowOpenActionsButton.IsEnabled = false;
        }
        else
        {
            NowLastActionTitleText.Text = ToActionTitle(lastActionEvent);
            NowLastActionTimeText.Text = $"Time: {lastActionEvent.TsUtc.ToLocalTime():HH:mm:ss}";
            NowOpenActionsButton.IsEnabled = true;
        }

        NowWhatPortaDoingText.Text = BuildNowDoingSentence(snapshot);
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
            return "n/a";
        }

        return value.Value ? "On" : "Off";
    }

    private static string MapYesNo(bool? value)
    {
        if (!value.HasValue)
        {
            return "n/a";
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

        if (_effectiveSettings != null
            && !_effectiveSettings.NotificationReductionEnabled
            && !_effectiveSettings.FocusModeEnabled
            && !_effectiveSettings.AnimationReductionEnabled
            && !_effectiveSettings.TemporaryAppRestrictionEnabled)
        {
            return "Watching only (no actions enabled)";
        }

        if (snapshot.BaselineReady == false)
        {
            return "Calibrating before suggesting actions.";
        }

        return "Ready to suggest breaks when needed";
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
                : "n/a";
            string agreementText = TryReadDouble(root, "agreement", out double agreement)
                ? agreement.ToString("F2", CultureInfo.InvariantCulture)
                : "n/a";

            return new OverloadExplainView(
                FivSummary: fivSummary,
                TopReasons: topReasons,
                Quality: qualityText,
                Agreement: agreementText,
                Clarity: clarity,
                Pressure: pressure,
                Jitter: jitter);
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
            return "Why: explain details not available.";
        }

        return $"Clarity: {ToBandLabel(explain.Clarity.Value)} | Pressure: {ToBandLabel(explain.Pressure.Value)} | Jitter: {ToBandLabel(explain.Jitter.Value)}";
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
            valueText.Text = "Unknown";
            confidenceText.Text = "Confidence: Unknown";
            trendText.Text = "Trend: -";
            intensityBand.Background = new SolidColorBrush(Color.FromRgb(71, 85, 105));
            return;
        }

        valueText.Text = value.Value.ToString("F2", CultureInfo.InvariantCulture);
        confidenceText.Text = confidence.HasValue
            ? $"Confidence: {Math.Clamp(confidence.Value * 100d, 0d, 100d):F0}%"
            : "Confidence: Unknown";

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
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PortaAgent", EventCursorFileName);
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
                : "n/a";
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
            return "n/a";
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
            return "n/a";
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

    private void PopulateInputs(LocalApiInputsSummaryResponse? inputsSummary, LocalApiRetentionResponse? retention)
    {
        InputTypingValue.Text = FormatMetric(inputsSummary?.TypingBurstIntensity);
        InputMouseValue.Text = "n/a";
        InputScrollValue.Text = FormatMetric(inputsSummary?.ScrollVelocityIndex);
        InputFocusSwitchValue.Text = FormatMetric(inputsSummary?.FocusSwitchRate);
        InputIdleFragmentationValue.Text = FormatMetric(inputsSummary?.IdleFragmentationIndex);
        InputNotificationLatencyValue.Text = FormatMetric(inputsSummary?.NotificationLatencyIndex);

        if (retention == null)
        {
            RetentionWindowValue.Text = "Retention window: Unknown";
            RetentionSizeValue.Text = "Local data size: Unknown";
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

        InterventionsListView.ItemsSource = events
            .Select(e => new InterventionRow(
                e.Action,
                e.PolicyId,
                e.Domain,
                e.TriggerState,
                e.TriggerValue.ToString("F2", CultureInfo.InvariantCulture),
                $"{(e.Confidence * 100d):F0}%",
                e.DurationSeconds.HasValue ? $"{Math.Max(e.DurationSeconds.Value, 0)}s" : "n/a",
                e.EscalationLevel,
                e.LifecycleState,
                e.Reversible ? "Yes" : "No",
                e))
            .ToList();

        if (events.Count == 0)
        {
            _selectedIntervention = null;
            UpdateInterventionDrawer(null);
        }
    }

    private void InterventionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InterventionsListView.SelectedItem is InterventionRow row)
        {
            _selectedIntervention = row.Source;
            UpdateInterventionDrawer(row.Source);
            return;
        }

        _selectedIntervention = null;
        UpdateInterventionDrawer(null);
    }

    private void UpdateInterventionDrawer(LocalApiEventResponse? item)
    {
        if (item == null)
        {
            InterventionDrawerTitle.Text = "Select an intervention";
            InterventionDrawerIdentity.Text = "Intervention ID: n/a";
            InterventionDrawerPolicy.Text = "Policy/Domain: n/a";
            InterventionDrawerLifecycle.Text = "Lifecycle: n/a";
            InterventionDrawerWhen.Text = "When: n/a";
            InterventionDrawerPlanned.Text = "Planned end: n/a";
            InterventionDrawerActual.Text = "Actual end: n/a";
            InterventionDrawerCooldown.Text = "Cooldown end: n/a";
            InterventionDrawerReversal.Text = "Reversal applied: n/a";
            InterventionDrawerWhy.Text = "Why: n/a";
            InterventionDrawerContributors.Text = "Contributors: n/a";
            InterventionDrawerConfidence.Text = "Confidence: n/a";
            InterventionDrawerDuration.Text = "Duration: n/a";
            InterventionDrawerChanged.Text = "What changed: n/a";
            InterventionDrawerPrevious.Text = "Previous value: n/a";
            InterventionDrawerUndo.Text = "How to undo: n/a";
            InterventionToggleButton.IsEnabled = false;
            return;
        }

        string duration = item.DurationSeconds.HasValue ? $"{Math.Max(item.DurationSeconds.Value, 0)}s" : "n/a";
        InterventionDrawerTitle.Text = item.Action;
        InterventionDrawerIdentity.Text = $"Intervention ID: {item.InterventionId}";
        InterventionDrawerPolicy.Text = $"Policy/Domain: {item.PolicyId} / {item.Domain}";
        InterventionDrawerLifecycle.Text = $"Lifecycle: {item.LifecycleState}";
        InterventionDrawerWhen.Text = $"When: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        InterventionDrawerPlanned.Text = $"Planned end: {(item.PlannedEndTimestampUtc.HasValue ? item.PlannedEndTimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "n/a")}";
        InterventionDrawerActual.Text = $"Actual end: {(item.ActualEndTimestampUtc.HasValue ? item.ActualEndTimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "n/a")}";
        InterventionDrawerCooldown.Text = $"Cooldown end: {(item.CooldownEndTimestampUtc.HasValue ? item.CooldownEndTimestampUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "n/a")}";
        InterventionDrawerReversal.Text = $"Reversal applied: {(item.ReversalApplied ? "Yes" : "No")}";
        InterventionDrawerWhy.Text = $"Why: {item.Reason}";
        InterventionDrawerContributors.Text = item.Contributors.Count > 0
            ? $"Contributors: {string.Join(", ", item.Contributors.Take(5))}"
            : "Contributors: n/a";
        InterventionDrawerConfidence.Text = $"Confidence: {(item.Confidence * 100d):F0}%";
        InterventionDrawerDuration.Text = $"Duration: {duration}";
        InterventionDrawerChanged.Text = $"What changed: {(string.IsNullOrWhiteSpace(item.WhatChanged) ? item.Action : item.WhatChanged)}";
        InterventionDrawerPrevious.Text = $"Previous value: {FormatPreviousValue(item.PreviousSystemStateJson)}";
        InterventionDrawerUndo.Text = $"How to undo: {(string.IsNullOrWhiteSpace(item.HowToUndo) ? "n/a" : item.HowToUndo)}";
        InterventionToggleButton.IsEnabled = item.Reversible;
    }

    private async void InterventionCategoryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents)
        {
            return;
        }

        await SaveSettingsAsync("intervention_category_toggle").ConfigureAwait(true);
    }

    private async void InterventionToggleButton_Click(object sender, RoutedEventArgs e)
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

    private void PopulateSettings(LocalApiSettingsResponse? settings)
    {
        _suppressSettingsEvents = true;
        if (settings == null)
        {
            IntensitySlider.Value = 1;
            SelectPrivacyLevel("Standard");
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
        SettingsStatusText.Text = $"Settings status: updated {settings.UpdatedAtUtc.ToLocalTime():HH:mm:ss}";
        _suppressSettingsEvents = false;
        UpdateIntensityLabel();
    }

    private void IntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateIntensityLabel();
    }

    private void UpdateIntensityLabel()
    {
        IntensityValueText.Text = IntensitySlider.Value switch
        {
            <= 0.5 => "Gentle",
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
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync("settings_save").ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync(string reason)
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

        _effectiveSettings = response;
        PopulateSettings(response);
        SettingsStatusText.Text = $"Settings status: saved ({reason})";
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

        DateTime until = PauseUntilDatePicker.SelectedDate.Value.Date.AddHours(8);
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

    private void PopulateStates(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot == null)
        {
            FocusStateConfidence.Text = "Confidence: n/a";
            OverloadStateConfidence.Text = "Confidence: n/a";
            FatigueStateConfidence.Text = "Confidence: n/a";
            StabilityStateConfidence.Text = "Confidence: n/a";
            FocusStateContributors.Text = "Contributors: n/a";
            OverloadStateContributors.Text = "Contributors: n/a";
            FatigueStateContributors.Text = "Contributors: n/a";
            StabilityStateContributors.Text = "Contributors: n/a";
            return;
        }

        string confidenceText = $"Confidence: {(snapshot.Confidence * 100d):F0}%";
        string contributors = snapshot.Contributors.Count > 0
            ? string.Join(", ", snapshot.Contributors.Take(6))
            : "n/a";
        FocusStateConfidence.Text = confidenceText;
        OverloadStateConfidence.Text = confidenceText;
        FatigueStateConfidence.Text = confidenceText;
        StabilityStateConfidence.Text = confidenceText;
        FocusStateContributors.Text = $"Contributors: {contributors}";
        OverloadStateContributors.Text = $"Contributors: {contributors}";
        FatigueStateContributors.Text = $"Contributors: {contributors}";
        StabilityStateContributors.Text = $"Contributors: {contributors}";
    }

    private void PopulateTimeline(LocalApiTimelineResponse? timeline)
    {
        UpdateHistoryControls();
        TimelineOverloadTrackPanel.Children.Clear();
        TimelineTrustTrackPanel.Children.Clear();
        TimelineFocusTrackPanel.Children.Clear();
        TimelineOverloadActiveOverlayPanel.Children.Clear();
        TimelineActionMarkersPanel.Children.Clear();

        if (timeline == null || timeline.Snapshots.Count == 0)
        {
            TimelineOverloadTrackPanel.Children.Add(new TextBlock { Text = "No timeline data." });
            TimelineTrustTrackPanel.Children.Add(new TextBlock { Text = "No timeline data." });
            TimelineFocusTrackPanel.Children.Add(new TextBlock { Text = "No timeline data." });
            TimelineOverloadActiveOverlayPanel.Children.Add(new TextBlock { Text = "No overload-active data." });
            TimelineActionMarkersPanel.Children.Add(new TextBlock { Text = "No actions." });
            SetTimelineDetail(null, null);
            return;
        }

        int maxPoints = _historyRange switch
        {
            HistoryRange.Last1Hour => 360,
            HistoryRange.Last4Hours => 480,
            _ => 600
        };
        IReadOnlyList<LocalApiCurrentSnapshotResponse> snapshots = DownsampleSnapshots(timeline.Snapshots, maxPoints);

        foreach (LocalApiCurrentSnapshotResponse snapshot in snapshots)
        {
            TimelineOverloadTrackPanel.Children.Add(BuildTrackPoint(snapshot, snapshot.OverloadLikelihood, Color.FromRgb(239, 68, 68), "Overload"));
            TimelineTrustTrackPanel.Children.Add(BuildTrackPoint(snapshot, snapshot.Confidence, Color.FromRgb(59, 130, 246), "Trust"));
            double focusNormalized = Math.Clamp(snapshot.SustainedAttentionDurationSec / 3600d, 0d, 1d);
            TimelineFocusTrackPanel.Children.Add(BuildTrackPoint(snapshot, focusNormalized, Color.FromRgb(34, 197, 94), "Focus streak"));
            TimelineOverloadActiveOverlayPanel.Children.Add(BuildOverloadActiveMarker(snapshot));
        }

        foreach (LocalApiEventResponse intervention in timeline.Interventions
                     .OrderBy(static evt => evt.TimestampUtc)
                     .TakeLast(120))
        {
            TimelineActionMarkersPanel.Children.Add(BuildInterventionMarker(intervention));
        }

        SetTimelineDetail(snapshots.LastOrDefault(), null);
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

    private UIElement BuildTrackPoint(
        LocalApiCurrentSnapshotResponse snapshot,
        double value,
        Color color,
        string label)
    {
        double clamped = Math.Clamp(value, 0d, 1d);

        var button = new Button
        {
            Width = 8,
            Height = Math.Max(6d, 40d * clamped),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Tag = snapshot,
            Background = new SolidColorBrush(color),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        };
        button.ToolTip = $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | {label}: {clamped:F2}";
        button.Click += TimelineSnapshotButton_Click;
        return button;
    }

    private UIElement BuildOverloadActiveMarker(LocalApiCurrentSnapshotResponse snapshot)
    {
        bool active = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
        var button = new Button
        {
            Width = 8,
            Height = 16,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = snapshot,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(180, 220, 38, 38))
                : Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(0.5)
        };
        button.ToolTip = active
            ? $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | Overload Active"
            : $"{snapshot.TimestampUtc.ToLocalTime():HH:mm:ss} | Overload Inactive";
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
                ? Math.Clamp(intervention.DurationSeconds.Value / 12d, 12d, 40d)
                : 14d,
            Height = 22,
            Margin = new Thickness(2, 0, 2, 0),
            Tag = intervention,
            Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        };
        button.Click += TimelineInterventionButton_Click;
        return button;
    }

    private void TimelineSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiCurrentSnapshotResponse snapshot)
        {
            SetTimelineDetail(snapshot, null);
        }
    }

    private void TimelineInterventionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is LocalApiEventResponse intervention)
        {
            SetTimelineDetail(null, intervention);
        }
    }

    private void SetTimelineDetail(LocalApiCurrentSnapshotResponse? snapshot, LocalApiEventResponse? intervention)
    {
        if (snapshot == null && intervention == null)
        {
            TimelineDetailTimestamp.Text = "Timestamp: n/a";
            TimelineDetailOverload.Text = "Overload: n/a";
            TimelineDetailTrust.Text = "Trust level: n/a";
            TimelineDetailFocus.Text = "Focus streak: n/a";
            TimelineDetailWhyHeader.Text = "Why: n/a";
            TimelineDetailWhyFiv.Text = "Clarity/Pressure/Jitter: n/a";
            TimelineDetailWhyReasons.Text = "Top reasons: n/a";
            TimelineDetailWhyQuality.Text = "Quality: n/a | Agreement: n/a";
            TimelineDetailStabilization.Text = "Stabilization: n/a";
            TimelineDetailAction.Text = "Action: n/a";
            return;
        }

        if (snapshot != null)
        {
            OverloadExplainView explain = ParseOverloadExplain(snapshot.ContributorsJson);
            bool overloadActive = snapshot.OverloadActive || snapshot.ActivationWindowOpen;
            TimelineDetailTimestamp.Text = $"Timestamp: {snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            TimelineDetailOverload.Text = $"Overload: {(snapshot.OverloadLikelihood * 100d):F0}% | Active: {(overloadActive ? "Yes" : "No")}";
            TimelineDetailTrust.Text = $"Trust level: {(snapshot.Confidence * 100d):F0}%";
            TimelineDetailFocus.Text = $"Focus streak: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}";
            TimelineDetailWhyHeader.Text = _historyExplainMode ? "Why? (Explain mode)" : "Why?";
            TimelineDetailWhyFiv.Text = _historyExplainMode
                ? explain.FivSummary
                : BuildHumanFivSummary(explain);
            TimelineDetailWhyReasons.Text = $"Top reasons: {explain.TopReasons}";
            TimelineDetailWhyQuality.Text = $"Quality: {explain.Quality} | Agreement: {explain.Agreement}";
            TimelineDetailStabilization.Text = BuildStabilizationText(snapshot.StateFlagsJson);
            TimelineDetailAction.Text = string.IsNullOrWhiteSpace(snapshot.LastAction)
                ? "Action: n/a"
                : $"Action: {snapshot.LastAction}";
            return;
        }

        TimelineDetailTimestamp.Text = $"Timestamp: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TimelineDetailOverload.Text = $"Action trigger: {intervention.TriggerState}={intervention.TriggerValue:F2}";
        TimelineDetailTrust.Text = $"Trust level: {(intervention.Confidence * 100d):F0}%";
        TimelineDetailFocus.Text = "Focus streak: n/a (event marker)";
        TimelineDetailWhyHeader.Text = "Why?";
        TimelineDetailWhyFiv.Text = string.IsNullOrWhiteSpace(intervention.Reason)
            ? "Reason: n/a"
            : $"Reason: {intervention.Reason}";
        TimelineDetailWhyReasons.Text = intervention.Contributors.Count > 0
            ? $"Top reasons: {string.Join(", ", intervention.Contributors.Take(6))}"
            : "Top reasons: n/a";
        TimelineDetailWhyQuality.Text = "Quality: n/a | Agreement: n/a";
        TimelineDetailStabilization.Text = "Stabilization: n/a";
        TimelineDetailAction.Text = $"Action: {(string.IsNullOrWhiteSpace(intervention.WhatChanged) ? intervention.Action : intervention.WhatChanged)}";
    }

    private static string BuildStabilizationText(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return "Stabilization: ON timer n/a / 120s";
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            double dwellOnSeconds = 0d;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("dwell_on_ms", out JsonElement dwellOnElem)
                && dwellOnElem.ValueKind == JsonValueKind.Number
                && dwellOnElem.TryGetDouble(out double dwellOnMs))
            {
                dwellOnSeconds = Math.Max(0d, dwellOnMs / 1000d);
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

            return $"Stabilization: ON timer {dwellOnSeconds:F0}s / 120s{(active ? " (active)" : string.Empty)}";
        }
        catch
        {
            return "Stabilization: ON timer n/a / 120s";
        }
    }

    private static string FormatPreviousValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "n/a";
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
            : "Unknown";
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
        NowTruth nowTruth)
    {
        string runState = status == null
            ? (serviceProbe.Running ? "Running" : "Paused")
            : (status.Paused ? "Paused" : "Running");
        StatusPillText.Text = runState;
        SetBadgeVisual(StatusPillBorder, isPositive: runState == "Running");

        bool consentOn = status?.ConsentActive == true;
        ConsentBadgeText.Text = $"Consent: {(consentOn ? "On" : "Needed")}";
        SetBadgeVisual(ConsentBadgeBorder, isPositive: consentOn);

        bool calibrationReady = nowTruth.Snapshot?.BaselineReady
            ?? (status != null && (_effectiveSettings?.BaselineRecalibrationActive != true));
        CalibrationBadgeText.Text = $"Calibration: {(calibrationReady ? "Ready" : "Calibrating")}";
        SetBadgeVisual(CalibrationBadgeBorder, isPositive: calibrationReady);

        string trustLabel = MapTrustLabel(nowTruth.Snapshot?.Confidence);
        TrustBadgeText.Text = $"Trust: {trustLabel}";
        SetTrustBadgeVisual(trustLabel);

        bool statusFresh = status != null
            && _lastStatusSuccessUtc.HasValue
            && (DateTimeOffset.UtcNow - _lastStatusSuccessUtc.Value) <= StatusFreshnessWindow
            && (DateTimeOffset.UtcNow - status.LastUpdatedUtc) <= StatusFreshnessWindow;
        string? hint = BuildTopBarHint(serviceProbe, runtimeSettings, status, statusFresh);
        TopBarHintText.Text = hint ?? string.Empty;
        TopBarHintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

        AgentVersionMenuItem.Header = $"Agent: {status?.AgentVersion ?? "n/a"}";
        UiVersionMenuItem.Header = $"UI: {_uiVersion}";
        SchemaVersionMenuItem.Header = $"Schema: {status?.SchemaVersion ?? "n/a"}";
    }

    private static void SetBadgeVisual(Border border, bool isPositive)
    {
        border.Background = isPositive
            ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
            : new SolidColorBrush(Color.FromRgb(127, 29, 29));
    }

    private void SetTrustBadgeVisual(string trustLabel)
    {
        TrustBadgeBorder.Background = trustLabel switch
        {
            "High" => new SolidColorBrush(Color.FromRgb(6, 95, 70)),
            "Medium" => new SolidColorBrush(Color.FromRgb(146, 64, 14)),
            "--" => new SolidColorBrush(Color.FromRgb(51, 65, 85)),
            _ => new SolidColorBrush(Color.FromRgb(127, 29, 29))
        };
    }

    private static string MapTrustLabel(double? confidence)
    {
        if (!confidence.HasValue)
        {
            return "--";
        }

        if (confidence.Value >= 0.85d)
        {
            return "High";
        }

        return confidence.Value >= 0.65d ? "Medium" : "Low";
    }

    private static string? BuildTopBarHint(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        LocalApiStatusResponse? status,
        bool statusFresh)
    {
        if (status == null)
        {
            if (runtimeSettings is { LocalApiAuthReady: false })
            {
                return $"Local API auth error: {runtimeSettings.LocalApiAuthError ?? "TOKEN_READ_FAILED"}";
            }

            return serviceProbe.Running
                ? "Not updating: Local API unreachable"
                : "Not updating: Service offline";
        }

        if (!status.LocalApiAuthReady)
        {
            return $"Local API auth error: {status.LocalApiAuthError ?? "TOKEN_READ_FAILED"}";
        }

        if (!statusFresh)
        {
            return "Not updating: Status stale";
        }

        if (!status.ConsentActive)
        {
            return "Not collecting: Consent needed";
        }

        if (IsMigrationFailure(status.LastError))
        {
            return "Not updating: Database migration failed";
        }

        return null;
    }

    private static NowTruth BuildNowTruth(
        ServiceProbeResult serviceProbe,
        LocalApiRuntimeSettings? runtimeSettings,
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        bool statusFresh)
    {
        if (status == null)
        {
            if (runtimeSettings is { LocalApiAuthReady: false })
            {
                return new NowTruth(
                    Snapshot: null,
                    Explanation: $"Local API auth error: {runtimeSettings.LocalApiAuthError ?? "TOKEN_READ_FAILED"}");
            }

            string explanation = serviceProbe.Running
                ? "Not updating: Local API unreachable"
                : "Not updating: Service offline";
            return new NowTruth(
                Snapshot: null,
                Explanation: explanation);
        }

        if (!status.LocalApiAuthReady)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: $"Local API auth error: {status.LocalApiAuthError ?? "TOKEN_READ_FAILED"}");
        }

        if (!statusFresh)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: "Not updating: Status stale");
        }

        if (snapshot == null)
        {
            return new NowTruth(
                Snapshot: null,
                Explanation: "Not updating: Snapshot unavailable");
        }

        return new NowTruth(
            Snapshot: snapshot,
            Explanation: string.Empty);
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

    private static string BuildOfflineErrorMessage(ServiceProbeResult serviceProbe, LocalApiEndpointSettings? endpoint)
    {
        string endpointText = endpoint == null
            ? "endpoint=missing"
            : $"endpoint={endpoint.Host}:{endpoint.Port}";

        string serviceText = serviceProbe.Exists
            ? $"service={serviceProbe.StatusText}"
            : "service=not_installed";

        return $"local_api_unreachable ({serviceText}, {endpointText})";
    }

    private static string BuildOnlineErrorMessage(string? apiError, ServiceProbeResult serviceProbe, bool statusFresh)
    {
        if (!string.IsNullOrWhiteSpace(apiError))
        {
            return apiError;
        }

        if (!statusFresh)
        {
            return "status_stale";
        }

        return serviceProbe.Running
            ? "None"
            : $"Service not running ({serviceProbe.StatusText}).";
    }

    private string BuildDiagnosticsSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PORTA Diagnostics Summary");
        sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Service running: {ServiceRunningValue.Text}");
        sb.AppendLine($"Tray running: {TrayRunningValue.Text}");
        sb.AppendLine($"Consent active: {ConsentActiveValue.Text}");
        sb.AppendLine($"Backend connected: {BackendConnectedValue.Text}");
        sb.AppendLine($"Last event: {LastEventValue.Text}");
        sb.AppendLine($"Policy version: {PolicyVersionValue.Text}");
        sb.AppendLine($"Schema version: {SchemaVersionValue.Text}");
        sb.AppendLine($"Snapshot confidence: {SnapshotConfidenceValue.Text}");
        sb.AppendLine($"Overload active: {OverloadActiveValue.Text}");
        sb.AppendLine($"Sustained attention (s): {SustainedAttentionValue.Text}");
        sb.AppendLine($"Idle fragmentation index: {IdleFragmentationValue.Text}");
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
    private sealed record NowTruth(LocalApiCurrentSnapshotResponse? Snapshot, string Explanation);
    private sealed record OverloadExplainView(
        string FivSummary,
        string TopReasons,
        string Quality,
        string Agreement,
        double? Clarity,
        double? Pressure,
        double? Jitter)
    {
        public static OverloadExplainView Unavailable =>
            new(
                "Explain not available (backend field missing).",
                "Explain not available (backend field missing).",
                "n/a",
                "n/a",
                null,
                null,
                null);
    }

    private sealed record InterventionRow(
        string Action,
        string PolicyId,
        string Domain,
        string TriggerState,
        string TriggerValue,
        string Confidence,
        string Duration,
        string Escalation,
        string Lifecycle,
        string Reversible,
        LocalApiEventResponse Source);
}
