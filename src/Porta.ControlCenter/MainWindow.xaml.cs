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
    private ActionsView _actionsView = ActionsView.Suggestions;
    private readonly DispatcherTimer _settingsAutoSaveTimer;
    private string _settingsAutoSaveReason = "settings_auto";
    private DateTimeOffset _settingsDirtyUntilUtc = DateTimeOffset.MinValue;
    private bool _settingsSaveInFlight;
    private static readonly TimeSpan HistorySelectionHold = TimeSpan.FromSeconds(12);
    private DateTimeOffset? _selectedHistorySnapshotTimestampUtc;
    private long? _selectedHistoryInterventionId;
    private DateTimeOffset? _historySelectionExpiresUtc;

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

    private enum ActionsView
    {
        Suggestions,
        Changes
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
        _lastSeenEventId = LoadLastSeenEventId();
        UiVersionMenuItem.Header = $"UI: {_uiVersion}";
        InitializePauseUntilControls();
        _settingsAutoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsAutoSaveTimer.Tick += async (_, _) =>
        {
            _settingsAutoSaveTimer.Stop();
            await SaveSettingsAsync(_settingsAutoSaveReason).ConfigureAwait(true);
        };

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
            _settingsAutoSaveTimer.Stop();
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
            PopulateInputs(null, null, null);
            PopulateInterventions([], null);
            PopulateSettings(null);
            PopulateStates(null);
            PopulateTimeline(null);
            UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            UpdateHealthChecklist(serviceProbe, apiReachable);
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
        PopulateInputs(inputSummary, _retention, truthSnapshot);
        PopulateInterventions(_allRecentEvents, _effectiveSettings);
        PopulateSettings(_effectiveSettings);
        PopulateStates(truthSnapshot);
        PopulateTimeline(GetSelectedHistoryTimeline());
        UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        UpdateHealthChecklist(serviceProbe, apiReachable);
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
            LiveKeyboardValue.Text = "Unknown";
            LiveMouseValue.Text = "Unknown";
            LiveSwitchValue.Text = "Unknown";
            LiveIdleValue.Text = "Unknown";
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
            LiveQualityValue.Text = "Quality score: Unknown";
            LivePartialValue.Text = "Partial data: Unknown";
            LiveMissingChannelsValue.Text = "Missing: n/a";
        }
        else
        {
            LiveQualityValue.Text = snapshot.CurrentQualityScore.HasValue
                ? $"Quality score: {snapshot.CurrentQualityScore.Value:F2}"
                : "Quality score: Unknown";
            LivePartialValue.Text = snapshot.PartialData.HasValue
                ? $"Partial data: {(snapshot.PartialData.Value ? "Yes" : "No")}"
                : "Partial data: Unknown";
            LiveMissingChannelsValue.Text = snapshot.MissingMajorChannels is { Count: > 0 }
                ? $"Missing: {string.Join(" / ", snapshot.MissingMajorChannels)}"
                : "Missing: None";
        }

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
                Undo: BuildUndoBadge(e),
                Source: e))
            .ToList();

        SuggestionsListView.ItemsSource = suggestions;
        ChangesListView.ItemsSource = changes;
        SuggestionsCountText.Text = $"Suggestions: {suggestions.Count}";
        ChangesCountText.Text = $"Changes: {changes.Count}";

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

        // Keep change-list scrolling controlled/readable instead of fast jumps.
        double step = 24d;
        double nextOffset = scrollViewer.VerticalOffset - (Math.Sign(e.Delta) * step);
        scrollViewer.ScrollToVerticalOffset(Math.Max(0d, nextOffset));
        e.Handled = true;
    }

    private void UpdateInterventionDrawer(LocalApiEventResponse? item)
    {
        if (item == null)
        {
            ActionDrawerTitle.Text = "Select an action event";
            ActionDrawerWhen.Text = "When: n/a";
            ActionDrawerShortWhy.Text = "Why: n/a";
            ActionDrawerCooldown.Text = "Cooldown remaining: n/a";
            ActionDrawerRepeat.Text = string.Empty;
            ActionDrawerRepeat.Visibility = Visibility.Collapsed;
            ActionDrawerWhatChanged.Text = "What changed: n/a";
            ActionDrawerUndo.Text = "Undo: n/a";
            ActionDrawerWhyJson.Text = "n/a";
            ActionDrawerUndoButton.IsEnabled = false;
            return;
        }

        bool isSuggestion = IsSuggestionEvent(item.Action);
        ActionDrawerTitle.Text = isSuggestion
            ? BuildSuggestionTitle(item.Action)
            : (string.IsNullOrWhiteSpace(item.WhatChanged) ? HumanizeActionName(item.Action) : item.WhatChanged);
        ActionDrawerWhen.Text = $"When: {item.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        ActionDrawerShortWhy.Text = $"Why: {(isSuggestion ? BuildSuggestionShortWhy(item) : BuildChangeShortWhy(item))}";
        ActionDrawerCooldown.Text = $"Cooldown remaining: {FormatCooldownRemaining(item.CooldownEndTimestampUtc)}";
        ActionDrawerRepeat.Text = isSuggestion
            ? BuildSuggestionRepeatLine(item)
            : string.Empty;
        ActionDrawerRepeat.Visibility = isSuggestion ? Visibility.Visible : Visibility.Collapsed;
        ActionDrawerWhatChanged.Text = $"What changed: {(string.IsNullOrWhiteSpace(item.WhatChanged) ? HumanizeActionName(item.Action) : item.WhatChanged)}";
        ActionDrawerUndo.Text = isSuggestion
            ? "Undo: Not needed for suggestions."
            : $"Undo: {BuildUndoDetails(item)}";
        ActionDrawerUndo.Visibility = Visibility.Visible;
        ActionDrawerWhyJson.Text = FormatWhyJsonForDisplay(item.WhyJson);
        ActionDrawerUndoButton.IsEnabled = !isSuggestion && item.Reversible;
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

        return "Applied according to current regulation policy.";
    }

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
            return manualUndo;
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
            instruction = "Open Controls and disable Notification reduction.";
            return true;
        }

        if (normalized.Contains("focus", StringComparison.Ordinal))
        {
            instruction = "Open Controls and disable Focus mode nudge.";
            return true;
        }

        if (normalized.Contains("animation", StringComparison.Ordinal))
        {
            instruction = "Open Controls and disable Animation reduction.";
            return true;
        }

        if (normalized.Contains("app", StringComparison.Ordinal) || normalized.Contains("restrict", StringComparison.Ordinal))
        {
            instruction = "Open Controls and disable Temporary app restriction.";
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
            return "n/a";
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
            return "n/a";
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
            return "n/a";
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

    private void PopulateStates(LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (snapshot == null)
        {
            SetFlowStepState(SignalsStatusPill, SignalsStatusText, FlowStepState.Blocked, "Blocked");
            SignalsBlockersText.Text = "What can block it: service offline / consent needed / auth not ready.";
            SignalsCurrentText.Text = "Current status: no live signal snapshot.";

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
        SignalsCurrentText.Text = $"Current status: keyboard {ChannelAvailabilityLabel(missingChannels, "keyboard")}, mouse {ChannelAvailabilityLabel(missingChannels, "mouse")}, focus {ChannelAvailabilityLabel(missingChannels, "focus")}, idle {ChannelAvailabilityLabel(missingChannels, "idle")}.";

        bool baselineReady = snapshot.BaselineReady == true;
        bool coldStart = snapshot.ColdStart == true;
        bool overloadActive = snapshot.OverloadActive;
        double quality = snapshot.CurrentQualityScore ?? 0d;
        var calibrationBlockers = new List<string>();
        if (!baselineReady)
        {
            if (quality > 0d && quality < 0.65d)
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

            if (coldStart)
            {
                calibrationBlockers.Add("cold start");
            }

            if (calibrationBlockers.Count == 0)
            {
                calibrationBlockers.Add("collecting enough baseline data");
            }
        }

        FlowStepState calibrationState = baselineReady
            ? FlowStepState.Ready
            : FlowStepState.Warning;
        SetFlowStepState(
            CalibrationStatusPill,
            CalibrationStatusText,
            calibrationState,
            baselineReady ? "Ready" : "Calibrating");
        CalibrationBlockersText.Text = calibrationBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", calibrationBlockers)}.";
        string baselinePctText = snapshot.BaselineReadyPct.HasValue
            ? $"{Math.Clamp(snapshot.BaselineReadyPct.Value * 100d, 0d, 100d):F0}% metrics ready"
            : "progress unavailable";
        CalibrationCurrentText.Text = $"Current status: {(baselineReady ? "baseline ready" : "calibrating")} ({baselinePctText}), quality {(snapshot.CurrentQualityScore?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a")}, cold start {(coldStart ? "on" : "off")}.";

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
        double overload = Math.Clamp(snapshot.OverloadLikelihood, 0d, 1d);
        double focusedMinutes = Math.Max(0d, snapshot.SustainedAttentionDurationSec / 60d);
        var decisionBlockers = new List<string>();
        if (trust < 0.60d)
        {
            decisionBlockers.Add("low trust");
        }

        if (snapshot.CooldownTriggered)
        {
            decisionBlockers.Add("cooldown active");
        }

        if (snapshot.SustainedAttentionDurationSec < 600d)
        {
            decisionBlockers.Add("not enough focus streak");
        }

        if (overload < 0.80d)
        {
            decisionBlockers.Add("overload below action threshold");
        }

        FlowStepState decisionsState = decisionBlockers.Count == 0
            ? FlowStepState.Ready
            : FlowStepState.Warning;
        string decisionLabel = decisionsState == FlowStepState.Ready
            ? "Ready"
            : snapshot.OverloadActive
                ? "Monitoring"
                : "Holding";
        SetFlowStepState(DecisionsStatusPill, DecisionsStatusText, decisionsState, decisionLabel);
        DecisionsBlockersText.Text = decisionBlockers.Count == 0
            ? "What can block it: none detected."
            : $"What can block it: {string.Join(" / ", decisionBlockers)}.";
        DecisionsCurrentText.Text = $"Current status: overload {(overload * 100d):F0}%, trust {(trust * 100d):F0}%, focus streak {focusedMinutes:F0} min, overload active {(snapshot.OverloadActive ? "yes" : "no")}.";
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
        TimelineOverloadTrackPanel.Children.Clear();
        TimelineTrustTrackPanel.Children.Clear();
        TimelineFocusTrackPanel.Children.Clear();
        TimelineOverloadActiveOverlayPanel.Children.Clear();

        if (timeline == null || timeline.Snapshots.Count == 0)
        {
            TimelineOverloadTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineTrustTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineFocusTrackPanel.Children.Add(new TextBlock { Text = "No timeline data.", Foreground = Brushes.Gainsboro });
            TimelineOverloadActiveOverlayPanel.Children.Add(new TextBlock { Text = "No overload-active data.", Foreground = Brushes.Gainsboro });
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
            Width = 9,
            Height = Math.Max(8d, 64d * clamped),
            Margin = new Thickness(1.5, 0, 1.5, 0),
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
        button.ToolTip = $"{intervention.TimestampUtc.ToLocalTime():HH:mm:ss} | {intervention.Action}";
        button.Click += TimelineInterventionButton_Click;
        return button;
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
            TimelineDetailTimestamp.Text = "Timestamp: n/a";
            TimelineDetailOverload.Text = "Overload: n/a";
            TimelineDetailTrust.Text = "Trust level: n/a";
            TimelineDetailFocus.Text = "Focus streak: n/a";
            TimelineDetailWhyHeader.Text = "Why: n/a";
            TimelineDetailWhyFiv.Text = "Clarity/Pressure/Jitter: n/a";
            TimelineDetailWhyReasons.Text = "Top reasons: n/a";
            TimelineDetailWhyQuality.Text = "Quality: n/a | Agreement: n/a";
            TimelineDetailStabilization.Text = "Stabilization: n/a";
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
            TimelineDetailTrust.Text = $"Trust level: {(snapshot.Confidence * 100d):F0}%";
            TimelineDetailFocus.Text = $"Focus streak: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}";
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
                : "Quality: n/a | Agreement: n/a";
            TimelineDetailStabilization.Text = BuildStabilizationText(snapshot.StateFlagsJson, _historyExplainMode);
            return;
        }

        TimelineDetailTimestamp.Text = $"Timestamp: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TimelineDetailOverload.Text = $"Action trigger: {intervention.TriggerState}={intervention.TriggerValue:F2}";
        TimelineDetailTrust.Text = $"Trust level: {(intervention.Confidence * 100d):F0}%";
        TimelineDetailFocus.Text = "Focus streak: n/a (event marker)";
        TimelineDetailWhyHeader.Text = _historyExplainMode
            ? "Why? (Detailed)"
            : "Why?";
        TimelineDetailWhyFiv.Text = string.IsNullOrWhiteSpace(intervention.Reason)
            ? "Reason: n/a"
            : $"Reason: {intervention.Reason}";
        TimelineDetailWhyReasons.Text = intervention.Contributors.Count > 0
            ? $"Top reasons: {FormatTopReasons(string.Join(", ", intervention.Contributors.Take(6)), _historyExplainMode)}"
            : "Top reasons: n/a";
        TimelineDetailWhyQuality.Text = "Quality: n/a | Agreement: n/a";
        TimelineDetailStabilization.Text = BuildStabilizationText(intervention.StateFlagsJson, _historyExplainMode);
    }

    private static string BuildStabilizationText(string? stateFlagsJson, bool explainMode)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return explainMode
                ? "Stabilization: detailed flags missing"
                : "Stabilization: ON timer n/a / 120s";
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
                : "Stabilization: ON timer n/a / 120s";
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

    private void UpdateHealthChecklist(ServiceProbeResult serviceProbe, bool apiReachable)
    {
        string lastError = LastErrorValue.Text;
        string lastUpdate = UpdatedValue.Text.Replace("Last refresh:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

        HealthServiceValue.Text = serviceProbe.Running
            ? "✅ Yes"
            : $"❌ No ({serviceProbe.StatusText})";

        HealthApiValue.Text = apiReachable
            ? "✅ Yes"
            : "❌ No";

        if (_latestStatus == null)
        {
            HealthAuthValue.Text = "❌ Unknown (status unavailable)";
            HealthConsentValue.Text = "❌ Unknown";
            HealthDatabaseValue.Text = "❌ Unknown";
            HealthLastUpdateValue.Text = string.IsNullOrWhiteSpace(lastUpdate) ? "Unknown" : lastUpdate;
            HealthLastErrorValue.Text = string.IsNullOrWhiteSpace(lastError) ? "None" : lastError;
            return;
        }

        HealthAuthValue.Text = _latestStatus.LocalApiAuthReady
            ? "✅ Yes"
            : $"❌ No ({_latestStatus.LocalApiAuthError ?? "TOKEN_READ_FAILED"})";

        HealthConsentValue.Text = _latestStatus.ConsentActive
            ? "✅ Yes"
            : "❌ No";

        bool migrationFailure =
            (!string.IsNullOrWhiteSpace(_latestStatus.LastError)
                && (_latestStatus.LastError.Contains("migration", StringComparison.OrdinalIgnoreCase)
                    || _latestStatus.LastError.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
                    || _latestStatus.LastError.Contains("sqlite error", StringComparison.OrdinalIgnoreCase)
                    || _latestStatus.LastError.Contains("db startup failed", StringComparison.OrdinalIgnoreCase)))
            || (lastError.Contains("migration", StringComparison.OrdinalIgnoreCase)
                || lastError.Contains("readonly database", StringComparison.OrdinalIgnoreCase)
                || lastError.Contains("sqlite error", StringComparison.OrdinalIgnoreCase)
                || lastError.Contains("db startup failed", StringComparison.OrdinalIgnoreCase));
        bool schemaKnown = !string.IsNullOrWhiteSpace(_latestStatus.SchemaVersion) && !_latestStatus.SchemaVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        bool databaseOk = schemaKnown && !migrationFailure;
        HealthDatabaseValue.Text = databaseOk
            ? $"✅ Yes (schema {_latestStatus.SchemaVersion})"
            : migrationFailure
                ? "❌ No (migration/startup error)"
                : "❌ No (schema unknown)";

        HealthLastUpdateValue.Text = string.IsNullOrWhiteSpace(lastUpdate) ? "Unknown" : lastUpdate;
        HealthLastErrorValue.Text = string.IsNullOrWhiteSpace(lastError) ? "None" : lastError;
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
        double? Jitter,
        bool HasStructuredData)
    {
        public static OverloadExplainView Unavailable =>
            new(
                "Explain not available (backend field missing).",
                "Explain not available (backend field missing).",
                "n/a",
                "n/a",
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
        string Undo,
        LocalApiEventResponse Source);
}
