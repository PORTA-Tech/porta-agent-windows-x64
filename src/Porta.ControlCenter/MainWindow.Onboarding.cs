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
        bool dbBusyRetrying = IsDbBusyRetrying(status, snapshot: null);

        OnboardingCurrentStatusText.Text = OnboardingCopyBuilder.BuildStatusText(
            serviceProbe.Running,
            trayRunning,
            apiReachable,
            status,
            statusFresh,
            captureHint,
            statusHttpCode,
            dbBusyRetrying);
        OnboardingCurrentSystemText.Text = OnboardingCopyBuilder.BuildSystemText(
            serviceProbe.Running,
            trayRunning,
            apiReachable,
            status,
            statusFresh,
            captureHint,
            statusHttpCode);
        OnboardingCurrentGuidanceText.Text = OnboardingCopyBuilder.BuildGuidanceText(
            serviceProbe.Running,
            trayRunning,
            apiReachable,
            status,
            statusFresh,
            captureHint,
            statusHttpCode,
            dbBusyRetrying);
        OnboardingOverlay.Visibility = Visibility.Visible;
    }

    private void OnboardingOpenSystemButton_Click(object sender, RoutedEventArgs e)
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
