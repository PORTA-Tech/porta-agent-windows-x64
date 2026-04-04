using Porta.Agent.Core;

namespace Porta.Agent.LocalApi;

public enum UserAgentVisualMode
{
    Unavailable,
    Normal,
    DeepWork,
    Overload
}

public sealed record UserAgentVisualSignals(
    UserAgentVisualMode Mode,
    string TooltipText,
    bool ShowDeepWorkStreak,
    string DeepWorkStreakText,
    bool ShowRecoveryToast,
    long RecoveryEventId,
    string RecoveryMessage);

public static class UserAgentVisualSignalsAdapter
{
    private const string WaitingForServiceDataText = "PORTA | Waiting for service data";

    public static UserAgentVisualSignals Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc,
        long lastRecoveryEventId)
    {
        if (status == null || snapshot == null)
        {
            return new UserAgentVisualSignals(
                Mode: UserAgentVisualMode.Unavailable,
                TooltipText: WaitingForServiceDataText,
                ShowDeepWorkStreak: false,
                DeepWorkStreakText: string.Empty,
                ShowRecoveryToast: false,
                RecoveryEventId: 0,
                RecoveryMessage: string.Empty);
        }

        LocalCurrentExperienceState experience = LocalCurrentExperienceStateAdapter.Build(
            status,
            snapshot,
            liveSession: null,
            recentEvents,
            nowUtc);
        UserAgentVisualMode mode = MapMode(experience.Mode);
        (bool showRecoveryToast, long recoveryEventId, string recoveryMessage) = BuildRecoveryToast(
            status.TrustTier,
            mode,
            experience,
            lastRecoveryEventId);

        return new UserAgentVisualSignals(
            Mode: mode,
            TooltipText: BuildTooltip(status, snapshot, mode, experience.ShowDeepWorkStreak, experience.DeepWorkStreakText),
            ShowDeepWorkStreak: experience.ShowDeepWorkStreak,
            DeepWorkStreakText: experience.DeepWorkStreakText,
            ShowRecoveryToast: showRecoveryToast,
            RecoveryEventId: recoveryEventId,
            RecoveryMessage: recoveryMessage);
    }

    private static string BuildTooltip(
        LocalApiStatusResponse status,
        LocalApiCurrentSnapshotResponse snapshot,
        UserAgentVisualMode mode,
        bool showDeepWorkStreak,
        string deepWorkStreakText)
    {
        if (!status.ServiceRunning)
        {
            return "PORTA | Core service unavailable";
        }

        if (!status.ConsentActive)
        {
            return "PORTA | Consent needed";
        }

        if (!string.Equals(status.CaptureContext, "UserSession", StringComparison.OrdinalIgnoreCase))
        {
            return "PORTA | User-session capture unavailable";
        }

        if (snapshot.PartialData == true && snapshot.MissingMajorChannels is { Count: > 0 })
        {
            string missing = string.Join("/", snapshot.MissingMajorChannels.Select(ToDisplayChannelName));
            return $"PORTA | Partial capture | Missing {missing}";
        }

        return mode switch
        {
            UserAgentVisualMode.DeepWork when showDeepWorkStreak => $"PORTA | {deepWorkStreakText}",
            UserAgentVisualMode.DeepWork => "PORTA | Deep Work active",
            UserAgentVisualMode.Overload => "PORTA | Overload regulation active",
            UserAgentVisualMode.Normal => "PORTA | Watching activity",
            _ => WaitingForServiceDataText
        };
    }

    private static (bool Show, long EventId, string Message) BuildRecoveryToast(
        string? trustTier,
        UserAgentVisualMode mode,
        LocalCurrentExperienceState experience,
        long lastRecoveryEventId)
    {
        if (!CalibrationTruth.AllowsSuggestionsForTrust(trustTier))
        {
            return (false, 0, string.Empty);
        }

        if (mode == UserAgentVisualMode.Overload
            || !experience.HasRecentRecoveryEvent
            || experience.RecentRecoveryEventId <= 0
            || experience.RecentRecoveryEventId == lastRecoveryEventId)
        {
            return (false, 0, string.Empty);
        }

        return (true, experience.RecentRecoveryEventId, experience.RecentRecoveryMessage);
    }

    private static UserAgentVisualMode MapMode(LocalCurrentExperienceMode mode)
    {
        return mode switch
        {
            LocalCurrentExperienceMode.DeepWork => UserAgentVisualMode.DeepWork,
            LocalCurrentExperienceMode.Overload => UserAgentVisualMode.Overload,
            LocalCurrentExperienceMode.Normal => UserAgentVisualMode.Normal,
            _ => UserAgentVisualMode.Unavailable
        };
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
}
