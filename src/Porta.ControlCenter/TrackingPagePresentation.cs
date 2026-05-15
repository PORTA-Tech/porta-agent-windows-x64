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
    bool PauseResumeEnabled,
    // Quality chip: "Good" | "Fair" | "Settling" | "Limited" | "Paused" | "Updating"
    string QualityChipLabel,
    // Progress 0.0–1.0 for the quality bar
    double QualityProgress);

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
        (string qualityChipLabel, double qualityProgress) = BuildQualityChip(status, snapshot);

        return new TrackingPagePresentation(
            CollectionSummary: collectionSummary,
            QualityHeadline: qualityHeadline,
            QualitySummary: qualitySummary,
            QualityHelp: qualityHelp,
            ConsentSummary: consentSummary,
            PrivacySummary: privacySummary,
            ControlsSummary: controlsSummary,
            PauseResumeLabel: pauseResumeLabel,
            PauseResumeEnabled: pauseResumeEnabled,
            QualityChipLabel: qualityChipLabel,
            QualityProgress: qualityProgress);
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
        var (label, _) = BuildQualityChip(status, snapshot);
        return label switch
        {
            "Paused"   => "Tracking is paused.",
            "Updating" => "Tracking quality is updating.",
            "Good"     => "Tracking quality looks strong.",
            "Fair"     => "Tracking quality is good and still settling.",
            "Settling" => "Ari is calibrating your baseline — quality is building up.",
            _          => "Tracking is collecting data — quality will improve with use."
        };
    }

    /// <summary>
    /// Returns a human-readable chip label + progress (0–1) for the quality bar.
    /// Uses confidence and calibration state as the primary signals because
    /// CurrentQualityScore is often null and PartialData is almost always true early on.
    /// </summary>
    private static (string Label, double Progress) BuildQualityChip(
        LocalApiStatusResponse? status,
        LocalApiCurrentSnapshotResponse? snapshot)
    {
        if (status?.Paused == true)   return ("Paused", 0d);
        if (status?.ConsentActive == false) return ("No consent", 0d);
        if (snapshot == null)         return ("Updating", 0.15d);

        bool missingChannels = snapshot.MissingMajorChannels is { Count: > 0 };
        bool coldStart  = snapshot.ColdStart == true;
        bool analysisReady = snapshot.analysis_ready;
        double confidence  = System.Math.Clamp(snapshot.Confidence, 0d, 1d);
        double? rawScore   = snapshot.CurrentQualityScore;

        // Missing major capture channels → genuinely limited
        if (missingChannels)
            return ("Limited", 0.25d);

        // No analysis yet (very fresh session)
        if (!analysisReady && coldStart)
            return ("Settling", 0.40d);

        // Use the best available quality signal
        double effectiveScore = rawScore.HasValue
            ? rawScore.Value
            : confidence; // confidence is always populated and tracks real quality

        if (effectiveScore >= 0.72d)
            return ("Good",     System.Math.Clamp(effectiveScore, 0d, 1d));
        if (effectiveScore >= 0.45d)
            return ("Fair",     System.Math.Clamp(effectiveScore + 0.1d, 0d, 1d));
        if (analysisReady)
            return ("Settling", 0.45d);

        return ("Settling", 0.35d);
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
