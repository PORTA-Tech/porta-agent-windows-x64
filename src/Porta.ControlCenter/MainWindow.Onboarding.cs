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
        bool statusFresh,
        int? statusHttpCode)
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

        OnboardingCurrentStatusText.Text = BuildOnboardingStatusText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint, statusHttpCode);
        OnboardingCurrentHealthText.Text = BuildOnboardingHealthText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint, statusHttpCode);
        OnboardingCurrentGuidanceText.Text = BuildOnboardingGuidanceText(serviceProbe, trayRunning, apiReachable, status, statusFresh, captureHint, statusHttpCode);
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private static string BuildOnboardingStatusText(
        ServiceProbeResult serviceProbe,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint,
        int? statusHttpCode)
    {
        if (!serviceProbe.Running)
        {
            return "Background runtime is limited because the AriAgent service is not running yet.";
        }

        if (!trayRunning)
        {
            return "Background runtime is limited because the live desktop session agent is not running yet.";
        }

        if (!apiReachable)
        {
            return serviceProbe.Running ? "Local API unreachable." : "Service offline.";
        }

        if (status == null)
        {
            return statusHttpCode is 401 or 403
                ? "API auth mismatch."
                : "Service alive, waiting for current status.";
        }

        if (!status.ServiceRunning)
        {
            return "Service offline.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "API auth mismatch.";
        }

        if (!statusFresh)
        {
            return BuildFreshnessExplanation(status, snapshot: null);
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
        string? captureHint,
        int? statusHttpCode)
    {
        string serviceState = serviceProbe.Running ? "service on" : "service off";
        string trayState = trayRunning ? "session agent on" : "session agent off";
        string apiState = !apiReachable
            ? (serviceProbe.Running ? "API unreachable" : "service offline")
            : status == null && statusHttpCode is 401 or 403
                ? "API auth mismatch"
                : "API reachable";
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
        string? captureHint,
        int? statusHttpCode)
    {
        if (!serviceProbe.Running)
        {
            return "What to do: keep Control Center open for a moment, then open System if the service still shows offline.";
        }

        if (!trayRunning)
        {
            return "What to do: sign out and back in to Windows if the desktop session agent does not appear within a minute.";
        }

        if (!apiReachable)
        {
            return serviceProbe.Running
                ? "What to do: open System or refresh once if Local API unreachable stays on screen."
                : "What to do: open System if the service stays offline.";
        }

        if (status == null)
        {
            return statusHttpCode is 401 or 403
                ? "What to do: close and reopen Control Center once so it reloads local API credentials. If it still shows this, open System."
                : "What to do: wait a few seconds and refresh once. If status still does not load, open System.";
        }

        if (!status.ServiceRunning)
        {
            return "What to do: open System and restart the service if it stays offline.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "What to do: reopen Control Center so it reloads local API credentials. If it still shows this, open System.";
        }

        if (!statusFresh)
        {
            return IsDbBusyRetrying(status, snapshot: null)
                ? "What to do: wait a few seconds while Ari retries the database refresh, then refresh once."
                : "What to do: open System and refresh once. If status stays stale, sign out and back in.";
        }

        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return "What to do: keep using the keyboard or mouse for a few seconds while Ari retries. If it stays stuck, open System and then sign out and back in.";
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
