using System.Diagnostics;

namespace Porta.Agent.Core;

public sealed record RawSignalSample(
    DateTimeOffset TimestampUtc,
    double InteractionLoad,
    double ScrollLoad,
    double FocusSwitchLoad,
    double ProcessCountLoad);

public sealed record PrimitiveSignalSnapshot(
    DateTimeOffset TimestampUtc,
    double TypingBurstIntensity,
    double ScrollVelocityIndex,
    double FocusSwitchRate,
    IReadOnlyList<string> Contributors);

public sealed record InferredStateSnapshot(
    DateTimeOffset TimestampUtc,
    double FocusStrength,
    double OverloadLikelihood,
    double FatigueProbability,
    double CognitiveStability,
    double Confidence,
    IReadOnlyList<string> Contributors);

public sealed record RegulationDecisionSnapshot(
    string Action,
    bool ActivationWindowTriggered,
    bool CooldownTriggered,
    bool ActivationWindowOpen,
    string PolicyId = "n/a",
    string Domain = "control",
    string EscalationLevel = "Inform",
    DateTimeOffset? PlannedEndTimestampUtc = null,
    DateTimeOffset? CooldownEndTimestampUtc = null);

public interface ISignalAcquisitionStage
{
    RawSignalSample Capture(DateTimeOffset nowUtc);
}

public interface IPrimitiveNormalizationStage
{
    PrimitiveSignalSnapshot Normalize(RawSignalSample raw);
}

public interface IInferenceStage
{
    InferredStateSnapshot Infer(PrimitiveSignalSnapshot primitive);
}

public interface IStabilizationStage
{
    InferredStateSnapshot Stabilize(InferredStateSnapshot inferred);
}

public interface IRegulationStage
{
    RegulationDecisionSnapshot Evaluate(InferredStateSnapshot stabilized, AgentControlState control);
}

public interface IBackendConnectivityProbe
{
    Task<bool> IsConnectedAsync(CancellationToken token);
}

public sealed class SystemSignalAcquisitionStage : ISignalAcquisitionStage
{
    public RawSignalSample Capture(DateTimeOffset nowUtc)
    {
        int processCount = Process.GetProcesses().Length;
        double seconds = nowUtc.ToUnixTimeMilliseconds() / 1000.0;
        double interaction = Math.Abs(Math.Sin(seconds / 6.0));
        double scroll = Math.Abs(Math.Cos(seconds / 8.0));
        double focusSwitch = Math.Abs(Math.Sin(seconds / 11.0));
        double processLoad = Math.Clamp(processCount / 400.0, 0.0, 1.0);

        return new RawSignalSample(
            TimestampUtc: nowUtc,
            InteractionLoad: interaction,
            ScrollLoad: scroll,
            FocusSwitchLoad: focusSwitch,
            ProcessCountLoad: processLoad);
    }
}

public sealed class DefaultPrimitiveNormalizationStage : IPrimitiveNormalizationStage
{
    public PrimitiveSignalSnapshot Normalize(RawSignalSample raw)
    {
        double typingBurst = Math.Clamp((raw.InteractionLoad * 0.7) + (raw.ProcessCountLoad * 0.3), 0, 1);
        double scrollVelocity = Math.Clamp((raw.ScrollLoad * 0.75) + (raw.InteractionLoad * 0.25), 0, 1);
        double focusSwitchRate = Math.Clamp((raw.FocusSwitchLoad * 0.8) + (raw.ProcessCountLoad * 0.2), 0, 1);
        var contributors = new List<string>(3);
        if (typingBurst > 0.6) contributors.Add("interaction_load");
        if (scrollVelocity > 0.6) contributors.Add("scroll_load");
        if (focusSwitchRate > 0.6) contributors.Add("focus_switch_load");
        if (contributors.Count == 0) contributors.Add("low_activity");

        return new PrimitiveSignalSnapshot(raw.TimestampUtc, typingBurst, scrollVelocity, focusSwitchRate, contributors);
    }
}

public sealed class DefaultInferenceStage : IInferenceStage
{
    public InferredStateSnapshot Infer(PrimitiveSignalSnapshot primitive)
    {
        double overload = Math.Clamp(
            (primitive.ScrollVelocityIndex * 0.45) +
            (primitive.FocusSwitchRate * 0.35) +
            (primitive.TypingBurstIntensity * 0.20),
            0, 1);

        double focus = Math.Clamp(
            (primitive.TypingBurstIntensity * 0.5) +
            ((1 - primitive.FocusSwitchRate) * 0.3) +
            ((1 - overload) * 0.2),
            0, 1);

        double fatigue = Math.Clamp((overload * 0.6) + ((1 - focus) * 0.4), 0, 1);
        double stability = Math.Clamp(1.0 - Math.Abs(overload - focus), 0, 1);
        double confidence = Math.Clamp(0.55 + (primitive.TypingBurstIntensity * 0.25) + (stability * 0.20), 0, 1);

        var contributors = new List<string>();
        if (overload >= 0.7) contributors.Add("high_overload_likelihood");
        if (fatigue >= 0.7) contributors.Add("high_fatigue_probability");
        if (focus >= 0.7) contributors.Add("high_focus_strength");
        if (contributors.Count == 0) contributors.Add("balanced_profile");

        return new InferredStateSnapshot(
            primitive.TimestampUtc,
            focus,
            overload,
            fatigue,
            stability,
            confidence,
            contributors);
    }
}

public sealed class EwmaStabilizationStage : IStabilizationStage
{
    private readonly object _gate = new();
    private InferredStateSnapshot? _previous;

    public InferredStateSnapshot Stabilize(InferredStateSnapshot inferred)
    {
        lock (_gate)
        {
            const double alpha = 0.35;
            if (_previous == null)
            {
                _previous = inferred;
                return inferred;
            }

            var smoothed = inferred with
            {
                FocusStrength = Blend(_previous.FocusStrength, inferred.FocusStrength, alpha),
                OverloadLikelihood = Blend(_previous.OverloadLikelihood, inferred.OverloadLikelihood, alpha),
                FatigueProbability = Blend(_previous.FatigueProbability, inferred.FatigueProbability, alpha),
                CognitiveStability = Blend(_previous.CognitiveStability, inferred.CognitiveStability, alpha),
                Confidence = Blend(_previous.Confidence, inferred.Confidence, alpha)
            };
            _previous = smoothed;
            return smoothed;
        }
    }

    private static double Blend(double previous, double current, double alpha)
    {
        return Math.Clamp((alpha * current) + ((1 - alpha) * previous), 0, 1);
    }
}

public sealed class DefaultRegulationStage : IRegulationStage
{
    private readonly object _gate = new();
    private readonly HybridPolicyEngine _policyEngine = new();
    private readonly IPolicyEnvelopeProvider _envelopeProvider;
    private readonly Dictionary<RegulationDomain, DomainRuntimeState> _domainStates = new()
    {
        [RegulationDomain.Overload] = new DomainRuntimeState(),
        [RegulationDomain.Focus] = new DomainRuntimeState(),
        [RegulationDomain.Fatigue] = new DomainRuntimeState()
    };
    private string _lastDeliveredAction = "starting";

    public DefaultRegulationStage(IPolicyEnvelopeProvider envelopeProvider)
    {
        _envelopeProvider = envelopeProvider;
    }

    public RegulationDecisionSnapshot Evaluate(InferredStateSnapshot stabilized, AgentControlState control)
    {
        lock (_gate)
        {
            if (!control.ConsentActive)
            {
                _lastDeliveredAction = "consent_required";
                ResetStateMemory();
                return new RegulationDecisionSnapshot(
                    Action: "consent_required",
                    ActivationWindowTriggered: false,
                    CooldownTriggered: false,
                    ActivationWindowOpen: false,
                    PolicyId: "consent-gate",
                    Domain: "control",
                    EscalationLevel: "Inform");
            }

            if (control.Paused)
            {
                _lastDeliveredAction = "paused_by_user";
                return new RegulationDecisionSnapshot(
                    Action: "paused_by_user",
                    ActivationWindowTriggered: false,
                    CooldownTriggered: false,
                    ActivationWindowOpen: false,
                    PolicyId: "pause-gate",
                    Domain: "control",
                    EscalationLevel: "Inform");
            }

            RegulationPolicyEnvelope envelope = _envelopeProvider.GetEnvelope();
            IReadOnlyDictionary<RegulationDomain, EffectiveRegulationPolicy> policies =
                _policyEngine.ResolvePolicies(control.RegulationIntensity, envelope);

            DomainDecision overloadDecision = EvaluateDomain(
                policies[RegulationDomain.Overload],
                _domainStates[RegulationDomain.Overload],
                stabilized,
                new DomainSignal(stabilized.OverloadLikelihood, Eligible: true, RecoveryTarget: 0.55));
            DomainDecision focusDecision = EvaluateDomain(
                policies[RegulationDomain.Focus],
                _domainStates[RegulationDomain.Focus],
                stabilized,
                new DomainSignal(
                    stabilized.FocusStrength,
                    Eligible: stabilized.OverloadLikelihood < 0.50,
                    RecoveryTarget: 0.60));
            DomainDecision fatigueDecision = EvaluateDomain(
                policies[RegulationDomain.Fatigue],
                _domainStates[RegulationDomain.Fatigue],
                stabilized,
                new DomainSignal(stabilized.FatigueProbability, Eligible: true, RecoveryTarget: 0.55));

            DomainDecision selected = SelectDecision(overloadDecision, focusDecision, fatigueDecision);
            string candidate = ApplyUserCategoryGates(selected.Domain, selected.Action, selected.Level, control);
            bool activationTriggered = IsInterventionAction(candidate)
                && (!string.Equals(candidate, _lastDeliveredAction, StringComparison.Ordinal) || selected.ActivationTriggered);
            bool cooldownTriggered = selected.CooldownBlocked;
            bool activationWindowOpen = overloadDecision.ActivationWindowOpen
                || focusDecision.ActivationWindowOpen
                || fatigueDecision.ActivationWindowOpen;

            _lastDeliveredAction = candidate;
            return new RegulationDecisionSnapshot(
                Action: candidate,
                ActivationWindowTriggered: activationTriggered,
                CooldownTriggered: cooldownTriggered,
                ActivationWindowOpen: activationWindowOpen,
                PolicyId: selected.PolicyId,
                Domain: selected.Domain.ToString().ToLowerInvariant(),
                EscalationLevel: selected.Level.ToString(),
                PlannedEndTimestampUtc: ResolvePlannedEndTimestamp(candidate, stabilized.TimestampUtc),
                CooldownEndTimestampUtc: selected.CooldownEndTimestampUtc);
        }
    }

    private DomainDecision EvaluateDomain(
        EffectiveRegulationPolicy policy,
        DomainRuntimeState runtime,
        InferredStateSnapshot snapshot,
        DomainSignal signal)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        double elapsedSeconds = ResolveElapsedSeconds(nowTicks, runtime.LastObservedMonotonicTicks);
        runtime.LastObservedMonotonicTicks = nowTicks;
        bool softConfidenceOk = snapshot.Confidence >= policy.ConfidenceGate;
        bool hardConfidenceOk = snapshot.Confidence >= policy.HardConfidenceGate;
        bool cooldownActive = runtime.CooldownUntilUtc.HasValue && snapshot.TimestampUtc < runtime.CooldownUntilUtc.Value;

        if (runtime.CurrentLevel > RegulationEscalationLevel.Inform && runtime.LastEscalationAtUtc.HasValue)
        {
            int interventionWindowSeconds = ResolveInterventionWindowSeconds(policy.Domain, runtime.CurrentLevel);
            if ((snapshot.TimestampUtc - runtime.LastEscalationAtUtc.Value).TotalSeconds >= interventionWindowSeconds)
            {
                RegulationEscalationLevel previousLevel = runtime.CurrentLevel;
                runtime.CurrentLevel = RegulationEscalationLevel.Inform;
                runtime.LastEscalationAtUtc = null;
                int cooldownSeconds = ResolveRecoveryCooldownSeconds(policy, previousLevel);
                runtime.CooldownUntilUtc = cooldownSeconds > 0
                    ? snapshot.TimestampUtc.AddSeconds(cooldownSeconds)
                    : null;
                runtime.RecoverySeconds = 0;
                runtime.ActivationSeconds = 0;
                runtime.HardActivationSeconds = 0;
                return new DomainDecision(
                    PolicyId: policy.PolicyId,
                    Domain: policy.Domain,
                    Level: RegulationEscalationLevel.Inform,
                    Action: "monitor_only",
                    SignalValue: signal.Value,
                    CooldownBlocked: false,
                    ActivationWindowOpen: false,
                    ActivationTriggered: false,
                    CooldownEndTimestampUtc: runtime.CooldownUntilUtc);
            }
        }

        bool softHit = signal.Eligible
            && softConfidenceOk
            && signal.Value >= policy.Thresholds.Soft;
        bool hardHit = signal.Eligible
            && hardConfidenceOk
            && signal.Value >= policy.Thresholds.Hard;

        runtime.ActivationSeconds = softHit && !cooldownActive
            ? runtime.ActivationSeconds + elapsedSeconds
            : 0d;
        runtime.HardActivationSeconds = hardHit && !cooldownActive
            ? runtime.HardActivationSeconds + elapsedSeconds
            : 0d;
        bool softActivationWindowOpen = runtime.ActivationSeconds >= policy.ActivationWindowSeconds;
        bool hardActivationWindowOpen = runtime.HardActivationSeconds >= policy.HardActivationWindowSeconds;
        bool activationWindowOpen = softActivationWindowOpen || hardActivationWindowOpen;

        bool recoveryCondition = signal.RecoveryValue <= signal.RecoveryTarget || !signal.Eligible;
        runtime.RecoverySeconds = runtime.CurrentLevel > RegulationEscalationLevel.Inform && recoveryCondition
            ? runtime.RecoverySeconds + elapsedSeconds
            : 0d;

        if (runtime.CurrentLevel > RegulationEscalationLevel.Inform
            && runtime.RecoverySeconds >= policy.RecoveryWindowSeconds)
        {
            RegulationEscalationLevel previousLevel = runtime.CurrentLevel;
            runtime.CurrentLevel = RegulationEscalationLevel.Inform;
            runtime.LastEscalationAtUtc = null;
            int cooldownSeconds = ResolveRecoveryCooldownSeconds(policy, previousLevel);
            runtime.CooldownUntilUtc = cooldownSeconds > 0
                ? snapshot.TimestampUtc.AddSeconds(cooldownSeconds)
                : null;
            runtime.RecoverySeconds = 0;
            runtime.ActivationSeconds = 0;
            runtime.HardActivationSeconds = 0;
            return new DomainDecision(
                PolicyId: policy.PolicyId,
                Domain: policy.Domain,
                Level: RegulationEscalationLevel.Inform,
                Action: "monitor_only",
                SignalValue: signal.Value,
                CooldownBlocked: false,
                ActivationWindowOpen: false,
                ActivationTriggered: false,
                CooldownEndTimestampUtc: runtime.CooldownUntilUtc);
        }

        bool activationTriggered = false;
        if (activationWindowOpen && !cooldownActive)
        {
            RegulationEscalationLevel target = runtime.CurrentLevel;
            if (hardHit && hardActivationWindowOpen)
            {
                target = policy.MaxEscalationLevel;
            }
            else if (softHit && softActivationWindowOpen)
            {
                if (runtime.CurrentLevel < RegulationEscalationLevel.Suggest)
                {
                    target = RegulationEscalationLevel.Suggest;
                }
                else if (runtime.CurrentLevel < policy.MaxEscalationLevel
                    && (!runtime.LastEscalationAtUtc.HasValue
                        || (snapshot.TimestampUtc - runtime.LastEscalationAtUtc.Value).TotalSeconds >= policy.ActivationWindowSeconds))
                {
                    target = (RegulationEscalationLevel)((int)runtime.CurrentLevel + 1);
                }
            }

            if (target > runtime.CurrentLevel)
            {
                runtime.CurrentLevel = target;
                runtime.LastEscalationAtUtc = snapshot.TimestampUtc;
                activationTriggered = true;
            }
        }

        if (runtime.CurrentLevel == RegulationEscalationLevel.Inform)
        {
            return new DomainDecision(
                PolicyId: policy.PolicyId,
                Domain: policy.Domain,
                Level: runtime.CurrentLevel,
                Action: "monitor_only",
                SignalValue: signal.Value,
                CooldownBlocked: (softHit || hardHit) && cooldownActive,
                ActivationWindowOpen: activationWindowOpen,
                ActivationTriggered: activationTriggered,
                CooldownEndTimestampUtc: runtime.CooldownUntilUtc);
        }

        string action = _policyEngine.ResolveAction(policy, runtime.CurrentLevel);
        if (!IsInterventionAction(action))
        {
            return new DomainDecision(
                PolicyId: policy.PolicyId,
                Domain: policy.Domain,
                Level: RegulationEscalationLevel.Inform,
                Action: "monitor_only",
                SignalValue: signal.Value,
                CooldownBlocked: (softHit || hardHit) && cooldownActive,
                ActivationWindowOpen: activationWindowOpen,
                ActivationTriggered: false,
                CooldownEndTimestampUtc: runtime.CooldownUntilUtc);
        }

        return new DomainDecision(
            PolicyId: policy.PolicyId,
            Domain: policy.Domain,
            Level: runtime.CurrentLevel,
            Action: action,
            SignalValue: signal.Value,
            CooldownBlocked: (softHit || hardHit) && cooldownActive,
            ActivationWindowOpen: activationWindowOpen,
            ActivationTriggered: activationTriggered,
            CooldownEndTimestampUtc: runtime.CooldownUntilUtc);
    }

    private static DomainDecision SelectDecision(params DomainDecision[] decisions)
    {
        DomainDecision selected = decisions[0];
        for (int i = 1; i < decisions.Length; i++)
        {
            DomainDecision current = decisions[i];
            if ((int)current.Level > (int)selected.Level)
            {
                selected = current;
                continue;
            }

            if ((int)current.Level == (int)selected.Level && current.SignalValue > selected.SignalValue)
            {
                selected = current;
            }
        }

        return selected;
    }

    private void ResetStateMemory()
    {
        foreach (DomainRuntimeState state in _domainStates.Values)
        {
            state.ActivationSeconds = 0;
            state.HardActivationSeconds = 0;
            state.RecoverySeconds = 0;
            state.CooldownUntilUtc = null;
            state.CurrentLevel = RegulationEscalationLevel.Inform;
            state.LastEscalationAtUtc = null;
            state.LastObservedMonotonicTicks = null;
        }
    }

    private static bool IsInterventionAction(string action)
    {
        return string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal)
            || string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal)
            || string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal)
            || string.Equals(action, "break_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "stretch_break_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "focus_lock_in_suggestion", StringComparison.Ordinal)
            || string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal)
            || string.Equals(action, "inform_overload_risk", StringComparison.Ordinal)
            || string.Equals(action, "inform_focus_protection", StringComparison.Ordinal)
            || string.Equals(action, "inform_fatigue_prevention", StringComparison.Ordinal);
    }

    private static string ApplyUserCategoryGates(RegulationDomain domain, string action, RegulationEscalationLevel level, AgentControlState control)
    {
        bool overloadNudgeAllowed = control.FocusModeEnabled
            || control.NotificationReductionEnabled
            || control.AnimationReductionEnabled;
        bool focusNudgeAllowed = control.FocusModeEnabled;
        bool fatigueNudgeAllowed = control.AnimationReductionEnabled;

        if (string.Equals(action, "temporary_app_restriction_enabled", StringComparison.Ordinal)
            && !control.TemporaryAppRestrictionEnabled)
        {
            if (domain == RegulationDomain.Focus)
            {
                return focusNudgeAllowed ? "focus_assist_enabled" : "focus_lock_in_suggestion";
            }

            if (domain == RegulationDomain.Fatigue)
            {
                return fatigueNudgeAllowed ? "fatigue_relief_enabled" : "stretch_break_suggestion";
            }

            return overloadNudgeAllowed ? "focus_mode_enabled" : "break_suggestion";
        }

        if (string.Equals(action, "focus_mode_enabled", StringComparison.Ordinal) && !overloadNudgeAllowed)
        {
            return "break_suggestion";
        }

        if (string.Equals(action, "focus_assist_enabled", StringComparison.Ordinal) && !focusNudgeAllowed)
        {
            return "focus_lock_in_suggestion";
        }

        if (string.Equals(action, "fatigue_relief_enabled", StringComparison.Ordinal) && !fatigueNudgeAllowed)
        {
            return "stretch_break_suggestion";
        }

        // Restrict escalation with all categories disabled falls back to suggest.
        if (level == RegulationEscalationLevel.Restrict
            && !control.TemporaryAppRestrictionEnabled
            && !overloadNudgeAllowed
            && !focusNudgeAllowed
            && !fatigueNudgeAllowed)
        {
            return domain switch
            {
                RegulationDomain.Focus => "focus_lock_in_suggestion",
                RegulationDomain.Fatigue => "stretch_break_suggestion",
                _ => "break_suggestion"
            };
        }

        return action;
    }

    private static int ResolveRecoveryCooldownSeconds(EffectiveRegulationPolicy policy, RegulationEscalationLevel previousLevel)
    {
        if (policy.Domain != RegulationDomain.Focus)
        {
            return policy.CooldownSeconds;
        }

        // Focus policy: no cooldown for level 1-2; cooldown only after restrict.
        return previousLevel >= RegulationEscalationLevel.Restrict ? policy.CooldownSeconds : 0;
    }

    private static int ResolveInterventionWindowSeconds(RegulationDomain domain, RegulationEscalationLevel level)
    {
        return level switch
        {
            RegulationEscalationLevel.Restrict => 180,
            RegulationEscalationLevel.Nudge => 600,
            RegulationEscalationLevel.Suggest => 300,
            RegulationEscalationLevel.Inform => 60,
            _ => 60
        };
    }

    private static double ResolveElapsedSeconds(long nowTicks, long? previousTicks)
    {
        if (!previousTicks.HasValue || nowTicks <= previousTicks.Value)
        {
            return 1d;
        }

        double elapsed = (double)(nowTicks - previousTicks.Value) / Stopwatch.Frequency;
        return Math.Clamp(elapsed, 0.05d, 5d);
    }

    private static DateTimeOffset? ResolvePlannedEndTimestamp(string action, DateTimeOffset nowUtc)
    {
        int seconds = action switch
        {
            "temporary_app_restriction_enabled" => 180,
            "focus_mode_enabled" => 600,
            "focus_assist_enabled" => 600,
            "fatigue_relief_enabled" => 600,
            "break_suggestion" => 300,
            "focus_lock_in_suggestion" => 300,
            "stretch_break_suggestion" => 300,
            "inform_overload_risk" => 60,
            "inform_focus_protection" => 60,
            "inform_fatigue_prevention" => 60,
            _ => 0
        };
        return seconds <= 0 ? null : nowUtc.AddSeconds(seconds);
    }

    private sealed class DomainRuntimeState
    {
        public double ActivationSeconds { get; set; }
        public double HardActivationSeconds { get; set; }
        public double RecoverySeconds { get; set; }
        public RegulationEscalationLevel CurrentLevel { get; set; } = RegulationEscalationLevel.Inform;
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset? LastEscalationAtUtc { get; set; }
        public long? LastObservedMonotonicTicks { get; set; }
    }

    private sealed record DomainSignal(
        double Value,
        bool Eligible,
        double RecoveryTarget)
    {
        public double RecoveryValue => Value;
    }

    private sealed record DomainDecision(
        string PolicyId,
        RegulationDomain Domain,
        RegulationEscalationLevel Level,
        string Action,
        double SignalValue,
        bool CooldownBlocked,
        bool ActivationWindowOpen,
        bool ActivationTriggered,
        DateTimeOffset? CooldownEndTimestampUtc);
}

public sealed class HttpBackendConnectivityProbe : IBackendConnectivityProbe
{
    private readonly string? _backend = Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");

    public async Task<bool> IsConnectedAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_backend) || !Uri.TryCreate(_backend, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        try
        {
            Uri healthUri = new UriBuilder(uri)
            {
                Path = "health",
                Query = string.Empty,
                Fragment = string.Empty
            }.Uri;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpRequestMessage request = new(HttpMethod.Get, healthUri);
            using HttpResponseMessage response = await http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
