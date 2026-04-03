using Porta.Agent.LocalApi;

namespace Porta.ControlCenter;

internal sealed record TrackingPagePresentation(
    string CollectionSummary,
    string QualityHeadline,
    string QualitySummary,
    string QualityHelp,
    string ConsentSummary,
    string PrivacySummary,
    string ControlsSummary,
    string PauseResumeLabel,
    bool PauseResumeEnabled);

internal static class TrackingPagePresentationBuilder
{
    public static TrackingPagePresentation Build(
        LocalApiStatusResponse? status,
        LocalApiSettingsResponse? settings,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiInputsSummaryResponse? inputsSummary,
        LocalApiRetentionResponse? retention)
    {
        _ = retention;
        string collectionSummary = BuildCollectionSummary(status);
        string qualityHeadline = BuildQualityHeadline(status, snapshot);
        string qualitySummary = BuildQualitySummary(status, snapshot);
        string qualityHelp = BuildQualityHelp(status, snapshot, inputsSummary);
        string consentSummary = BuildConsentSummary(status);
        string privacySummary = BuildPrivacySummary(settings);
        string controlsSummary = BuildControlsSummary(settings);
        string pauseResumeLabel = status?.Paused == true ? "Resume tracking" : "Pause tracking";
        bool pauseResumeEnabled = status != null;

        return new TrackingPagePresentation(
            CollectionSummary: collectionSummary,
            QualityHeadline: qualityHeadline,
            QualitySummary: qualitySummary,
            QualityHelp: qualityHelp,
            ConsentSummary: consentSummary,
            PrivacySummary: privacySummary,
            ControlsSummary: controlsSummary,
            PauseResumeLabel: pauseResumeLabel,
            PauseResumeEnabled: pauseResumeEnabled);
    }

    private static string BuildCollectionSummary(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return "Ari has not loaded local tracking status yet.";
        }

        if (status.Paused)
        {
            return "Tracking is paused, so Ari is not observing new activity right now.";
        }

        if (!status.ConsentActive)
        {
            return "Ari is ready, but it will not collect anything until you grant consent.";
        }

        return "Ari is tracking pattern-level activity from your signed-in Windows session.";
    }

    private static string BuildQualityHeadline(LocalApiStatusResponse? status, LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (status?.Paused == true)
        {
            return "Tracking is paused.";
        }

        if (status?.ConsentActive == false)
        {
            return "Tracking is waiting for your consent.";
        }

        if (snapshot == null)
        {
            return "Tracking quality is still filling in.";
        }

        if (snapshot.PartialData == true || snapshot.MissingMajorChannels is { Count: > 0 })
        {
            return "Tracking quality is limited right now.";
        }

        double score = snapshot.CurrentQualityScore ?? 0d;
        if (score >= 0.85d)
        {
            return "Tracking quality looks strong.";
        }

        if (score >= 0.60d)
        {
            return "Tracking quality is usable and still settling.";
        }

        return "Tracking quality is limited right now.";
    }

    private static string BuildQualitySummary(LocalApiStatusResponse? status, LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (status?.Paused == true)
        {
            return "Ari is not collecting while tracking is paused.";
        }

        if (status?.ConsentActive == false)
        {
            return "Ari does not collect anything until you grant consent.";
        }

        if (snapshot == null)
        {
            return "Ari is connected, but it still needs a little more recent activity to fill in today's pattern data.";
        }

        if (snapshot.MissingMajorChannels is { Count: > 0 })
        {
            return $"Ari is currently missing {HumanizeMissingChannels(snapshot.MissingMajorChannels)} pattern data.";
        }

        if (snapshot.PartialData == true)
        {
            return "Ari is collecting, but today's picture is still partial.";
        }

        return "Ari has enough recent pattern data to describe your current focus more clearly.";
    }

    private static string BuildQualityHelp(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot,
        LocalApiInputsSummaryResponse? inputsSummary)
    {
        if (status?.Paused == true)
        {
            return "Resume tracking when you are ready to continue.";
        }

        if (status?.ConsentActive == false)
        {
            return "Grant consent to start tracking again.";
        }

        string? captureFailure = DescribeCaptureFailure(status?.CaptureFailureReason);
        if (!string.IsNullOrWhiteSpace(captureFailure))
        {
            return captureFailure;
        }

        if (snapshot?.MissingMajorChannels is { Count: > 0 })
        {
            return "A short stretch of normal keyboard or mouse activity in your signed-in session usually clears this up.";
        }

        if (inputsSummary != null && inputsSummary.SampleCount == 0)
        {
            return "No recent pattern samples have arrived yet. A few seconds of normal activity usually fills this in.";
        }

        return "Best results come from a normal signed-in desktop session with a few seconds of keyboard or mouse activity.";
    }

    private static string BuildConsentSummary(LocalApiStatusResponse? status)
    {
        if (status == null)
        {
            return "Consent status is not available yet.";
        }

        return status.ConsentActive
            ? "On. Ari can collect pattern-level activity."
            : "Needed. Ari will not collect anything until you grant consent.";
    }

    private static string BuildPrivacySummary(LocalApiSettingsResponse? settings)
    {
        if (settings == null)
        {
            return "Privacy mode is not available yet.";
        }

        string level = settings.PrivacyLevel?.Trim().ToLowerInvariant() switch
        {
            "minimal" => "Minimal",
            "detailed" => "Detailed",
            "patterns_only" => "Patterns only",
            _ => "Standard"
        };

        return $"{level}. Ari tracks patterns instead of content.";
    }

    private static string BuildControlsSummary(LocalApiSettingsResponse? settings)
    {
        if (settings == null)
        {
            return "Controls are not available yet.";
        }

        var enabled = new List<string>();
        if (settings.NotificationReductionEnabled)
        {
            enabled.Add("reduce notifications");
        }

        if (settings.FocusModeEnabled)
        {
            enabled.Add("focus nudges");
        }

        if (settings.AnimationReductionEnabled)
        {
            enabled.Add("reduce animations");
        }

        if (settings.TemporaryAppRestrictionEnabled)
        {
            enabled.Add("temporary app restriction");
        }

        if (enabled.Count == 0)
        {
            return "Recommendations only. Ari is not making automatic desktop changes.";
        }

        return $"Enabled now: {string.Join(", ", enabled)}.";
    }

    private static string HumanizeMissingChannels(IReadOnlyList<string> channels)
    {
        string[] readable = channels
            .Select(static channel => channel.Trim().ToLowerInvariant() switch
            {
                "keyboard" => "keyboard",
                "mouse" => "mouse",
                "focus" => "app focus",
                "idle" => "activity gap",
                _ => channel.Trim().ToLowerInvariant()
            })
            .Where(static channel => !string.IsNullOrWhiteSpace(channel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return readable.Length == 0
            ? "some"
            : string.Join(", ", readable);
    }

    private static string? DescribeCaptureFailure(string? captureFailureReason)
    {
        return captureFailureReason switch
        {
            "USER_AGENT_HEARTBEAT_STALE" => "Ari is still reconnecting to the current desktop session.",
            "USER_AGENT_SESSION_SWITCHED" => "Ari is still switching over to your current desktop session.",
            "USER_AGENT_INSTANCE_MISMATCH" => "Ari is still syncing the current tracking session.",
            "USER_AGENT_INPUT_SELF_TEST_FAILED" => "Ari saw activity, but the local capture check did not confirm it yet.",
            "USER_AGENT_KEYBOARD_SIGNAL_MISSING" => "Keyboard activity is not being detected right now.",
            "USER_AGENT_MOUSE_SIGNAL_MISSING" => "Mouse activity is not being detected right now.",
            "CAPTURE_CONTEXT_SERVICE_SESSION" => "Ari is attached to a background session instead of your active desktop session.",
            "CAPTURE_CONTEXT_UNKNOWN" => "Ari has not attached to your desktop session yet.",
            _ => null
        };
    }
}
