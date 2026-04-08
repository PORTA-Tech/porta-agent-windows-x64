using System.Text.Json;
using System.Text.Json.Serialization;

namespace Porta.Agent.Core;

public sealed record RuntimeOverloadStabilizationSettings(
    double ThresholdOn,
    double ThresholdOff,
    int DwellOnSeconds,
    int DwellOffSeconds,
    double ConfidenceMin,
    int GapAllowSeconds,
    double LowQualityPauseThreshold,
    int LowQualityDwellDecayMs);

public sealed record RuntimeBreakPolicySettings(
    int MicrobreakWindowSeconds,
    int MicrobreakEffectiveFocusSecondsLowOverload,
    int MicrobreakEffectiveFocusSecondsHighOverload,
    int ShortBreakWindowSeconds,
    int ShortBreakEffectiveFocusSecondsLowOverload,
    int ShortBreakEffectiveFocusSecondsHighOverload,
    int CooldownSeconds,
    double ConfidenceMin,
    double HighOverloadLikelihood,
    double DeliveryBoundaryIdleMinSeconds,
    double DeliveryBoundaryIdleMaxSeconds,
    double DeliveryBoundaryTypingQuietMax,
    double DeliveryBoundaryKeyboardBurstQuietMax);

public sealed record RuntimeDeepWorkDetectionSettings(
    int SustainedAttentionSeconds,
    double ConfidenceMin,
    double FragmentationMax,
    double VolatilityMax,
    double IntensityMin,
    double IntensityMax,
    int DwellOnSeconds,
    int DwellOffSeconds,
    int GapAllowSeconds);

public sealed record RuntimeNotificationSuppressionSettings(
    int FailureBackoffSeconds,
    int EnableCooldownSeconds,
    int MinOnSeconds);

public sealed record RuntimeBrightnessAdjustmentSettings(
    int ReductionDelta,
    int MinSafeBrightness,
    int RampStepPerBucket,
    int CooldownSeconds,
    int FailureBackoffSeconds);

public sealed record RuntimePolicySettings(
    RuntimeOverloadStabilizationSettings OverloadStabilization,
    RuntimeBreakPolicySettings BreakPolicy,
    RuntimeDeepWorkDetectionSettings DeepWorkDetection,
    RuntimeNotificationSuppressionSettings NotificationSuppression,
    RuntimeBrightnessAdjustmentSettings BrightnessAdjustment);

public sealed record RuntimePolicyEnvelope(
    string Version,
    RuntimePolicySettings Settings);

public interface IRuntimePolicyProvider
{
    RuntimePolicyEnvelope GetEnvelope();
}

public sealed class FileRuntimePolicyProvider : IRuntimePolicyProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private RuntimePolicyEnvelope _cached = EmptyEnvelope();
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFileWriteUtc = DateTimeOffset.MinValue;
    private readonly string _envelopePath;

    public FileRuntimePolicyProvider()
    {
        _envelopePath = ResolveEnvelopePath();
    }

    internal FileRuntimePolicyProvider(string envelopePath)
    {
        _envelopePath = string.IsNullOrWhiteSpace(envelopePath)
            ? ResolveEnvelopePath()
            : envelopePath;
    }

    public RuntimePolicyEnvelope GetEnvelope()
    {
        lock (_gate)
        {
            DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
            if ((nowUtc - _lastRefreshUtc) < RefreshInterval)
            {
                return _cached;
            }

            _lastRefreshUtc = nowUtc;
            if (!File.Exists(_envelopePath))
            {
                _cached = EmptyEnvelope();
                _lastFileWriteUtc = DateTimeOffset.MinValue;
                return _cached;
            }

            DateTimeOffset fileWriteUtc = File.GetLastWriteTimeUtc(_envelopePath);
            if (fileWriteUtc == _lastFileWriteUtc)
            {
                return _cached;
            }

            _lastFileWriteUtc = fileWriteUtc;
            try
            {
                string raw = File.ReadAllText(_envelopePath);
                RuntimeEnvelopeDocument? parsed = JsonSerializer.Deserialize<RuntimeEnvelopeDocument>(raw, JsonOptions);
                _cached = ResolveEnvelope(parsed);
            }
            catch
            {
                _cached = EmptyEnvelope();
            }

            return _cached;
        }
    }

    private static string ResolveEnvelopePath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("PORTA_POLICY_ENVELOPE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath.Trim();
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(root, "PortaAgent", "policy-envelope.json");
    }

    private static RuntimePolicyEnvelope ResolveEnvelope(RuntimeEnvelopeDocument? parsed)
    {
        RuntimePolicySettings defaults = CreateDefaultSettings();
        if (parsed == null)
        {
            return EmptyEnvelope();
        }

        RuntimeOverloadStabilizationSettings overload = ResolveOverload(parsed.Detectors?.Overload, defaults.OverloadStabilization);
        RuntimeBreakPolicySettings breakPolicy = ResolveBreak(parsed.Delivery?.BreakSuggestions, defaults.BreakPolicy, overload);
        RuntimeDeepWorkDetectionSettings deepWork = ResolveDeepWork(parsed.Detectors?.DeepWork, defaults.DeepWorkDetection);
        RuntimeNotificationSuppressionSettings notificationSuppression = ResolveNotificationSuppression(parsed.Constraints?.Notifications, defaults.NotificationSuppression);
        RuntimeBrightnessAdjustmentSettings brightness = ResolveBrightness(parsed.Constraints?.Brightness, defaults.BrightnessAdjustment);

        string version = string.IsNullOrWhiteSpace(parsed.PolicyVersion)
            ? (string.IsNullOrWhiteSpace(parsed.Version) ? "local-default" : parsed.Version.Trim())
            : parsed.PolicyVersion.Trim();
        return new RuntimePolicyEnvelope(
            Version: version,
            Settings: new RuntimePolicySettings(
                OverloadStabilization: overload,
                BreakPolicy: breakPolicy,
                DeepWorkDetection: deepWork,
                NotificationSuppression: notificationSuppression,
                BrightnessAdjustment: brightness));
    }

    private static RuntimeOverloadStabilizationSettings ResolveOverload(
        OverloadDetectorDocument? doc,
        RuntimeOverloadStabilizationSettings fallback)
    {
        double thresholdOn = Clamp(doc?.ThresholdOn ?? doc?.ThresholdOnLegacy ?? fallback.ThresholdOn, 0.45d, 0.95d);
        double thresholdOff = Clamp(doc?.ThresholdOff ?? doc?.ThresholdOffLegacy ?? fallback.ThresholdOff, 0.30d, 0.90d);
        if (thresholdOff > thresholdOn - 0.05d)
        {
            thresholdOff = Math.Max(0.30d, thresholdOn - 0.05d);
        }

        return new RuntimeOverloadStabilizationSettings(
            ThresholdOn: thresholdOn,
            ThresholdOff: thresholdOff,
            DwellOnSeconds: ClampInt(doc?.DwellOnSeconds ?? doc?.DwellOnLegacy ?? fallback.DwellOnSeconds, 5, 900),
            DwellOffSeconds: ClampInt(doc?.DwellOffSeconds ?? doc?.DwellOffLegacy ?? fallback.DwellOffSeconds, 5, 900),
            ConfidenceMin: Clamp(doc?.ConfidenceMin ?? fallback.ConfidenceMin, 0.40d, 0.95d),
            GapAllowSeconds: ClampInt(doc?.GapAllowSeconds ?? fallback.GapAllowSeconds, 0, 120),
            LowQualityPauseThreshold: Clamp(doc?.LowQualityPauseThreshold ?? fallback.LowQualityPauseThreshold, 0.10d, 0.80d),
            LowQualityDwellDecayMs: ClampInt(doc?.LowQualityDwellDecayMs ?? fallback.LowQualityDwellDecayMs, 0, 30000));
    }

    private static RuntimeBreakPolicySettings ResolveBreak(
        BreakSuggestionsDocument? doc,
        RuntimeBreakPolicySettings fallback,
        RuntimeOverloadStabilizationSettings overload)
    {
        return new RuntimeBreakPolicySettings(
            MicrobreakWindowSeconds: ClampInt(doc?.MicrobreakWindowSeconds ?? fallback.MicrobreakWindowSeconds, 600, 7200),
            MicrobreakEffectiveFocusSecondsLowOverload: ClampInt(
                doc?.MicrobreakEffectiveFocusSecondsLowOverload ?? fallback.MicrobreakEffectiveFocusSecondsLowOverload,
                600,
                5400),
            MicrobreakEffectiveFocusSecondsHighOverload: ClampInt(
                doc?.MicrobreakEffectiveFocusSecondsHighOverload ?? fallback.MicrobreakEffectiveFocusSecondsHighOverload,
                300,
                5400),
            ShortBreakWindowSeconds: ClampInt(doc?.ShortBreakWindowSeconds ?? fallback.ShortBreakWindowSeconds, 1800, 14400),
            ShortBreakEffectiveFocusSecondsLowOverload: ClampInt(
                doc?.ShortBreakEffectiveFocusSecondsLowOverload ?? fallback.ShortBreakEffectiveFocusSecondsLowOverload,
                1800,
                10800),
            ShortBreakEffectiveFocusSecondsHighOverload: ClampInt(
                doc?.ShortBreakEffectiveFocusSecondsHighOverload ?? fallback.ShortBreakEffectiveFocusSecondsHighOverload,
                900,
                10800),
            CooldownSeconds: ClampInt(doc?.CooldownSeconds ?? fallback.CooldownSeconds, 60, 7200),
            ConfidenceMin: Clamp(doc?.ConfidenceMin ?? fallback.ConfidenceMin, 0.30d, 0.95d),
            HighOverloadLikelihood: Clamp(doc?.HighOverloadLikelihood ?? overload.ThresholdOn, 0.30d, 0.95d),
            DeliveryBoundaryIdleMinSeconds: Clamp(doc?.DeliveryBoundaryIdleMinSeconds ?? fallback.DeliveryBoundaryIdleMinSeconds, 0.5d, 60d),
            DeliveryBoundaryIdleMaxSeconds: Clamp(doc?.DeliveryBoundaryIdleMaxSeconds ?? fallback.DeliveryBoundaryIdleMaxSeconds, 1d, 120d),
            DeliveryBoundaryTypingQuietMax: Clamp(doc?.DeliveryBoundaryTypingQuietMax ?? fallback.DeliveryBoundaryTypingQuietMax, 0d, 1d),
            DeliveryBoundaryKeyboardBurstQuietMax: Clamp(doc?.DeliveryBoundaryKeyboardBurstQuietMax ?? fallback.DeliveryBoundaryKeyboardBurstQuietMax, 0d, 1d));
    }

    private static RuntimeDeepWorkDetectionSettings ResolveDeepWork(
        DeepWorkDetectorDocument? doc,
        RuntimeDeepWorkDetectionSettings fallback)
    {
        double intensityMin = Clamp(doc?.IntensityMin ?? fallback.IntensityMin, 0d, 1d);
        double intensityMax = Clamp(doc?.IntensityMax ?? fallback.IntensityMax, 0d, 1d);
        if (intensityMax < intensityMin)
        {
            intensityMax = intensityMin;
        }

        return new RuntimeDeepWorkDetectionSettings(
            SustainedAttentionSeconds: ClampInt(doc?.SustainedAttentionSeconds ?? fallback.SustainedAttentionSeconds, 60, 14400),
            ConfidenceMin: Clamp(doc?.ConfidenceMin ?? fallback.ConfidenceMin, 0.30d, 0.95d),
            FragmentationMax: Clamp(doc?.FragmentationMax ?? fallback.FragmentationMax, 0d, 1d),
            VolatilityMax: Clamp(doc?.VolatilityMax ?? fallback.VolatilityMax, 0d, 1d),
            IntensityMin: intensityMin,
            IntensityMax: intensityMax,
            DwellOnSeconds: ClampInt(doc?.DwellOnSeconds ?? fallback.DwellOnSeconds, 5, 1800),
            DwellOffSeconds: ClampInt(doc?.DwellOffSeconds ?? fallback.DwellOffSeconds, 5, 1800),
            GapAllowSeconds: ClampInt(doc?.GapAllowSeconds ?? fallback.GapAllowSeconds, 0, 120));
    }

    private static RuntimeNotificationSuppressionSettings ResolveNotificationSuppression(
        NotificationConstraintsDocument? doc,
        RuntimeNotificationSuppressionSettings fallback)
    {
        return new RuntimeNotificationSuppressionSettings(
            FailureBackoffSeconds: ClampInt(doc?.FailureBackoffSeconds ?? fallback.FailureBackoffSeconds, 10, 7200),
            EnableCooldownSeconds: ClampInt(doc?.EnableCooldownSeconds ?? fallback.EnableCooldownSeconds, 10, 7200),
            MinOnSeconds: ClampInt(doc?.MinOnSeconds ?? fallback.MinOnSeconds, 10, 7200));
    }

    private static RuntimeBrightnessAdjustmentSettings ResolveBrightness(
        BrightnessConstraintsDocument? doc,
        RuntimeBrightnessAdjustmentSettings fallback)
    {
        return new RuntimeBrightnessAdjustmentSettings(
            ReductionDelta: ClampInt(doc?.ReductionDelta ?? fallback.ReductionDelta, 1, 50),
            MinSafeBrightness: ClampInt(doc?.MinSafeBrightness ?? fallback.MinSafeBrightness, 0, 100),
            RampStepPerBucket: ClampInt(doc?.RampStepPerBucket ?? fallback.RampStepPerBucket, 1, 25),
            CooldownSeconds: ClampInt(doc?.CooldownSeconds ?? fallback.CooldownSeconds, 10, 7200),
            FailureBackoffSeconds: ClampInt(doc?.FailureBackoffSeconds ?? fallback.FailureBackoffSeconds, 10, 7200));
    }

    private static RuntimePolicySettings CreateDefaultSettings()
    {
        RuntimeOverloadStabilizationSettings overload = new(
            PortaExecutionConstants.OverloadStabilization.ThresholdOn,
            PortaExecutionConstants.OverloadStabilization.ThresholdOff,
            PortaExecutionConstants.OverloadStabilization.DwellOnSeconds,
            PortaExecutionConstants.OverloadStabilization.DwellOffSeconds,
            PortaExecutionConstants.OverloadStabilization.ConfidenceMin,
            PortaExecutionConstants.OverloadStabilization.GapAllowSeconds,
            PortaExecutionConstants.OverloadStabilization.LowQualityPauseThreshold,
            PortaExecutionConstants.OverloadStabilization.LowQualityDwellDecayMs);

        RuntimeBreakPolicySettings breakPolicy = new(
            PortaExecutionConstants.BreakPolicy.MicrobreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsLowOverload,
            PortaExecutionConstants.BreakPolicy.MicrobreakEffectiveFocusSecondsHighOverload,
            PortaExecutionConstants.BreakPolicy.ShortBreakWindowSeconds,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsLowOverload,
            PortaExecutionConstants.BreakPolicy.ShortBreakEffectiveFocusSecondsHighOverload,
            PortaExecutionConstants.BreakPolicy.CooldownSeconds,
            PortaExecutionConstants.BreakPolicy.ConfidenceMin,
            PortaExecutionConstants.BreakPolicy.HighOverloadLikelihood,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMinSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryIdleMaxSeconds,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryTypingQuietMax,
            PortaExecutionConstants.BreakPolicy.DeliveryBoundaryKeyboardBurstQuietMax);

        RuntimeDeepWorkDetectionSettings deepWork = new(
            PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds,
            PortaExecutionConstants.DeepWorkDetection.ConfidenceMin,
            PortaExecutionConstants.DeepWorkDetection.FragmentationMax,
            PortaExecutionConstants.DeepWorkDetection.VolatilityMax,
            PortaExecutionConstants.DeepWorkDetection.IntensityMin,
            PortaExecutionConstants.DeepWorkDetection.IntensityMax,
            PortaExecutionConstants.DeepWorkDetection.DwellOnSeconds,
            PortaExecutionConstants.DeepWorkDetection.DwellOffSeconds,
            PortaExecutionConstants.DeepWorkDetection.GapAllowSeconds);

        RuntimeNotificationSuppressionSettings notificationSuppression = new(
            PortaExecutionConstants.NotificationSuppression.FailureBackoffSeconds,
            PortaExecutionConstants.NotificationSuppression.EnableCooldownSeconds,
            PortaExecutionConstants.NotificationSuppression.MinOnSeconds);

        RuntimeBrightnessAdjustmentSettings brightness = new(
            PortaExecutionConstants.BrightnessAdjustment.ReductionDelta,
            PortaExecutionConstants.BrightnessAdjustment.MinSafeBrightness,
            PortaExecutionConstants.BrightnessAdjustment.RampStepPerBucket,
            PortaExecutionConstants.BrightnessAdjustment.CooldownSeconds,
            PortaExecutionConstants.BrightnessAdjustment.FailureBackoffSeconds);

        return new RuntimePolicySettings(overload, breakPolicy, deepWork, notificationSuppression, brightness);
    }

    private static RuntimePolicyEnvelope EmptyEnvelope()
    {
        return new RuntimePolicyEnvelope("local-default", CreateDefaultSettings());
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Clamp(value, min, max);
    }

    private static int ClampInt(int value, int min, int max)
    {
        return Math.Clamp(value, min, max);
    }

    private sealed record RuntimeEnvelopeDocument(
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("policy_version")] string? PolicyVersion,
        [property: JsonPropertyName("detectors")] DetectorsDocument? Detectors,
        [property: JsonPropertyName("delivery")] DeliveryDocument? Delivery,
        [property: JsonPropertyName("constraints")] ConstraintsDocument? Constraints);

    private sealed record DetectorsDocument(
        [property: JsonPropertyName("overload")] OverloadDetectorDocument? Overload,
        [property: JsonPropertyName("deep_work")] DeepWorkDetectorDocument? DeepWork);

    // Step 2 migration map:
    // The JSON envelope name break_suggestions is legacy compatibility naming.
    // Product taxonomy is moving toward explicit short-break and long-break rules,
    // but the old envelope remains until policy transport is migrated.
    private sealed record DeliveryDocument(
        [property: JsonPropertyName("break_suggestions")] BreakSuggestionsDocument? BreakSuggestions);

    private sealed record ConstraintsDocument(
        [property: JsonPropertyName("notifications")] NotificationConstraintsDocument? Notifications,
        [property: JsonPropertyName("brightness")] BrightnessConstraintsDocument? Brightness);

    private sealed record OverloadDetectorDocument(
        [property: JsonPropertyName("threshold_on")] double? ThresholdOn,
        [property: JsonPropertyName("threshold_off")] double? ThresholdOff,
        [property: JsonPropertyName("dwell_on_s")] int? DwellOnSeconds,
        [property: JsonPropertyName("dwell_off_s")] int? DwellOffSeconds,
        [property: JsonPropertyName("confidence_min")] double? ConfidenceMin,
        [property: JsonPropertyName("gap_allow_s")] int? GapAllowSeconds,
        [property: JsonPropertyName("low_quality_pause_threshold")] double? LowQualityPauseThreshold,
        [property: JsonPropertyName("low_quality_dwell_decay_ms")] int? LowQualityDwellDecayMs,
        [property: JsonPropertyName("thresholdOn")] double? ThresholdOnLegacy = null,
        [property: JsonPropertyName("thresholdOff")] double? ThresholdOffLegacy = null,
        [property: JsonPropertyName("dwellOnSeconds")] int? DwellOnLegacy = null,
        [property: JsonPropertyName("dwellOffSeconds")] int? DwellOffLegacy = null);

    private sealed record DeepWorkDetectorDocument(
        [property: JsonPropertyName("sustained_attention_s")] int? SustainedAttentionSeconds,
        [property: JsonPropertyName("confidence_min")] double? ConfidenceMin,
        [property: JsonPropertyName("fragmentation_max")] double? FragmentationMax,
        [property: JsonPropertyName("volatility_max")] double? VolatilityMax,
        [property: JsonPropertyName("intensity_min")] double? IntensityMin,
        [property: JsonPropertyName("intensity_max")] double? IntensityMax,
        [property: JsonPropertyName("dwell_on_s")] int? DwellOnSeconds,
        [property: JsonPropertyName("dwell_off_s")] int? DwellOffSeconds,
        [property: JsonPropertyName("gap_allow_s")] int? GapAllowSeconds);

    // Step 2 migration map:
    // microbreak_* fields are legacy short-break policy names.
    // short_break_* fields are legacy longer-break policy names.
    // The schema keeps them for compatibility until the break taxonomy is renamed.
    private sealed record BreakSuggestionsDocument(
        [property: JsonPropertyName("microbreak_window_s")] int? MicrobreakWindowSeconds,
        [property: JsonPropertyName("microbreak_effective_focus_min_low_s")] int? MicrobreakEffectiveFocusSecondsLowOverload,
        [property: JsonPropertyName("microbreak_effective_focus_min_high_s")] int? MicrobreakEffectiveFocusSecondsHighOverload,
        [property: JsonPropertyName("short_break_window_s")] int? ShortBreakWindowSeconds,
        [property: JsonPropertyName("short_break_effective_focus_min_low_s")] int? ShortBreakEffectiveFocusSecondsLowOverload,
        [property: JsonPropertyName("short_break_effective_focus_min_high_s")] int? ShortBreakEffectiveFocusSecondsHighOverload,
        [property: JsonPropertyName("cooldown_s")] int? CooldownSeconds,
        [property: JsonPropertyName("confidence_min")] double? ConfidenceMin,
        [property: JsonPropertyName("high_overload_likelihood")] double? HighOverloadLikelihood,
        [property: JsonPropertyName("idle_boundary_min_s")] double? DeliveryBoundaryIdleMinSeconds,
        [property: JsonPropertyName("idle_boundary_max_s")] double? DeliveryBoundaryIdleMaxSeconds,
        [property: JsonPropertyName("typing_quiet_max")] double? DeliveryBoundaryTypingQuietMax,
        [property: JsonPropertyName("keyboard_burst_quiet_max")] double? DeliveryBoundaryKeyboardBurstQuietMax);

    private sealed record NotificationConstraintsDocument(
        [property: JsonPropertyName("failure_backoff_seconds")] int? FailureBackoffSeconds,
        [property: JsonPropertyName("enable_cooldown_seconds")] int? EnableCooldownSeconds,
        [property: JsonPropertyName("min_on_seconds")] int? MinOnSeconds);

    private sealed record BrightnessConstraintsDocument(
        [property: JsonPropertyName("reduction_delta")] int? ReductionDelta,
        [property: JsonPropertyName("min_safe")] int? MinSafeBrightness,
        [property: JsonPropertyName("ramp_step_per_bucket")] int? RampStepPerBucket,
        [property: JsonPropertyName("cooldown_seconds")] int? CooldownSeconds,
        [property: JsonPropertyName("failure_backoff_seconds")] int? FailureBackoffSeconds);
}
