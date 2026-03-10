using System.Text.Json;
using Porta.Agent.Core;

namespace Porta.Agent.Core.Tests;

public sealed class RuntimePolicyProviderTests
{
    [Fact]
    public void GetEnvelope_UsesDefaultsWhenFileMissing()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        var provider = new FileRuntimePolicyProvider(missingPath);

        RuntimePolicyEnvelope envelope = provider.GetEnvelope();

        Assert.Equal("local-default", envelope.Version);
        Assert.Equal(PortaExecutionConstants.OverloadStabilization.ThresholdOn, envelope.Settings.OverloadStabilization.ThresholdOn);
        Assert.Equal(PortaExecutionConstants.BreakPolicy.MicrobreakWindowSeconds, envelope.Settings.BreakPolicy.MicrobreakWindowSeconds);
        Assert.Equal(PortaExecutionConstants.DeepWorkDetection.SustainedAttentionSeconds, envelope.Settings.DeepWorkDetection.SustainedAttentionSeconds);
        Assert.Equal(PortaExecutionConstants.NotificationSuppression.EnableCooldownSeconds, envelope.Settings.NotificationSuppression.EnableCooldownSeconds);
        Assert.Equal(PortaExecutionConstants.BrightnessAdjustment.ReductionDelta, envelope.Settings.BrightnessAdjustment.ReductionDelta);
    }

    [Fact]
    public void GetEnvelope_ParsesPolicyEnvelopeAndClampsUnsafeValues()
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try
        {
            var document = new
            {
                policy_version = "policy-2026-03-10",
                detectors = new
                {
                    overload = new
                    {
                        threshold_on = 0.66,
                        threshold_off = 0.64,
                        dwell_on_s = 20,
                        dwell_off_s = 15,
                        confidence_min = 0.48,
                        gap_allow_s = 12,
                        low_quality_pause_threshold = 0.28,
                        low_quality_dwell_decay_ms = 3000
                    },
                    deep_work = new
                    {
                        sustained_attention_s = 600,
                        confidence_min = 0.58,
                        fragmentation_max = 0.40,
                        volatility_max = 0.45,
                        intensity_min = 0.25,
                        intensity_max = 0.70,
                        dwell_on_s = 30,
                        dwell_off_s = 25,
                        gap_allow_s = 18
                    }
                },
                delivery = new
                {
                    break_suggestions = new
                    {
                        microbreak_window_s = 1800,
                        microbreak_effective_focus_min_low_s = 1200,
                        microbreak_effective_focus_min_high_s = 900,
                        short_break_window_s = 3600,
                        short_break_effective_focus_min_low_s = 2400,
                        short_break_effective_focus_min_high_s = 1800,
                        cooldown_s = 900,
                        confidence_min = 0.45,
                        high_overload_likelihood = 0.61,
                        idle_boundary_min_s = 3.0,
                        idle_boundary_max_s = 20.0,
                        typing_quiet_max = 0.22,
                        keyboard_burst_quiet_max = 0.12
                    }
                },
                constraints = new
                {
                    notifications = new
                    {
                        failure_backoff_seconds = 90,
                        enable_cooldown_seconds = 180,
                        min_on_seconds = 45
                    },
                    brightness = new
                    {
                        reduction_delta = 8,
                        min_safe = 25,
                        ramp_step_per_bucket = 2,
                        cooldown_seconds = 300,
                        failure_backoff_seconds = 240
                    }
                }
            };

            File.WriteAllText(path, JsonSerializer.Serialize(document));
            var provider = new FileRuntimePolicyProvider(path);

            RuntimePolicyEnvelope envelope = provider.GetEnvelope();

            Assert.Equal("policy-2026-03-10", envelope.Version);
            Assert.Equal(0.66d, envelope.Settings.OverloadStabilization.ThresholdOn, 6);
            Assert.Equal(0.61d, envelope.Settings.OverloadStabilization.ThresholdOff, 6);
            Assert.Equal(20, envelope.Settings.OverloadStabilization.DwellOnSeconds);
            Assert.Equal(600, envelope.Settings.DeepWorkDetection.SustainedAttentionSeconds);
            Assert.Equal(1200, envelope.Settings.BreakPolicy.MicrobreakEffectiveFocusSecondsLowOverload);
            Assert.Equal(900, envelope.Settings.BreakPolicy.MicrobreakEffectiveFocusSecondsHighOverload);
            Assert.Equal(180, envelope.Settings.NotificationSuppression.EnableCooldownSeconds);
            Assert.Equal(45, envelope.Settings.NotificationSuppression.MinOnSeconds);
            Assert.Equal(8, envelope.Settings.BrightnessAdjustment.ReductionDelta);
            Assert.Equal(25, envelope.Settings.BrightnessAdjustment.MinSafeBrightness);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
