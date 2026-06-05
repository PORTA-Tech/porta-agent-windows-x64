using System.Text.Json;
using System.Text.Json.Nodes;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi.Mappers;

public static class SnapshotMapper
{
    private const string SchemaV1EmptyJson = "{\"schema_ver\":1}";

    public static LocalApiCurrentSnapshotResponse Map(
        AgentBrainSnapshot snapshot,
        AgentLatestStateDetails? latestState,
        AgentServiceStatusSnapshot? status = null)
    {
        double overloadLikelihood = latestState?.OverloadLikelihood ?? snapshot.OverloadLikelihood;
        double confidence = latestState?.Confidence ?? snapshot.Confidence;
        double idleFragmentationIndex = latestState?.IdleFragmentationIndex ?? snapshot.IdleFragmentationIndex;
        string stateFlagsJson = latestState?.StateFlagsJson ?? snapshot.StateFlagsJson;
        string contributorsJson = latestState?.ContributorsJson ?? snapshot.ContributorsJson;
        bool overloadActive = latestState?.OverloadActive ?? snapshot.OverloadActive;
        double sustainedAttentionSeconds = latestState?.SustainedAttentionDurationSec ?? snapshot.SustainedAttentionDurationSec;
        double entropyNorm = Clamp01((latestState?.FocusAppEntropy ?? 0d) / 2d);
        double focusChoppinessIndex = Clamp01(
            (0.50d * idleFragmentationIndex)
            + (0.35d * Clamp01(snapshot.SmoothedFocusSwitchRate))
            + (0.15d * entropyNorm));

        string normalizedStateFlagsJson = EnsureSchemaV1Json(stateFlagsJson, SchemaV1EmptyJson);
        string normalizedContributorsJson = EnsureSchemaV1Json(contributorsJson, SchemaV1EmptyJson);
        using JsonObjectScope stateFlagsScope = ParseJsonObject(normalizedStateFlagsJson);
        using JsonObjectScope contributorsScope = ParseJsonObject(normalizedContributorsJson);

        LocalApiCooldownsRemainingResponse cooldownsRemaining = BuildCooldownsRemaining(stateFlagsScope.Root);

        overloadActive = ReadBoolFlag(stateFlagsScope.Root, "overload_active", overloadActive);
        bool deepWorkActive = ReadBoolFlag(stateFlagsScope.Root, "deep_work_active");
        bool notificationsSuppressed = ReadBoolFlag(stateFlagsScope.Root, "notifications_suppressed");
        bool brightnessAdjusted = ReadBoolFlag(stateFlagsScope.Root, "brightness_adjusted");
        double taskContinuityScore = ReadDoubleFlag(stateFlagsScope.Root, "task_continuity_score", fallback: 0d);
        double taskContinuitySeconds = ReadDoubleFlag(stateFlagsScope.Root, "task_continuity_seconds", fallback: sustainedAttentionSeconds);
        string continuityState = ReadStringFlag(stateFlagsScope.Root, "continuity_state", fallback: "reset");
        double continuityConfidence = ReadDoubleFlag(stateFlagsScope.Root, "continuity_confidence", fallback: confidence);
        string attentionSegmentKind = LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(
            ReadStringFlag(stateFlagsScope.Root, "attention_segment_kind", fallback: LocalAttentionSegmentAnalytics.FocusSegmentKind));
        double attentionSegmentSeconds = ReadDoubleFlag(stateFlagsScope.Root, "attention_segment_seconds", fallback: 0d);
        bool distractionEpisodeActive = ReadBoolFlag(
            stateFlagsScope.Root,
            "distraction_episode_active",
            fallback: string.Equals(attentionSegmentKind, LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind, StringComparison.Ordinal)
                || string.Equals(attentionSegmentKind, LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind, StringComparison.Ordinal));
        double distractionEpisodeSeconds = ReadDoubleFlag(
            stateFlagsScope.Root,
            "distraction_episode_seconds",
            fallback: distractionEpisodeActive ? attentionSegmentSeconds : 0d);
        string attentionSegmentReasonCode = LocalAttentionSegmentAnalytics.NormalizeReasonCode(
            ReadStringFlag(stateFlagsScope.Root, "attention_segment_reason_code", fallback: LocalAttentionSegmentAnalytics.SteadyFocusReasonCode));
        string attentionSegmentReasonText = ReadStringFlag(
            stateFlagsScope.Root,
            "attention_segment_reason_text",
            fallback: string.Empty);
        string focusEpisodeReasonCode = LocalAttentionSegmentAnalytics.NormalizeReasonCode(
            ReadStringFlag(stateFlagsScope.Root, "focus_episode_reason_code", fallback: LocalAttentionSegmentAnalytics.FocusEpisodeActiveReasonCode));
        string focusEpisodeReasonText = ReadStringFlag(
            stateFlagsScope.Root,
            "focus_episode_reason_text",
            fallback: string.Empty);
        if (string.IsNullOrWhiteSpace(attentionSegmentReasonText))
        {
            attentionSegmentReasonText = attentionSegmentKind switch
            {
                LocalAttentionSegmentAnalytics.AwaySegmentKind => $"Away for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
                LocalAttentionSegmentAnalytics.ContextExcursionSegmentKind => $"Outside focus context for {FormatEpisodeDuration(attentionSegmentSeconds)}.",
                LocalAttentionSegmentAnalytics.OffTaskDistractionSegmentKind => $"Stayed outside focus context for {FormatEpisodeDuration(distractionEpisodeSeconds > 0d ? distractionEpisodeSeconds : attentionSegmentSeconds)}.",
                LocalAttentionSegmentAnalytics.FragmentationDistractionSegmentKind => $"Rapid switching / fragmentation for {FormatEpisodeDuration(distractionEpisodeSeconds > 0d ? distractionEpisodeSeconds : attentionSegmentSeconds)}.",
                _ => "Steady focus episode."
            };
        }
        if (string.IsNullOrWhiteSpace(focusEpisodeReasonText))
        {
            focusEpisodeReasonText = continuityState switch
            {
                "reset" => "Focus episode ended.",
                "pause" => "Focus episode is paused.",
                "decay" => "Focus episode is fading.",
                _ => "Focus episode active."
            };
        }
        double signalQualityScore = ReadDoubleFlag(
            stateFlagsScope.Root,
            "signal_quality_score",
            fallback: latestState?.CurrentQualityScore ?? 0d);
        double modelConfidence = ReadDoubleFlag(
            stateFlagsScope.Root,
            "model_confidence",
            fallback: confidence);
        double pressureLevel = ReadDoubleFlag(
            stateFlagsScope.Root,
            "pressure_level",
            fallback: overloadLikelihood);
        (double? fComponent, double? iComponent, double? vComponent) = ReadFivComponents(contributorsScope.Root);

        // Pinned null/partial behavior: if there is no finalized state row, keep endpoint healthy
        // with safe defaults and explicit cold-start semantics.
        bool baselineReady = latestState?.BaselineReady ?? false;
        bool coldStart = latestState?.ColdStart ?? !baselineReady;
        string calibrationStage = CalibrationTruth.NormalizeStage(
            latestState?.CalibrationStage ?? (baselineReady
                ? CalibrationTruth.StageCalibrated
                : (coldStart ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional)));
        bool systemActionsEnabled = latestState?.SystemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(calibrationStage);
        string trustTier = latestState?.TrustTier ?? CalibrationTruth.ResolveTrustTier(
            calibrationStage,
            confidence,
            latestState?.CurrentQualityScore ?? signalQualityScore,
            latestState?.PartialData == true);

        return new LocalApiCurrentSnapshotResponse(
            snapshot.TimestampUtc,
            snapshot.TypingBurstIntensity,
            snapshot.ScrollVelocityIndex,
            snapshot.FocusSwitchRate,
            snapshot.SmoothedTypingBurstIntensity,
            snapshot.SmoothedScrollVelocityIndex,
            snapshot.SmoothedFocusSwitchRate,
            snapshot.FocusStrength,
            overloadLikelihood,
            snapshot.FatigueProbability,
            snapshot.CognitiveStability,
            snapshot.DominantStateValue,
            snapshot.DominantStateName,
            confidence,
            snapshot.LastAction,
            snapshot.ActivationWindowTriggered,
            snapshot.CooldownTriggered,
            snapshot.ActivationWindowOpen,
            snapshot.Contributors,
            sustainedAttentionSeconds,
            idleFragmentationIndex,
            focusChoppinessIndex,
            overloadActive,
            normalizedStateFlagsJson,
            normalizedContributorsJson,
            baselineReady,
            latestState?.BaselineReadyPct,
            latestState?.AvgQuality2m,
            coldStart,
            calibrationStage,
            systemActionsEnabled,
            trustTier,
            latestState?.CurrentQualityScore,
            latestState?.PartialData,
            latestState?.MissingMajorChannels,
            cooldownsRemaining,
            deepWorkActive,
            notificationsSuppressed,
            brightnessAdjusted,
            fComponent,
            iComponent,
            vComponent,
            taskContinuityScore,
            taskContinuitySeconds,
            continuityState,
            continuityConfidence,
            signalQualityScore,
            modelConfidence,
            latestState?.BreakPendingAction,
            latestState?.BreakPendingKind,
            latestState?.BreakPendingReasonCode,
            latestState?.BreakPendingEffectiveFocusSeconds,
            latestState?.BreakPendingEffectiveFocusWindowSeconds,
            latestState?.BreakDeliveryBoundaryReached,
            latestState?.BreakDeliveryBoundaryKind,
            latestState?.BreakShortBreakEffectiveFocusSeconds,
            latestState?.BreakLongBreakEffectiveFocusSeconds,
            status?.RuntimeStateFreshness ?? "unknown",
            status?.RuntimeStateLastSuccessfulRefreshUtc,
            status?.RuntimeStateLastAttemptUtc,
            status?.RuntimeStateRefreshError,
            AnalyzedStateAvailable: latestState is not null,
            ModelSource: latestState is not null ? "analyzed_state" : "brain_fallback",
            FocusEpisodeQuality: continuityConfidence,
            FocusEpisodeSeconds: ResolveStoredFocusEpisodeSeconds(attentionSegmentKind, taskContinuitySeconds),
            FocusEpisodeState: continuityState,
            AttentionSegmentKind: attentionSegmentKind,
            AttentionSegmentSeconds: attentionSegmentSeconds,
            DistractionEpisodeActive: distractionEpisodeActive,
            DistractionEpisodeSeconds: distractionEpisodeSeconds,
            AttentionSegmentReasonCode: attentionSegmentReasonCode,
            AttentionSegmentReasonText: attentionSegmentReasonText,
            FocusEpisodeReasonCode: focusEpisodeReasonCode,
            FocusEpisodeReasonText: focusEpisodeReasonText,
            PressureLevel: pressureLevel,
            PressureConfidence: modelConfidence);
    }

    private static LocalApiCooldownsRemainingResponse BuildCooldownsRemaining(JsonElement root)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int deepWorkNotificationEnableSeconds = RemainingSeconds(ReadInt64Flag(root, "notification_enable_cooldown_until_utc_ms"), nowMs);
        int deepWorkNotificationMinOnSeconds = RemainingSeconds(ReadInt64Flag(root, "notification_min_on_until_utc_ms"), nowMs);
        int brightnessActionSeconds = RemainingSeconds(ReadInt64Flag(root, "brightness_cooldown_until_utc_ms"), nowMs);
        int breakSuggestionSeconds = RemainingSeconds(
            ReadInt64Flag(root, "break_cooldown_until_utc_ms", "cooldown_end_ts_ms"),
            nowMs);

        return new LocalApiCooldownsRemainingResponse(
            BreakSuggestionSecRemaining: breakSuggestionSeconds,
            DeepWorkNotificationEnableSecRemaining: deepWorkNotificationEnableSeconds,
            BrightnessActionSecRemaining: brightnessActionSeconds,
            DeepWorkNotificationMinOnSecRemaining: deepWorkNotificationMinOnSeconds,
            AnyActive: deepWorkNotificationEnableSeconds > 0
                || deepWorkNotificationMinOnSeconds > 0
                || brightnessActionSeconds > 0
                || breakSuggestionSeconds > 0);
    }

    private static int RemainingSeconds(long untilUtcMs, long nowUtcMs)
    {
        if (untilUtcMs <= 0 || untilUtcMs <= nowUtcMs)
        {
            return 0;
        }

        return (int)Math.Ceiling((untilUtcMs - nowUtcMs) / 1000d);
    }

    private static string FormatEpisodeDuration(double seconds)
    {
        int totalSeconds = Math.Max(0, (int)Math.Round(seconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int secs = totalSeconds % 60;
        if (hours > 0)
        {
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }

        return secs > 0 && minutes < 3 ? $"{minutes}m {secs}s" : $"{minutes}m";
    }

    private static JsonObjectScope ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return default;
            }

            return new JsonObjectScope(document);
        }
        catch
        {
            return default;
        }
    }

    private static long ReadInt64Flag(string? json, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(json) || names.Length == 0)
        {
            return 0;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            foreach (string name in names)
            {
                if (root.TryGetProperty(name, out JsonElement node)
                    && node.ValueKind == JsonValueKind.Number
                    && node.TryGetInt64(out long value))
                {
                    return Math.Max(0, value);
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static long ReadInt64Flag(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || names.Length == 0)
        {
            return 0;
        }

        foreach (string name in names)
        {
            if (root.TryGetProperty(name, out JsonElement node)
                && node.ValueKind == JsonValueKind.Number
                && node.TryGetInt64(out long value))
            {
                return Math.Max(0, value);
            }
        }

        return 0;
    }

    private static bool ReadBoolFlag(string? json, string name, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out JsonElement node))
            {
                return fallback;
            }

            return node.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when node.TryGetInt32(out int numeric) => numeric != 0,
                _ => fallback
            };
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBoolFlag(JsonElement root, string name, bool fallback = false)
    {
        if (root.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(name)
            || !root.TryGetProperty(name, out JsonElement node))
        {
            return fallback;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when node.TryGetInt32(out int numeric) => numeric != 0,
            _ => fallback
        };
    }

    private static double ReadDoubleFlag(string? json, string name, double fallback = 0d)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out JsonElement node))
            {
                return fallback;
            }

            if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out double numeric))
            {
                return numeric;
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static double ReadDoubleFlag(JsonElement root, string name, double fallback = 0d)
    {
        if (root.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(name)
            || !root.TryGetProperty(name, out JsonElement node))
        {
            return fallback;
        }

        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out double numeric))
        {
            return numeric;
        }

        return fallback;
    }

    private static string ReadStringFlag(string? json, string name, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out JsonElement node)
                || node.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }

            return node.GetString() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadStringFlag(JsonElement root, string name, string fallback = "")
    {
        if (root.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(name)
            || !root.TryGetProperty(name, out JsonElement node)
            || node.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        return node.GetString() ?? fallback;
    }

    private static (double? F, double? I, double? V) ReadFivComponents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("components", out JsonElement components)
            || components.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        return (
            ReadComponentValue(components, "F"),
            ReadComponentValue(components, "I"),
            ReadComponentValue(components, "V"));
    }

    private static double? ReadComponentValue(JsonElement components, string key)
    {
        if (!components.TryGetProperty(key, out JsonElement component)
            || component.ValueKind != JsonValueKind.Object
            || !component.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out double parsed))
        {
            return null;
        }

        return parsed;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

    private static string EnsureSchemaV1Json(string? json, string fallbackJson)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallbackJson;
        }

        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is not JsonObject obj)
            {
                return fallbackJson;
            }

            if (obj["schema_ver"] is null)
            {
                obj["schema_ver"] = 1;
            }

            return obj.ToJsonString();
        }
        catch
        {
            return fallbackJson;
        }
    }

    private readonly record struct JsonObjectScope(JsonDocument? Document) : IDisposable
    {
        public JsonElement Root => Document?.RootElement ?? default;

        public void Dispose()
        {
            Document?.Dispose();
        }
    }

    private static double ResolveStoredFocusEpisodeSeconds(string? attentionSegmentKind, double taskContinuitySeconds)
    {
        string kind = LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind(attentionSegmentKind);
        return LocalAttentionSegmentAnalytics.IsDistractionKind(kind)
               || kind == LocalAttentionSegmentAnalytics.AwaySegmentKind
            ? 0d
            : taskContinuitySeconds;
    }
}
