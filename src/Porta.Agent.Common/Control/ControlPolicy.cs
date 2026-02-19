using System;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.State;

namespace Porta.Agent.Common.Control;

public sealed class ControlPolicy
{
    public double Kp { get; init; } = 0.45;
    public double Ki { get; init; } = 0.08;
    public double Kd { get; init; } = 0.06;
    public double IntegralClamp { get; init; } = 3.0;
    public double SmoothingAlpha { get; init; } = 0.25;
    public double MaxAbsCommand { get; init; } = 1.0;

    public ControlOutput Compute(ScrollState input, ControlParameters parameters, ControlState state, long frequency)
    {
        if (!parameters.ActuationEnabled)
        {
            return ControlOutput.None("actuation_disabled");
        }

        if (!state.IsInitialized)
        {
            state.IsInitialized = true;
            state.LastTimestamp = input.Timestamp;
            state.LastError = 0;
            state.IntegralError = 0;
            state.LastCommand = 0;
            state.LastSmoothedCommand = 0;
            return ControlOutput.None("warmup");
        }

        if (frequency <= 0 || input.Timestamp <= state.LastTimestamp)
        {
            return ControlOutput.None("invalid_timebase");
        }

        double dt = (input.Timestamp - state.LastTimestamp) / (double)frequency;
        dt = Math.Max(dt, 1e-6);

        // Target is a stable zero-velocity state. Error sign damps incoming movement.
        double error = -input.VelocityPerSecond;
        double integral = Math.Clamp(state.IntegralError + (error * dt), -IntegralClamp, IntegralClamp);
        double derivative = (error - state.LastError) / dt;
        double command = Math.Clamp((Kp * error) + (Ki * integral) + (Kd * derivative), -MaxAbsCommand, MaxAbsCommand);
        double alpha = Math.Clamp(SmoothingAlpha, 0.0, 1.0);
        double smoothed = state.LastSmoothedCommand + (alpha * (command - state.LastSmoothedCommand));
        int proposedDelta = (int)Math.Round(smoothed * SafetyLimits.MaxAbsInjectionDelta, MidpointRounding.AwayFromZero);
        int boundedDelta = SafetyLimits.ClampInjectionDelta(proposedDelta);

        state.LastTimestamp = input.Timestamp;
        state.LastError = error;
        state.IntegralError = integral;
        state.LastCommand = command;
        state.LastSmoothedCommand = smoothed;

        if (!SafetyLimits.IsInjectableDelta(boundedDelta))
        {
            return new ControlOutput
            {
                ShouldInject = false,
                InjectionDelta = 0,
                DelayMs = 0,
                Command = command,
                SmoothedCommand = smoothed,
                Note = "below_injection_threshold"
            };
        }

        return new ControlOutput
        {
            ShouldInject = true,
            InjectionDelta = boundedDelta,
            DelayMs = Math.Clamp(parameters.FixedDelayMs, 0, SafetyLimits.MaxActuationDelayMs),
            Command = command,
            SmoothedCommand = smoothed,
            Note = "ok"
        };
    }

    public SessionState SaveSession(ControlState state)
    {
        return new SessionState
        {
            SchemaVersion = 1,
            SavedUtc = DateTimeOffset.UtcNow,
            Control = new ControlState
            {
                IsInitialized = state.IsInitialized,
                LastTimestamp = state.LastTimestamp,
                LastError = state.LastError,
                IntegralError = state.IntegralError,
                LastCommand = state.LastCommand,
                LastSmoothedCommand = state.LastSmoothedCommand
            }
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
            IsInitialized = session.Control.IsInitialized,
            LastTimestamp = session.Control.LastTimestamp,
            LastError = session.Control.LastError,
            IntegralError = session.Control.IntegralError,
            LastCommand = session.Control.LastCommand,
            LastSmoothedCommand = session.Control.LastSmoothedCommand
        };
    }
}
