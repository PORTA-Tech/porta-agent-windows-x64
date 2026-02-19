using System;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.State;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Control;

public sealed class ControlPolicy
{
    public double IntegralClamp { get; init; } = 2.0;
    public double MaxAbsCommand { get; init; } = 1.0;
    public double InstabilitySchedulingThreshold { get; init; } = 0.65;
    public double SchedulingDMultiplier { get; init; } = 1.35;
    public double SchedulingPMultiplier { get; init; } = 0.85;

    public (ControlOutput Output, ActuationCommand Command) Evaluate(
        ControlFeatures features,
        ControlParameters parameters,
        ControlState state)
    {
        if (!parameters.ActuationEnabled)
        {
            return (ControlOutput.None("actuation_disabled"), ActuationCommand.Safe("actuation_disabled"));
        }

        if (!state.IsInitialized)
        {
            state.IsInitialized = true;
            state.LastTimestamp = features.Timestamp;
            state.LastError = 0;
            state.IntegralError = 0;
            state.LastCommand = 0;
            state.LastSmoothedCommand = 0;
            state.LastActuationIntensity = 0;
            return (ControlOutput.None("warmup"), ActuationCommand.Safe("warmup"));
        }

        if (features.SignalConfidence < parameters.ConfidenceThreshold)
        {
            return (ControlOutput.None("low_confidence"), ActuationCommand.Safe("low_confidence"));
        }

        if (features.EngagementIndex < 0.10)
        {
            return (ControlOutput.None("low_engagement"), ActuationCommand.Safe("low_engagement"));
        }

        double kp = parameters.Kp;
        double ki = parameters.Ki;
        double kd = parameters.Kd;
        if (parameters.EnableGainScheduling && features.InstabilityIndex >= InstabilitySchedulingThreshold)
        {
            kp *= SchedulingPMultiplier;
            kd *= SchedulingDMultiplier;
        }
        else if (features.SignalConfidence >= parameters.ConfidenceThreshold)
        {
            // Persist only gains observed during stable operating conditions.
            state.LastStableKp = kp;
            state.LastStableKi = ki;
            state.LastStableKd = kd;
        }

        double integral = MathEx.Quantize(Math.Clamp(features.ArousalIntegral, -IntegralClamp, IntegralClamp));
        double raw = MathEx.Quantize((kp * features.ArousalError) + (ki * integral) + (kd * features.ArousalVelocity));
        double bounded = MathEx.Quantize(Math.Clamp(raw, -MaxAbsCommand, MaxAbsCommand));
        double alpha = MathEx.Clamp01(parameters.OutputSmoothingAlpha);
        double smoothed = MathEx.Quantize((alpha * state.LastSmoothedCommand) + ((1.0 - alpha) * bounded));
        int boundedDelta = SafetyLimits.ClampInjectionDelta((int)Math.Round(smoothed * SafetyLimits.MaxAbsInjectionDelta, MidpointRounding.AwayFromZero));
        int delayMs = Math.Clamp(parameters.FixedDelayMs, 0, SafetyLimits.MaxActuationDelayMs);
        bool shouldInject = SafetyLimits.IsInjectableDelta(boundedDelta);

        state.LastTimestamp = features.Timestamp;
        state.LastError = features.ArousalError;
        state.IntegralError = integral;
        state.LastCommand = bounded;
        state.LastSmoothedCommand = smoothed;

        var output = new ControlOutput
        {
            ShouldInject = shouldInject,
            InjectionDelta = shouldInject ? boundedDelta : 0,
            DelayMs = shouldInject ? delayMs : 0,
            Note = shouldInject ? "ok" : "below_injection_threshold"
        };

        var command = ActuationCommand.FromControlSignal(
            smoothed,
            features.SignalConfidence >= parameters.ConfidenceThreshold ? AuthorityLevel.Normal : AuthorityLevel.Reduced,
            "policy_evaluate");

        state.LastActuationIntensity = command.StimulationIntensity;
        return (output, command);
    }

    public ControlOutput Compute(ScrollState input, ControlParameters parameters, ControlState state, long frequency)
    {
        // Backward-compatible path for single-signal callers.
        var synthetic = new ControlFeatures
        {
            Timestamp = input.Timestamp,
            ArousalError = -input.VelocityPerSecond,
            ArousalVelocity = -input.VelocityPerSecond,
            ArousalIntegral = state.IntegralError,
            EngagementIndex = MathEx.Clamp01(Math.Abs(input.NormalizedDelta)),
            InstabilityIndex = 0,
            SignalConfidence = 1
        };
        return Evaluate(synthetic, parameters, state).Output;
    }

    public SessionState SaveSession(ControlState state)
    {
        return new SessionState
        {
            SchemaVersion = 1,
            SavedUtc = DateTimeOffset.UtcNow,
            BaselineArousalMean = state.BaselineArousalMean,
            BaselineVariance = state.BaselineVariance,
            DriftOffset = state.DriftOffset,
            LastStableKp = state.LastStableKp,
            LastStableKi = state.LastStableKi,
            LastStableKd = state.LastStableKd
        };
    }

    public ControlState RestoreSession(SessionState? session)
    {
        if (session == null || session.SchemaVersion != 1)
        {
            return new ControlState();
        }

        return new ControlState
        {
            // Warm-start baseline; keep transient controller terms reset.
            IsInitialized = false,
            LastTimestamp = 0,
            LastError = 0,
            IntegralError = 0,
            LastCommand = 0,
            LastSmoothedCommand = 0,
            LastActuationIntensity = 0,
            BaselineArousalMean = session.BaselineArousalMean,
            BaselineVariance = session.BaselineVariance,
            DriftOffset = session.DriftOffset,
            LastStableKp = session.LastStableKp,
            LastStableKi = session.LastStableKi,
            LastStableKd = session.LastStableKd
        };
    }
}
