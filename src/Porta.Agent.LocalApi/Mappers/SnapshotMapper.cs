using System.Text.Json;
using System.Text.Json.Nodes;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi.Mappers;

internal static class SnapshotMapper
{
    private const string SchemaV1EmptyJson = "{\"schema_ver\":1}";

    public static LocalApiCurrentSnapshotResponse Map(
        AgentBrainSnapshot snapshot,
        AgentLatestStateDetails? latestState)
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
        LocalApiCooldownsRemainingResponse cooldownsRemaining = BuildCooldownsRemaining(normalizedStateFlagsJson);

        overloadActive = ReadBoolFlag(normalizedStateFlagsJson, "overload_active", overloadActive);
        bool deepWorkActive = ReadBoolFlag(normalizedStateFlagsJson, "deep_work_active");
        bool notificationsSuppressed = ReadBoolFlag(normalizedStateFlagsJson, "notifications_suppressed");
        bool brightnessAdjusted = ReadBoolFlag(normalizedStateFlagsJson, "brightness_adjusted");
        (double? fComponent, double? iComponent, double? vComponent) = ReadFivComponents(normalizedContributorsJson);

        // Pinned null/partial behavior: if there is no finalized state row, keep endpoint healthy
        // with safe defaults and explicit cold-start semantics.
        bool baselineReady = latestState?.BaselineReady ?? false;
        bool coldStart = latestState?.ColdStart ?? !baselineReady;
        string calibrationStage = CalibrationTruth.NormalizeStage(
            latestState?.CalibrationStage ?? (baselineReady
                ? CalibrationTruth.StageCalibrated
                : (coldStart ? CalibrationTruth.StageCold : CalibrationTruth.StageProvisional)));
        bool systemActionsEnabled = latestState?.SystemActionsEnabled ?? CalibrationTruth.AllowsSystemActions(calibrationStage);
        string trustTier = latestState?.TrustTier ?? CalibrationTruth.ResolveTrustTier(calibrationStage, confidence);

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
            vComponent);
    }

    private static LocalApiCooldownsRemainingResponse BuildCooldownsRemaining(string? stateFlagsJson)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int deepWorkNotificationEnableSeconds = RemainingSeconds(ReadInt64Flag(stateFlagsJson, "notification_enable_cooldown_until_utc_ms"), nowMs);
        int deepWorkNotificationMinOnSeconds = RemainingSeconds(ReadInt64Flag(stateFlagsJson, "notification_min_on_until_utc_ms"), nowMs);
        int brightnessActionSeconds = RemainingSeconds(ReadInt64Flag(stateFlagsJson, "brightness_cooldown_until_utc_ms"), nowMs);
        int breakSuggestionSeconds = RemainingSeconds(
            ReadInt64Flag(stateFlagsJson, "break_cooldown_until_utc_ms", "cooldown_end_ts_ms"),
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

    private static (double? F, double? I, double? V) ReadFivComponents(string? contributorsJson)
    {
        if (string.IsNullOrWhiteSpace(contributorsJson))
        {
            return (null, null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(contributorsJson);
            JsonElement root = doc.RootElement;
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
        catch
        {
            return (null, null, null);
        }
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
}
