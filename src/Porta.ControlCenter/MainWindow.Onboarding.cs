using System.Windows;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

public partial class MainWindow
{
    private void PopulateOnboardingSurface(
        ServiceProbeResult serviceProbe,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh)
    {
        if (OnboardingOverlay == null)
        {
            return;
        }

        if (!_showOnboardingOverlay)
        {
            OnboardingOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        string? captureHint = status != null && statusFresh
            ? BuildCaptureContextHint(status)
            : null;

        OnboardingCurrentStatusText.Text = BuildOnboardingStatusText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint);
        OnboardingCurrentHealthText.Text = BuildOnboardingHealthText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint);
        OnboardingCurrentGuidanceText.Text = BuildOnboardingGuidanceText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint);
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private static string BuildOnboardingStatusText(
        ServiceProbeResult serviceProbe,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint)
    {
        if (!serviceProbe.Running)
        {
            return "Background runtime is limited because the AriAgent service is not running yet.";
        }

        if (!trayRunning)
        {
            return "Background runtime is limited because the live desktop session agent is not running yet.";
        }

        if (!apiReachable || status == null)
        {
            return "Ari is starting up in the background and waiting for live health from the local API.";
        }

        if (!statusFresh)
        {
            return "Ari is running, but the current health snapshot is stale.";
        }

        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return $"Ari is running, but capture is limited right now: {captureHint}.";
        }

        return "Ari is healthy right now and can keep working after you close Control Center.";
    }

    private static string BuildOnboardingHealthText(
        ServiceProbeResult serviceProbe,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint)
    {
        string serviceState = serviceProbe.Running ? "service on" : "service off";
        string trayState = trayRunning ? "session agent on" : "session agent off";
        string apiState = apiReachable ? "API reachable" : "API not ready";
        string captureState = string.IsNullOrWhiteSpace(captureHint)
            ? (status != null && statusFresh ? "capture flowing" : "capture not verified yet")
            : "capture limited";

        return $"Healthy means: {serviceState}, {trayState}, {apiState}, and {captureState}.";
    }

    private static string BuildOnboardingGuidanceText(
        ServiceProbeResult serviceProbe,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint)
    {
        if (!serviceProbe.Running)
        {
            return "What to do: keep Control Center open for a moment, then open Health if the service still shows offline.";
        }

        if (!trayRunning)
        {
            return "What to do: sign out and back in to Windows if the desktop session agent does not appear within a minute.";
        }

        if (!apiReachable || status == null)
        {
            return "What to do: wait a few seconds while Ari finishes starting in the background. Open Health if it stays stuck.";
        }

        if (!statusFresh)
        {
            return "What to do: open Health and refresh once. If the snapshot stays stale, sign out and back in.";
        }

        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return "What to do: keep using the keyboard or mouse for a few seconds while Ari retries. If it stays stuck, open Health and then sign out and back in.";
        }

        return "What to do: you can close Control Center now. Reopen it any time from Start Menu, the desktop shortcut, or the PORTA tray icon.";
    }

    private void OnboardingOpenHealthButton_Click(object sender, RoutedEventArgs e)
    {
        _showOnboardingOverlay = false;
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        ShowSection(UiSection.Diagnostics);
        Activate();
    }

    private void OnboardingDismissButton_Click(object sender, RoutedEventArgs e)
    {
        _showOnboardingOverlay = false;
        SaveOnboardingAcknowledged();
        OnboardingOverlay.Visibility = Visibility.Collapsed;
    }
}
