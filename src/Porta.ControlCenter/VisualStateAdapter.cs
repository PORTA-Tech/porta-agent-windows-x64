using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal enum PortaVisualMode
{
    Unavailable,
    Normal,
    DeepWork,
    Overload
}

internal sealed record PortaVisualState(
    PortaVisualMode Mode,
    Color AccentColor,
    string ModeLabel,
    string TrustLabel,
    string FocusStreakText,
    string FocusStatusText,
    string OperatingSentence,
    bool OverloadActive,
    bool DeepWorkActive,
    bool ShowRecoveryBanner,
    string RecoveryBannerText,
    string WhySummary);

internal static class VisualStateAdapter
{
    private const string NotAvailableText = "Not available";
    private const string WaitingForServiceDataText = "Waiting for service data";
    private static readonly TimeSpan RecoveryBannerWindow = TimeSpan.FromMinutes(20);

    public static PortaVisualState Build(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        string fallbackExplanation,
        DateTimeOffset nowUtc)
    {
        if (snapshot == null)
        {
            string waitingReason = string.IsNullOrWhiteSpace(fallbackExplanation)
                ? WaitingForServiceDataText
                : fallbackExplanation;
            return new PortaVisualState(
                Mode: PortaVisualMode.Unavailable,
                AccentColor: Color.FromRgb(107, 116, 128),
                ModeLabel: "Unavailable",
                TrustLabel: NotAvailableText,
                FocusStreakText: $"Focused time: {NotAvailableText}",
                FocusStatusText: waitingReason,
                OperatingSentence: waitingReason,
                OverloadActive: false,
                DeepWorkActive: false,
                ShowRecoveryBanner: false,
                RecoveryBannerText: string.Empty,
                WhySummary: NotAvailableText);
        }

        bool deepWorkActive = TryReadDeepWorkActive(snapshot.StateFlagsJson);
        bool overloadActive = snapshot.OverloadActive;
        PortaVisualMode mode = ResolveMode(overloadActive, deepWorkActive);
        Color accent = mode switch
        {
            PortaVisualMode.Overload => Color.FromRgb(255, 180, 84),
            PortaVisualMode.DeepWork => Color.FromRgb(94, 200, 255),
            _ => Color.FromRgb(127, 209, 163)
        };

        string focusStatus = BuildFocusStatus(snapshot);
        string operatingSentence = BuildOperatingSentence(status, snapshot, mode, fallbackExplanation);
        (bool showRecoveryBanner, string recoveryBannerText) = BuildRecoveryBanner(mode, recentEvents, nowUtc);
        string whySummary = BuildWhySummary(snapshot, recentEvents);

        return new PortaVisualState(
            Mode: mode,
            AccentColor: accent,
            ModeLabel: mode.ToString(),
            TrustLabel: MapTrustLabel(snapshot.Confidence),
            FocusStreakText: $"Focused time: {FormatFocusedTime(snapshot.SustainedAttentionDurationSec)}",
            FocusStatusText: focusStatus,
            OperatingSentence: operatingSentence,
            OverloadActive: overloadActive,
            DeepWorkActive: deepWorkActive,
            ShowRecoveryBanner: showRecoveryBanner,
            RecoveryBannerText: recoveryBannerText,
            WhySummary: whySummary);
    }

    private static string BuildOperatingSentence(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse snapshot,
        PortaVisualMode mode,
        string fallbackExplanation)
    {
        if (status?.Paused == true)
        {
            if (status.PauseUntilUtc.HasValue && status.PauseUntilUtc.Value > DateTimeOffset.UtcNow)
            {
                return $"Paused until {status.PauseUntilUtc.Value.ToLocalTime():h:mm tt}";
            }

            return "Paused.";
        }

        if (status?.ConsentActive == false)
        {
            return "Waiting for consent before collecting.";
        }

        if (mode == PortaVisualMode.DeepWork)
        {
            return "Deep Work mode active: minimizing interruptions.";
        }

        if (mode == PortaVisualMode.Overload)
        {
            return "Overload regulation is active.";
        }

        if (snapshot.BaselineReady == false)
        {
            return "Calibrating before suggesting actions.";
        }

        if (!string.IsNullOrWhiteSpace(fallbackExplanation))
        {
            return fallbackExplanation;
        }

        return "Ready to suggest breaks when needed.";
    }

    private static (bool Show, string Message) BuildRecoveryBanner(
        PortaVisualMode mode,
        IReadOnlyList<LocalApiEventResponse> recentEvents,
        DateTimeOffset nowUtc)
    {
        if (mode == PortaVisualMode.Overload)
        {
            return (false, string.Empty);
        }

        LocalApiEventResponse? recoveryEvent = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault(static evt =>
                string.Equals(evt.Action, "overload_brightness_restored", StringComparison.OrdinalIgnoreCase)
                || string.Equals(evt.Type, "overload_brightness_restored", StringComparison.OrdinalIgnoreCase));
        if (recoveryEvent == null)
        {
            return (false, string.Empty);
        }

        if ((nowUtc - recoveryEvent.TsUtc) > RecoveryBannerWindow)
        {
            return (false, string.Empty);
        }

        return (true, "Recovery complete: overload mitigation returned to normal.");
    }

    private static string BuildWhySummary(
        LocalApiCurrentSnapshotResponse snapshot,
        IReadOnlyList<LocalApiEventResponse> recentEvents)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.ContributorsJson))
        {
            return "Live signal explain data is available.";
        }

        LocalApiEventResponse? latest = recentEvents
            .OrderByDescending(static evt => evt.TsUtc)
            .FirstOrDefault();
        if (latest == null || string.IsNullOrWhiteSpace(latest.WhyJson))
        {
            return NotAvailableText;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(latest.WhyJson);
            JsonElement root = doc.RootElement;
            string reason = root.TryGetProperty("reason", out JsonElement reasonEl)
                ? reasonEl.GetString() ?? string.Empty
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                return reason;
            }
        }
        catch
        {
        }

        return NotAvailableText;
    }

    private static PortaVisualMode ResolveMode(bool overloadActive, bool deepWorkActive)
    {
        // Pinned visual priority model:
        // overload wins, then deep work, then normal.
        if (overloadActive)
        {
            return PortaVisualMode.Overload;
        }

        if (deepWorkActive)
        {
            return PortaVisualMode.DeepWork;
        }

        return PortaVisualMode.Normal;
    }

    private static bool TryReadDeepWorkActive(string? stateFlagsJson)
    {
        if (string.IsNullOrWhiteSpace(stateFlagsJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateFlagsJson);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("deep_work_active", out JsonElement value))
            {
                return false;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.TryGetInt32(out int n) && n == 1,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFocusStatus(LocalApiCurrentSnapshotResponse snapshot)
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

    private static string MapTrustLabel(double confidence)
    {
        if (confidence >= 0.85d)
        {
            return "High";
        }

        return confidence >= 0.65d ? "Medium" : "Low";
    }

    private static string FormatFocusedTime(double sustainedAttentionSeconds)
    {
        if (sustainedAttentionSeconds < 60d)
        {
            return $"{Math.Max(0d, sustainedAttentionSeconds):F0} sec";
        }

        double minutes = Math.Floor(sustainedAttentionSeconds / 60d);
        return $"{minutes.ToString("F0", CultureInfo.InvariantCulture)} min";
    }
}
