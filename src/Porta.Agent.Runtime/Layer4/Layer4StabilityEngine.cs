using System.Text.Json;
using Porta.Agent.Runtime.Layer3;

namespace Porta.Agent.Runtime.Layer4;

internal sealed class Layer4StabilityEngine
{
    private readonly Layer4StabilityConfig _config;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownExpiryByState = new(StringComparer.Ordinal);
    private readonly Queue<(DateTimeOffset atUtc, string from, string to)> _transitionHistory = new();
    private string _committedState = "stable";
    private string? _candidateState;
    private DateTimeOffset _candidateSinceUtc;
    private DateTimeOffset _flapSuppressionUntilUtc;
    private string _flapLockedState = "stable";
    private bool _initialized;

    public Layer4StabilityEngine(Layer4StabilityConfig config)
    {
        _config = config;
    }

    public Layer4StateResult Stabilize(Layer3StateResult layer3)
    {
        lock (_gate)
        {
            string previousState = _committedState;
            bool hysteresisApplied = false;
            bool dwellApplied = false;
            bool cooldownActive = false;
            bool flapSuppressed = false;
            string transitionReason = "no_change";

            if (!_initialized)
            {
                _initialized = true;
                _committedState = layer3.StateName;
                transitionReason = "cold_start_passthrough";
                return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 0.5);
            }

            if (layer3.EvaluatedAtUtc <= DateTimeOffset.MinValue)
            {
                transitionReason = "missing_timestamp_passthrough";
                return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 0.5);
            }

            if (layer3.EvaluatedAtUtc < _flapSuppressionUntilUtc)
            {
                flapSuppressed = true;
                _committedState = _flapLockedState;
                transitionReason = "flap_suppression_lock";
                return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 1.0);
            }

            string candidate = SelectCandidateState(layer3);
            if (!string.Equals(candidate, _committedState, StringComparison.Ordinal))
            {
                hysteresisApplied = true;
                if (!IsTransitionAllowedByHysteresis(_committedState, candidate, layer3.GlobalRiskScore))
                {
                    transitionReason = "hysteresis_blocked";
                    return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 1.0);
                }

                if (IsCooldownActive(candidate, layer3.EvaluatedAtUtc))
                {
                    cooldownActive = true;
                    transitionReason = "cooldown_blocked";
                    return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 1.0);
                }

                if (!string.Equals(_candidateState, candidate, StringComparison.Ordinal))
                {
                    _candidateState = candidate;
                    _candidateSinceUtc = layer3.EvaluatedAtUtc;
                    transitionReason = "dwell_started";
                    return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied: true, cooldownActive, flapSuppressed, confidenceScale: 1.0);
                }

                int dwellSeconds = GetDwellSeconds(candidate);
                if ((layer3.EvaluatedAtUtc - _candidateSinceUtc).TotalSeconds < dwellSeconds)
                {
                    transitionReason = "dwell_wait";
                    return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied: true, cooldownActive, flapSuppressed, confidenceScale: 1.0);
                }

                dwellApplied = true;
                CommitTransition(_committedState, candidate, layer3.EvaluatedAtUtc);
                _committedState = candidate;
                transitionReason = "committed_after_dwell";
                _candidateState = null;
            }
            else
            {
                _candidateState = null;
            }

            if (CheckAndActivateFlapSuppression(layer3.EvaluatedAtUtc, out string lockedState))
            {
                flapSuppressed = true;
                _flapLockedState = lockedState;
                _committedState = lockedState;
                transitionReason = "flap_detected_lock";
            }

            return BuildResult(layer3, previousState, transitionReason, hysteresisApplied, dwellApplied, cooldownActive, flapSuppressed, confidenceScale: 1.0);
        }
    }

    private Layer4StateResult BuildResult(
        Layer3StateResult layer3,
        string previousState,
        string transitionReason,
        bool hysteresisApplied,
        bool dwellApplied,
        bool cooldownActive,
        bool flapSuppressed,
        double confidenceScale)
    {
        Dictionary<string, object?> layer3Explanation;
        try
        {
            layer3Explanation = JsonSerializer.Deserialize<Dictionary<string, object?>>(layer3.ExplanationJson) ?? new Dictionary<string, object?>();
        }
        catch
        {
            layer3Explanation = new Dictionary<string, object?>();
        }

        var extension = new Dictionary<string, object?>
        {
            ["layer3_state"] = layer3.StateName,
            ["committed_state"] = _committedState,
            ["hysteresis_applied"] = hysteresisApplied,
            ["dwell_applied"] = dwellApplied,
            ["cooldown_active"] = cooldownActive,
            ["flap_suppressed"] = flapSuppressed,
            ["previous_state"] = previousState,
            ["transition_reason"] = transitionReason,
            ["stability_model_version"] = _config.ModelVersion
        };
        layer3Explanation["layer4_stability"] = extension;
        string explanation = JsonSerializer.Serialize(layer3Explanation);

        return new Layer4StateResult(
            CommittedState: _committedState,
            Probability: layer3.Probability,
            Confidence: Math.Clamp(layer3.Confidence * confidenceScale, 0, 1),
            DistributionJson: layer3.DistributionJson,
            ContributingPrimitivesJson: layer3.ContributingPrimitivesJson,
            ExplanationJson: explanation,
            EvaluatedAtUtc: layer3.EvaluatedAtUtc,
            TransitionReason: transitionReason);
    }

    private string SelectCandidateState(Layer3StateResult layer3)
    {
        if (string.Equals(layer3.StateName, "recovering", StringComparison.Ordinal))
        {
            if (!string.Equals(_committedState, "stable", StringComparison.Ordinal) && string.Equals(layer3.TrendDirection, "down", StringComparison.Ordinal))
            {
                return "recovering";
            }

            return _committedState;
        }

        return layer3.StateName;
    }

    private bool IsTransitionAllowedByHysteresis(string from, string to, double risk)
    {
        int fromSeverity = Severity(from);
        int toSeverity = Severity(to);
        if (toSeverity > fromSeverity)
        {
            return risk >= EnterThreshold(to);
        }

        if (toSeverity < fromSeverity)
        {
            return risk <= ExitThreshold(from);
        }

        return true;
    }

    private bool IsCooldownActive(string toState, DateTimeOffset nowUtc)
    {
        if (_cooldownExpiryByState.TryGetValue(toState, out DateTimeOffset expiry))
        {
            return nowUtc < expiry;
        }

        return false;
    }

    private int GetDwellSeconds(string state)
    {
        return state switch
        {
            "critical" => _config.CriticalDwellSeconds,
            "degrading" => _config.DegradingDwellSeconds,
            "elevated_risk" => _config.ElevatedDwellSeconds,
            "recovering" => _config.RecoveringDwellSeconds,
            _ => 0
        };
    }

    private void CommitTransition(string from, string to, DateTimeOffset atUtc)
    {
        _transitionHistory.Enqueue((atUtc, from, to));
        TrimHistory(atUtc);
        if (string.Equals(from, "critical", StringComparison.Ordinal))
        {
            _cooldownExpiryByState["critical"] = atUtc.AddSeconds(_config.CriticalCooldownSeconds);
        }
        if (string.Equals(from, "degrading", StringComparison.Ordinal))
        {
            _cooldownExpiryByState["degrading"] = atUtc.AddSeconds(_config.DegradingCooldownSeconds);
        }
    }

    private bool CheckAndActivateFlapSuppression(DateTimeOffset nowUtc, out string lockedState)
    {
        TrimHistory(nowUtc);
        lockedState = _committedState;
        if (_transitionHistory.Count < _config.FlapTransitionCount)
        {
            return false;
        }

        int maxSeverity = Severity(_committedState);
        foreach ((_, string from, string to) in _transitionHistory)
        {
            maxSeverity = Math.Max(maxSeverity, Math.Max(Severity(from), Severity(to)));
        }

        lockedState = StateFromSeverity(maxSeverity);
        _flapSuppressionUntilUtc = nowUtc.AddSeconds(_config.FlapSuppressionSeconds);
        _transitionHistory.Clear();
        return true;
    }

    private void TrimHistory(DateTimeOffset nowUtc)
    {
        while (_transitionHistory.Count > 0)
        {
            (DateTimeOffset atUtc, _, _) = _transitionHistory.Peek();
            if ((nowUtc - atUtc).TotalSeconds <= _config.FlapWindowSeconds)
            {
                break;
            }
            _transitionHistory.Dequeue();
        }
    }

    private double EnterThreshold(string state) =>
        state switch
        {
            "critical" => _config.CriticalEnter,
            "degrading" => _config.DegradingEnter,
            "elevated_risk" => _config.ElevatedEnter,
            _ => 0.0
        };

    private double ExitThreshold(string state) =>
        state switch
        {
            "critical" => _config.CriticalExit,
            "degrading" => _config.DegradingExit,
            "elevated_risk" => _config.ElevatedExit,
            _ => 1.0
        };

    private static int Severity(string state) =>
        state switch
        {
            "stable" => 0,
            "recovering" => 1,
            "elevated_risk" => 2,
            "degrading" => 3,
            "critical" => 4,
            _ => 0
        };

    private static string StateFromSeverity(int severity) =>
        severity switch
        {
            4 => "critical",
            3 => "degrading",
            2 => "elevated_risk",
            1 => "recovering",
            _ => "stable"
        };
}
