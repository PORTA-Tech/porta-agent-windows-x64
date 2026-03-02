using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
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

    private readonly LocalApiClient _apiClient = new();
    private readonly DispatcherTimer _refreshTimer;
    private LocalApiStatusResponse? _latestStatus;
    private LocalApiCurrentSnapshotResponse? _lastSnapshot;
    private IReadOnlyList<LocalApiEventResponse> _recentEvents = [];
    private IReadOnlyList<LocalApiEventResponse> _allRecentEvents = [];
    private LocalApiTimelineResponse? _todayTimeline;
    private LocalApiTimelineResponse? _hourTimeline;
    private LocalApiSettingsResponse? _effectiveSettings;
    private LocalApiRetentionResponse? _retention;
    private bool _suppressSettingsEvents;
    private LocalApiEventResponse? _selectedIntervention;
    private DateTimeOffset _lastSlowRefreshUtc = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _manualRefreshPending;

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

    public MainWindow()
    {
        InitializeComponent();

        ShowSection(UiSection.Overview);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
            RefreshButton.IsEnabled = false;
            RefreshButton.Content = "Refreshing...";
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
            RefreshButton.Content = "Refresh";
            RefreshButton.IsEnabled = true;
            _refreshGate.Release();
        }
    }

    private async Task RefreshDiagnosticsCoreAsync()
    {
        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        ServiceProbeResult serviceProbe = ProbeServiceStatus();
        _latestStatus = await _apiClient.GetStatusAsync().ConfigureAwait(true);

        bool apiReachable = _latestStatus != null;
        LocalApiCurrentSnapshotResponse? previousSnapshot = _lastSnapshot;
        LocalApiCurrentSnapshotResponse? snapshot = apiReachable
            ? await _apiClient.GetCurrentSnapshotAsync().ConfigureAwait(true)
            : null;
        LocalApiInputsSummaryResponse? inputSummary = apiReachable
            ? await _apiClient.GetInputsSummaryAsync(60).ConfigureAwait(true)
            : null;
        _allRecentEvents = apiReachable
            ? await _apiClient.GetRecentEventsAsync(50).ConfigureAwait(true)
            : [];
        _recentEvents = _allRecentEvents.Take(5).ToArray();
        _lastSnapshot = snapshot;

        bool needsSlowRefresh = apiReachable
            && (DateTimeOffset.UtcNow - _lastSlowRefreshUtc) >= SlowRefreshInterval;
        if (needsSlowRefresh)
        {
            DateTimeOffset fromUtc = DateTimeOffset.UtcNow.Date;
            _todayTimeline = await _apiClient.GetTimelineAsync(fromUtc, DateTimeOffset.UtcNow, 60).ConfigureAwait(true);
            _hourTimeline = await _apiClient.GetTimelineAsync(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, 5).ConfigureAwait(true);
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

        if (_latestStatus == null)
        {
            StatusPillText.Text = serviceProbe.Running ? "API Unreachable" : "Offline";
            ConsentBadgeText.Text = "Consent: Unknown";
            BackendBadgeText.Text = "Backend: Unknown";
            VersionText.Text = "Version: n/a";
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
            QuickPauseResumeButton.Content = "Pause";
            QuickPauseResumeButton.IsEnabled = false;
            ConsentOverlay.Visibility = Visibility.Collapsed;
            PopulateOverview(null, null, [], previousSnapshot);
            PopulateInputs(null, null);
            PopulateInterventions([], null);
            PopulateSettings(null);
            PopulateStates(null);
            PopulateTimeline(null);
            UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            return;
        }

        bool consentMissing = !_latestStatus.ConsentActive;
        StatusPillText.Text = consentMissing
            ? "Needs Consent"
            : (_latestStatus.Paused ? "Paused" : "Running");
        ConsentBadgeText.Text = $"Consent: {(_latestStatus.ConsentActive ? "Active" : "Missing")}";
        BackendBadgeText.Text = $"Backend: {(_latestStatus.BackendConnected ? "Connected" : "Offline")}";
        VersionText.Text = $"Version: {_latestStatus.AgentVersion}";

        ConsentActiveValue.Text = _latestStatus.ConsentActive ? "Yes" : "No";
        BackendConnectedValue.Text = _latestStatus.BackendConnected ? "Yes" : "No";
        double focusStrength = snapshot?.FocusStrength ?? _latestStatus.FocusStrength;
        double overloadLikelihood = snapshot?.OverloadLikelihood ?? _latestStatus.OverloadLikelihood;
        string lastAction = snapshot?.LastAction ?? _latestStatus.LastAction;

        FocusStrengthValue.Text = focusStrength.ToString("F2", CultureInfo.InvariantCulture);
        OverloadValue.Text = overloadLikelihood.ToString("F2", CultureInfo.InvariantCulture);
        LastActionValue.Text = inputSummary != null
            ? $"{lastAction} | inputSamples={inputSummary.SampleCount}"
            : lastAction;
        LastEventValue.Text = _latestStatus.LastEventTimestampUtc?.ToString("O") ?? "n/a";
        PolicyVersionValue.Text = _latestStatus.PolicyVersion;
        SchemaVersionValue.Text = _latestStatus.SchemaVersion;
        AgentVersionValue.Text = _latestStatus.AgentVersion;
        ConsentVersionValue.Text = _latestStatus.ConsentVersion;
        ConsentTimestampValue.Text = _latestStatus.ConsentTimestampUtc?.ToString("O") ?? "n/a";
        ConsentHashValue.Text = _latestStatus.ConsentDisclosureHash;
        LastErrorValue.Text = BuildOnlineErrorMessage(_latestStatus.LastError, serviceProbe);
        QuickPauseResumeButton.Content = _latestStatus.Paused ? "Resume" : "Pause";
        QuickPauseResumeButton.IsEnabled = true;
        ConsentOverlay.Visibility = consentMissing ? Visibility.Visible : Visibility.Collapsed;

        PopulateOverview(snapshot, inputSummary, _recentEvents, previousSnapshot);
        PopulateInputs(inputSummary, _retention);
        PopulateInterventions(_allRecentEvents, _effectiveSettings);
        PopulateSettings(_effectiveSettings);
        PopulateStates(snapshot);
        PopulateTimeline(_hourTimeline);
        UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        ControlCenterFileLog.Info("manual_refresh_clicked");
        await RefreshDiagnosticsAsync(manual: true).ConfigureAwait(true);
        ControlCenterFileLog.Info($"manual_refresh_completed api_reachable={(_latestStatus != null).ToString().ToLowerInvariant()}");
    }

    private void OverviewNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Overview);
    }

    private void TimelineNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSection(UiSection.Timeline);
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
            UiSection.Overview => "Overview",
            UiSection.Timeline => "Timeline",
            UiSection.Interventions => "Interventions",
            UiSection.Inputs => "Inputs",
            UiSection.States => "States",
            UiSection.Settings => "Settings",
            UiSection.Diagnostics => "Diagnostics",
            _ => "Overview"
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

    private void PopulateOverview(
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiInputsSummaryResponse? inputSummary,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        LocalApiCurrentSnapshotResponse? previousSnapshot)
    {
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
        TimelinePrimitivesTrackPanel.Children.Clear();
        TimelineStatesTrackPanel.Children.Clear();
        TimelineInterventionsTrackPanel.Children.Clear();

        if (timeline == null || timeline.Snapshots.Count == 0)
        {
            TimelinePrimitivesTrackPanel.Children.Add(new TextBlock { Text = "No timeline data." });
            TimelineStatesTrackPanel.Children.Add(new TextBlock { Text = "No timeline data." });
            TimelineInterventionsTrackPanel.Children.Add(new TextBlock { Text = "No interventions." });
            SetTimelineDetail(null, null);
            return;
        }

        foreach (LocalApiCurrentSnapshotResponse snapshot in timeline.Snapshots.TakeLast(240))
        {
            TimelinePrimitivesTrackPanel.Children.Add(BuildSnapshotBar(snapshot, TimelineTrackKind.Primitives));
            TimelineStatesTrackPanel.Children.Add(BuildSnapshotBar(snapshot, TimelineTrackKind.States));
        }

        foreach (LocalApiEventResponse intervention in timeline.Interventions.TakeLast(80))
        {
            TimelineInterventionsTrackPanel.Children.Add(BuildInterventionBar(intervention));
        }

        SetTimelineDetail(timeline.Snapshots.LastOrDefault(), null);
    }

    private UIElement BuildSnapshotBar(LocalApiCurrentSnapshotResponse snapshot, TimelineTrackKind kind)
    {
        double score = kind switch
        {
            TimelineTrackKind.Primitives => Math.Clamp((snapshot.TypingBurstIntensity + snapshot.ScrollVelocityIndex + snapshot.FocusSwitchRate) / 3d, 0d, 1d),
            _ => Math.Clamp((snapshot.FocusStrength + snapshot.OverloadLikelihood + snapshot.FatigueProbability) / 3d, 0d, 1d)
        };

        var button = new Button
        {
            Width = 10,
            Height = Math.Max(6d, 40d * score),
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Tag = snapshot,
            Background = kind == TimelineTrackKind.Primitives
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(59, 130, 246)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(15, 23, 42))
        };
        button.Click += TimelineSnapshotButton_Click;
        return button;
    }

    private UIElement BuildInterventionBar(LocalApiEventResponse intervention)
    {
        var button = new Button
        {
            Content = "I",
            Width = intervention.DurationSeconds.HasValue
                ? Math.Clamp(intervention.DurationSeconds.Value / 8d, 10d, 42d)
                : 12d,
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
            TimelineDetailStates.Text = "State values: n/a";
            TimelineDetailConfidence.Text = "Confidence: n/a";
            TimelineDetailContributors.Text = "Contributors: n/a";
            TimelineDetailPolicy.Text = "Policy ID: n/a";
            TimelineDetailSystemChange.Text = "What changed on system: n/a";
            return;
        }

        if (snapshot != null)
        {
            TimelineDetailTimestamp.Text = $"Timestamp: {snapshot.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            TimelineDetailStates.Text = $"State values: focus={snapshot.FocusStrength:F2}, overload={snapshot.OverloadLikelihood:F2}, fatigue={snapshot.FatigueProbability:F2}";
            TimelineDetailConfidence.Text = $"Confidence: {(snapshot.Confidence * 100d):F0}%";
            TimelineDetailContributors.Text = snapshot.Contributors.Count > 0
                ? $"Contributors: {string.Join(", ", snapshot.Contributors.Take(6))}"
                : "Contributors: n/a";
            TimelineDetailPolicy.Text = "Policy ID: n/a";
            TimelineDetailSystemChange.Text = $"What changed on system: {snapshot.LastAction}";
            return;
        }

        TimelineDetailTimestamp.Text = $"Timestamp: {intervention!.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        TimelineDetailStates.Text = $"State values: trigger={intervention.TriggerState}, value={intervention.TriggerValue:F2}";
        TimelineDetailConfidence.Text = $"Confidence: {(intervention.Confidence * 100d):F0}%";
        TimelineDetailContributors.Text = intervention.Contributors.Count > 0
            ? $"Contributors: {string.Join(", ", intervention.Contributors.Take(6))}"
            : "Contributors: n/a";
        TimelineDetailPolicy.Text = $"Policy ID: {intervention.PolicyId}";
        TimelineDetailSystemChange.Text = $"What changed on system: {(string.IsNullOrWhiteSpace(intervention.WhatChanged) ? intervention.Action : intervention.WhatChanged)}";
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

    private static string BuildOnlineErrorMessage(string? apiError, ServiceProbeResult serviceProbe)
    {
        if (!string.IsNullOrWhiteSpace(apiError))
        {
            return apiError;
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
    private enum TimelineTrackKind
    {
        Primitives,
        States
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
