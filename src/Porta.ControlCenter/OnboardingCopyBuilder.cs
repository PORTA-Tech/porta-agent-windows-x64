using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal static class OnboardingCopyBuilder
{
    public static string BuildStatusText(
        bool serviceRunning,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint,
        int? statusHttpCode,
        bool dbBusyRetrying)
    {
        if (!serviceRunning)
        {
            return "Background tracking is limited because the Ari service is not running yet.";
        }

        if (!trayRunning)
        {
            return "Background tracking is limited because the live desktop session is not connected yet.";
        }

        if (!apiReachable)
        {
            return serviceRunning
                ? "System cannot reach the local service yet."
                : "Background tracking is limited because the service is offline.";
        }

        if (status == null)
        {
            return statusHttpCode is 401 or 403
                ? "System needs to reload local service access."
                : "System is connected and Today is still waiting for current status.";
        }

        if (!status.ServiceRunning)
        {
            return "Background tracking is limited because the Ari service is offline.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "System needs to reload local service access.";
        }

        if (!status.ConsentActive)
        {
            return "Tracking is waiting for your consent.";
        }

        if (status.Paused)
        {
            return "Tracking is paused right now.";
        }

        if (!statusFresh)
        {
            return dbBusyRetrying
                ? "Ari is still tracking, and Today is waiting for fresh details while the local database catches up."
                : "Ari is still tracking, but fresh Today details are delayed right now.";
        }

        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return $"Ari is running, but tracking quality is limited right now: {captureHint}.";
        }

        return "Ari is tracking normally and can keep working after you close Control Center.";
    }

    public static string BuildSystemText(
        bool serviceRunning,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint,
        int? statusHttpCode)
    {
        string serviceState = serviceRunning ? "service on" : "service off";
        string sessionState = trayRunning ? "desktop session connected" : "desktop session not connected";
        string apiState = !apiReachable
            ? (serviceRunning ? "local service unreachable" : "service offline")
            : status == null && statusHttpCode is 401 or 403
                ? "local service access needs reload"
                : "local service reachable";

        string trackingState =
            status?.Paused == true ? "tracking paused" :
            status?.ConsentActive == false ? "waiting for consent" :
            !string.IsNullOrWhiteSpace(captureHint) ? "tracking quality limited" :
            status != null && statusFresh ? "tracking active" :
            status != null ? "tracking still updating" :
            "tracking not verified yet";

        return $"System check: {serviceState}, {sessionState}, {apiState}, and {trackingState}.";
    }

    public static string BuildGuidanceText(
        bool serviceRunning,
        bool trayRunning,
        bool apiReachable,
        LocalApiStatusResponse? status,
        bool statusFresh,
        string? captureHint,
        int? statusHttpCode,
        bool dbBusyRetrying)
    {
        if (!serviceRunning)
        {
            return "What to do: keep Control Center open for a moment, then open System if the service still shows offline.";
        }

        if (!trayRunning)
        {
            return "What to do: wait up to a minute for the desktop session to appear. If it does not, sign out and back in to Windows.";
        }

        if (!apiReachable)
        {
            return serviceRunning
                ? "What to do: open System or refresh once if the local service stays unreachable."
                : "What to do: open System if the service stays offline.";
        }

        if (status == null)
        {
            return statusHttpCode is 401 or 403
                ? "What to do: close and reopen Control Center once so it reloads local service access. If it still shows this, open System."
                : "What to do: wait a few seconds and refresh once. If Today still does not load, open System.";
        }

        if (!status.ServiceRunning)
        {
            return "What to do: open System and restart the service if it stays offline.";
        }

        if (!status.LocalApiAuthReady)
        {
            return "What to do: reopen Control Center so it reloads local service access. If it still shows this, open System.";
        }

        if (!status.ConsentActive)
        {
            return "What to do: open Tracking and grant consent when you are ready to start tracking.";
        }

        if (status.Paused)
        {
            return "What to do: resume tracking when you are ready, then return to Today.";
        }

        if (!statusFresh)
        {
            return dbBusyRetrying
                ? "What to do: wait a few seconds while Ari refreshes Today, then refresh once."
                : "What to do: open System and refresh once. If Today stays stale, sign out and back in.";
        }

        if (!string.IsNullOrWhiteSpace(captureHint))
        {
            return "What to do: keep using the keyboard or mouse for a few seconds while Ari reconnects. Open Tracking if you want to review tracking quality, then open System if recovery stays stuck.";
        }

        return "What to do: you can close Control Center now. Reopen Ari any time from Start Menu, the desktop shortcut, or the Ari tray icon.";
    }
}
