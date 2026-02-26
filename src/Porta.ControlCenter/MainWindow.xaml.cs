using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

public partial class MainWindow : Window
{
    private readonly LocalApiClient _apiClient = new();
    private readonly DispatcherTimer _refreshTimer;
    private LocalApiStatusResponse? _latestStatus;

    public MainWindow()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshDiagnosticsAsync().ConfigureAwait(true);
        Loaded += async (_, _) =>
        {
            await RefreshDiagnosticsAsync().ConfigureAwait(true);
            _refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _apiClient.Dispose();
        };
    }

    private async Task RefreshDiagnosticsAsync()
    {
        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        _latestStatus = await _apiClient.GetStatusAsync().ConfigureAwait(true);
        LocalApiCurrentSnapshotResponse? snapshot = await _apiClient.GetCurrentSnapshotAsync().ConfigureAwait(true);
        LocalApiInputsSummaryResponse? inputSummary = await _apiClient.GetInputsSummaryAsync(60).ConfigureAwait(true);

        bool serviceOnline = _latestStatus != null;
        bool trayRunning = Process.GetProcessesByName("Porta.Tray").Length > 0;

        ServiceRunningValue.Text = serviceOnline ? "Yes" : "No";
        TrayRunningValue.Text = trayRunning ? "Yes" : "No";

        if (_latestStatus == null)
        {
            StatusPillText.Text = "Offline";
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
            ApiPortValue.Text = endpoint?.Port.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
            ConsentVersionValue.Text = "Unknown";
            ConsentTimestampValue.Text = "Unknown";
            ConsentHashValue.Text = "Unknown";
            LastErrorValue.Text = "local_api_unreachable";
            QuickPauseResumeButton.Content = "Pause";
            ConsentOverlay.Visibility = Visibility.Collapsed;
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
        ApiPortValue.Text = endpoint?.Port.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
        ConsentVersionValue.Text = _latestStatus.ConsentVersion;
        ConsentTimestampValue.Text = _latestStatus.ConsentTimestampUtc?.ToString("O") ?? "n/a";
        ConsentHashValue.Text = _latestStatus.ConsentDisclosureHash;
        LastErrorValue.Text = string.IsNullOrWhiteSpace(_latestStatus.LastError) ? "None" : _latestStatus.LastError;
        QuickPauseResumeButton.Content = _latestStatus.Paused ? "Resume" : "Pause";
        ConsentOverlay.Visibility = consentMissing ? Visibility.Visible : Visibility.Collapsed;
        UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private async void QuickPauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
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

    private async void GrantConsentButton_Click(object sender, RoutedEventArgs e)
    {
        await _apiClient.GrantConsentAsync().ConfigureAwait(true);
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
        LocalApiExportResponse? response = await _apiClient.ExportDataAsync().ConfigureAwait(true);
        if (response?.Success != true || string.IsNullOrWhiteSpace(response.BundlePath))
        {
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

        MessageBox.Show(
            $"Export created:\n{response.BundlePath}",
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
            MessageBox.Show(
                "Delete local data failed. Ensure the service is running and try again.",
                "PORTA Control Center",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        await RefreshDiagnosticsAsync().ConfigureAwait(true);
    }

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortaAgent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

