using System;
using Porta.Agent.Common.Contracts;
using Porta.Agent.Common.Processing;
using Porta.Agent.Common.State;
using Porta.Agent.Common.Utils;

namespace Porta.Agent.Common.Control;

public sealed class DeterministicControlEngine
{
    public sealed class ControlEvaluationSnapshot
    {
        public AggregatedState Aggregated { get; init; } = new();
        public ControlFeatures Features { get; init; } = new();
        public ActuationCommand ProposedCommand { get; init; } = ActuationCommand.Safe("uninitialized");
        public ActuationCommand SafeCommand { get; init; } = ActuationCommand.Safe("uninitialized");
    }

    private readonly object _sync = new();
    private readonly StateAggregator _aggregator;
    private readonly FeatureExtractor _extractor;
    private readonly ControlPolicy _policy;
    private readonly SafetyArbitrator _safety;
    private readonly ControlState _state;
    private readonly long _frequency;
    private readonly long _tickIntervalTicks;
    private long _lastEvaluatedTimestamp;
    private AggregatedState? _previousAggregated;

    public DeterministicControlEngine(
        long frequency,
        int controlHz = 20,
        int signalCapacity = 4096,
        double signalWindowSeconds = 5.0)
    {
        if (frequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        if (controlHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(controlHz));
        }

        _frequency = frequency;
        _tickIntervalTicks = Math.Max(1, frequency / controlHz);
        _aggregator = new StateAggregator(signalCapacity, signalWindowSeconds, emaAlpha: 0.25);
        _extractor = new FeatureExtractor();
        _policy = new ControlPolicy();
        _safety = new SafetyArbitrator();
        _state = new ControlState();
    }

    public long TickIntervalTicks => _tickIntervalTicks;
    public ControlState State => _state;

    public void RestoreSession(SessionState? session)
    {
        lock (_sync)
        {
            ControlState restored = _policy.RestoreSession(session);
            _state.BaselineArousalMean = restored.BaselineArousalMean;
            _state.BaselineVariance = restored.BaselineVariance;
            _state.DriftOffset = restored.DriftOffset;
            _state.LastStableKp = restored.LastStableKp;
            _state.LastStableKi = restored.LastStableKi;
            _state.LastStableKd = restored.LastStableKd;
            _previousAggregated = null;
            _lastEvaluatedTimestamp = 0;
        }
    }

    public SessionState SaveSession()
    {
        lock (_sync)
        {
            return _policy.SaveSession(_state);
        }
    }

    public void AddScrollSample(long timestamp, int rawDelta)
    {
        lock (_sync)
        {
            double normalized = SignalNormalizer.NormalizeWheelDelta(rawDelta);
            _aggregator.AddScroll(timestamp, normalized);
        }
    }

    public void AddKeyboardSample(long timestamp, bool isKeyDown)
    {
        lock (_sync)
        {
            _aggregator.AddKeyboard(timestamp, isKeyDown);
        }
    }

    public void AddFocusSample(long timestamp)
    {
        lock (_sync)
        {
            _aggregator.AddFocusChange(timestamp);
        }
    }

    public ControlEvaluationSnapshot EvaluateAtTimestampWithSnapshot(long timestamp, ControlParameters parameters)
    {
        lock (_sync)
        {
            if (_lastEvaluatedTimestamp > 0)
            {
                if (timestamp <= _lastEvaluatedTimestamp)
                {
                    throw new InvalidOperationException($"Non-monotonic control timestamp {timestamp} (last {_lastEvaluatedTimestamp}).");
                }

                long step = timestamp - _lastEvaluatedTimestamp;
                if (step != _tickIntervalTicks)
                {
                    throw new InvalidOperationException($"Non-fixed control timestep {step} (expected {_tickIntervalTicks}).");
                }
            }

            AggregatedState aggregated = _aggregator.Aggregate(timestamp, _frequency);
            ControlFeatures features = _extractor.Extract(
                aggregated,
                _previousAggregated,
                parameters.TargetArousalMin,
                parameters.TargetArousalMax,
                _state.IntegralError,
                _frequency);
            _previousAggregated = aggregated;
            _lastEvaluatedTimestamp = timestamp;
            var evaluated = _policy.Evaluate(features, parameters, _state);
            ActuationCommand safeCommand = _safety.Arbitrate(features, evaluated.Command, _state, parameters);
            _state.LastActuationIntensity = safeCommand.StimulationIntensity;
            _state.LastSafeSceneDynamics = safeCommand.SceneDynamics;
            _state.LastSafeVisualShift = safeCommand.VisualTemperatureShift;
            _state.LastSafePacingModifier = safeCommand.PacingModifier;
            _state.BaselineArousalMean = MathEx.Quantize(MathEx.Ema(_state.BaselineArousalMean, aggregated.CompositeArousalEstimate, 0.02));
            _state.BaselineVariance = MathEx.Quantize(MathEx.Ema(_state.BaselineVariance, aggregated.InteractionVariance, 0.02));
            _state.DriftOffset = MathEx.Quantize(_state.BaselineArousalMean - 0.5);
            return new ControlEvaluationSnapshot
            {
                Aggregated = aggregated,
                Features = features,
                ProposedCommand = evaluated.Command,
                SafeCommand = safeCommand
            };
        }
    }

    public ActuationCommand EvaluateAtTimestamp(long timestamp, ControlParameters parameters)
    {
        return EvaluateAtTimestampWithSnapshot(timestamp, parameters).SafeCommand;
    }

    // Compatibility wrapper for callers that still schedule ticks externally by timestamp checks.
    public bool IsTickDue(long timestamp, long nextTickTimestamp) => timestamp >= nextTickTimestamp;

    // Compatibility wrapper with prior naming.
    public ActuationCommand EvaluateTick(long timestamp, ControlParameters parameters) => EvaluateAtTimestamp(timestamp, parameters);
}
