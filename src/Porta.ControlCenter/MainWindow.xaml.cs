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
            LastErrorValue.Text = "local_api_unreachable";
            QuickPauseResumeButton.Content = "Pause";
            UpdatedValue.Text = $"Last refresh: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            return;
        }

        StatusPillText.Text = _latestStatus.Paused ? "Paused" : "Running";
        ConsentBadgeText.Text = $"Consent: {(_latestStatus.ConsentActive ? "Active" : "Missing")}";
        BackendBadgeText.Text = $"Backend: {(_latestStatus.BackendConnected ? "Connected" : "Offline")}";
        VersionText.Text = $"Version: {_latestStatus.AgentVersion}";

        ConsentActiveValue.Text = _latestStatus.ConsentActive ? "Yes" : "No";
        BackendConnectedValue.Text = _latestStatus.BackendConnected ? "Yes" : "No";
        FocusStrengthValue.Text = _latestStatus.FocusStrength.ToString("F2", CultureInfo.InvariantCulture);
        OverloadValue.Text = _latestStatus.OverloadLikelihood.ToString("F2", CultureInfo.InvariantCulture);
        LastActionValue.Text = _latestStatus.LastAction;
        LastEventValue.Text = _latestStatus.LastEventTimestampUtc?.ToString("O") ?? "n/a";
        PolicyVersionValue.Text = _latestStatus.PolicyVersion;
        SchemaVersionValue.Text = _latestStatus.SchemaVersion;
        AgentVersionValue.Text = _latestStatus.AgentVersion;
        ApiPortValue.Text = endpoint?.Port.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";
        LastErrorValue.Text = string.IsNullOrWhiteSpace(_latestStatus.LastError) ? "None" : _latestStatus.LastError;
        QuickPauseResumeButton.Content = _latestStatus.Paused ? "Resume" : "Pause";
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

    private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PortaAgent");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

