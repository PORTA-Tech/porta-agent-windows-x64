using System.Text.Json;

namespace Porta.Agent.Core;

public enum RegulationDomain
{
    Overload = 0,
    Focus = 1,
    Fatigue = 2
}

public enum RegulationEscalationLevel
{
    Inform = 0,
    Suggest = 1,
    Nudge = 2,
    Restrict = 3
}

public sealed record PolicyThresholds(
    double Soft,
    double Hard);

public sealed record PolicyIntensityModifier(
    double ThresholdDelta,
    int ActivationWindowDeltaSeconds,
    int RecoveryWindowDeltaSeconds,
    int CooldownDeltaSeconds,
    int MaxEscalationDelta);

public sealed record PolicyIntensityProfileModifiers(
    PolicyIntensityModifier Gentle,
    PolicyIntensityModifier Balanced,
    PolicyIntensityModifier Strong);

public sealed record RegulationPolicyDefinition(
    string PolicyId,
    RegulationDomain Domain,
    PolicyThresholds Thresholds,
    int ActivationWindowSeconds,
    int HardActivationWindowSeconds,
    int RecoveryWindowSeconds,
    double ConfidenceGate,
    double HardConfidenceGate,
    int CooldownSeconds,
    RegulationEscalationLevel MaxEscalationLevel,
    IReadOnlyList<string> AllowedActions,
    PolicyIntensityProfileModifiers IntensityProfileModifiers);

public sealed record RegulationPolicyOverride(
    RegulationDomain Domain,
    PolicyThresholds? Thresholds,
    int? ActivationWindowSeconds,
    int? HardActivationWindowSeconds,
    int? RecoveryWindowSeconds,
    double? ConfidenceGate,
    double? HardConfidenceGate,
    int? CooldownSeconds,
    RegulationEscalationLevel? MaxEscalationLevel,
    IReadOnlyList<string>? AllowedActions,
    PolicyIntensityProfileModifiers? IntensityProfileModifiers);

public sealed record RegulationPolicyEnvelope(
    string Version,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<RegulationPolicyOverride> Overrides);

public sealed record EffectiveRegulationPolicy(
    string PolicyId,
    RegulationDomain Domain,
    PolicyThresholds Thresholds,
    int ActivationWindowSeconds,
    int HardActivationWindowSeconds,
    int RecoveryWindowSeconds,
    double ConfidenceGate,
    double HardConfidenceGate,
    int CooldownSeconds,
    RegulationEscalationLevel MaxEscalationLevel,
    IReadOnlyList<string> AllowedActions);

public interface IPolicyEnvelopeProvider
{
    RegulationPolicyEnvelope GetEnvelope();
}

public sealed class FilePolicyEnvelopeProvider : IPolicyEnvelopeProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private RegulationPolicyEnvelope _cached = EmptyEnvelope();
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFileWriteUtc = DateTimeOffset.MinValue;
    private readonly string _envelopePath;

    public FilePolicyEnvelopeProvider()
    {
        _envelopePath = ResolveEnvelopePath();
    }

    public RegulationPolicyEnvelope GetEnvelope()
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
                EnvelopeDocument? parsed = JsonSerializer.Deserialize<EnvelopeDocument>(raw, JsonOptions);
                if (parsed?.Overrides == null)
                {
                    _cached = EmptyEnvelope();
                    return _cached;
                }

                var overrides = new List<RegulationPolicyOverride>(parsed.Overrides.Count);
                foreach (OverrideDocument doc in parsed.Overrides)
                {
                    if (!TryParseDomain(doc.Domain, out RegulationDomain domain))
                    {
                        continue;
                    }

                    IReadOnlyList<string>? allowedActions = doc.AllowedActions?.Where(static x => !string.IsNullOrWhiteSpace(x))
                        .Select(static x => x.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    PolicyIntensityProfileModifiers? modifiers = doc.IntensityProfileModifiers?.ToModel();
                    overrides.Add(new RegulationPolicyOverride(
                        Domain: domain,
                        Thresholds: doc.Thresholds?.ToModel(),
                        ActivationWindowSeconds: doc.ActivationWindowSeconds,
                        HardActivationWindowSeconds: doc.HardActivationWindowSeconds,
                        RecoveryWindowSeconds: doc.RecoveryWindowSeconds,
                        ConfidenceGate: doc.ConfidenceGate,
                        HardConfidenceGate: doc.HardConfidenceGate,
                        CooldownSeconds: doc.CooldownSeconds,
                        MaxEscalationLevel: TryParseEscalation(doc.MaxEscalationLevel),
                        AllowedActions: allowedActions,
                        IntensityProfileModifiers: modifiers));
                }

                _cached = new RegulationPolicyEnvelope(
                    Version: string.IsNullOrWhiteSpace(parsed.Version) ? "local-default" : parsed.Version.Trim(),
                    UpdatedAtUtc: parsed.UpdatedAtUtc ?? nowUtc,
                    Overrides: overrides);
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

    private static bool TryParseDomain(string? raw, out RegulationDomain domain)
    {
        return Enum.TryParse(raw, ignoreCase: true, out domain);
    }

    private static RegulationEscalationLevel? TryParseEscalation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse(raw, ignoreCase: true, out RegulationEscalationLevel parsed)
            ? parsed
            : null;
    }

    private static RegulationPolicyEnvelope EmptyEnvelope()
    {
        return new RegulationPolicyEnvelope("local-default", DateTimeOffset.UtcNow, []);
    }

    private sealed record EnvelopeDocument(
        string? Version,
        DateTimeOffset? UpdatedAtUtc,
        IReadOnlyList<OverrideDocument>? Overrides);

    private sealed record OverrideDocument(
        string? Domain,
        ThresholdsDocument? Thresholds,
        int? ActivationWindowSeconds,
        int? HardActivationWindowSeconds,
        int? RecoveryWindowSeconds,
        double? ConfidenceGate,
        double? HardConfidenceGate,
        int? CooldownSeconds,
        string? MaxEscalationLevel,
        IReadOnlyList<string>? AllowedActions,
        IntensityProfilesDocument? IntensityProfileModifiers);

    private sealed record ThresholdsDocument(
        double Soft,
        double Hard)
    {
        public PolicyThresholds ToModel()
        {
            return new PolicyThresholds(Soft, Hard);
        }
    }

    private sealed record IntensityProfilesDocument(
        IntensityModifierDocument? Gentle,
        IntensityModifierDocument? Balanced,
        IntensityModifierDocument? Strong)
    {
        public PolicyIntensityProfileModifiers ToModel()
        {
            return new PolicyIntensityProfileModifiers(
                Gentle: Gentle?.ToModel() ?? new PolicyIntensityModifier(0, 0, 0, 0, 0),
                Balanced: Balanced?.ToModel() ?? new PolicyIntensityModifier(0, 0, 0, 0, 0),
                Strong: Strong?.ToModel() ?? new PolicyIntensityModifier(0, 0, 0, 0, 0));
        }
    }

    private sealed record IntensityModifierDocument(
        double ThresholdDelta,
        int ActivationWindowDeltaSeconds,
        int RecoveryWindowDeltaSeconds,
        int CooldownDeltaSeconds,
        int MaxEscalationDelta)
    {
        public PolicyIntensityModifier ToModel()
        {
            return new PolicyIntensityModifier(
                ThresholdDelta,
                ActivationWindowDeltaSeconds,
                RecoveryWindowDeltaSeconds,
                CooldownDeltaSeconds,
                MaxEscalationDelta);
        }
    }
}

public sealed class HybridPolicyEngine
{
    private static readonly IReadOnlyDictionary<RegulationDomain, PolicySafetyBounds> SafetyBounds =
        new Dictionary<RegulationDomain, PolicySafetyBounds>
        {
            [RegulationDomain.Overload] = new PolicySafetyBounds(0.45, 0.92, 5, 900, 10, 1200, 0.40, 0.95, 30, 3600, RegulationEscalationLevel.Restrict),
            [RegulationDomain.Focus] = new PolicySafetyBounds(0.55, 0.95, 10, 1800, 10, 1800, 0.40, 0.95, 30, 3600, RegulationEscalationLevel.Restrict),
            [RegulationDomain.Fatigue] = new PolicySafetyBounds(0.50, 0.94, 15, 2400, 15, 2400, 0.40, 0.95, 60, 5400, RegulationEscalationLevel.Restrict)
        };

    private readonly IReadOnlyDictionary<RegulationDomain, RegulationPolicyDefinition> _localPolicies;

    public HybridPolicyEngine()
    {
        _localPolicies = BuildLocalPolicies();
    }

    public IReadOnlyDictionary<RegulationDomain, EffectiveRegulationPolicy> ResolvePolicies(
        string? intensity,
        RegulationPolicyEnvelope envelope)
    {
        var resolved = new Dictionary<RegulationDomain, EffectiveRegulationPolicy>(capacity: _localPolicies.Count);
        string normalizedIntensity = NormalizeIntensity(intensity);
        var overrides = envelope.Overrides.ToDictionary(static x => x.Domain, static x => x);

        foreach ((RegulationDomain domain, RegulationPolicyDefinition local) in _localPolicies)
        {
            PolicySafetyBounds bounds = SafetyBounds[domain];
            RegulationPolicyOverride? domainOverride = overrides.GetValueOrDefault(domain);
            PolicyIntensityProfileModifiers modifiers = domainOverride?.IntensityProfileModifiers ?? local.IntensityProfileModifiers;
            PolicyIntensityModifier activeModifier = ResolveIntensityModifier(modifiers, normalizedIntensity);

            PolicyThresholds baseThresholds = domainOverride?.Thresholds ?? local.Thresholds;
            double soft = Clamp(baseThresholds.Soft + activeModifier.ThresholdDelta, bounds.ThresholdMin, bounds.ThresholdMax);
            double hard = Clamp(baseThresholds.Hard + activeModifier.ThresholdDelta, bounds.ThresholdMin, bounds.ThresholdMax);
            if (hard < soft + 0.05)
            {
                hard = Math.Min(bounds.ThresholdMax, soft + 0.05);
            }

            int activationWindow = ClampInt(
                (domainOverride?.ActivationWindowSeconds ?? local.ActivationWindowSeconds) + activeModifier.ActivationWindowDeltaSeconds,
                bounds.ActivationMinSeconds,
                bounds.ActivationMaxSeconds);
            int hardActivationWindow = ClampInt(
                (domainOverride?.HardActivationWindowSeconds ?? local.HardActivationWindowSeconds) + activeModifier.ActivationWindowDeltaSeconds,
                bounds.ActivationMinSeconds,
                bounds.ActivationMaxSeconds);
            int recoveryWindow = ClampInt(
                (domainOverride?.RecoveryWindowSeconds ?? local.RecoveryWindowSeconds) + activeModifier.RecoveryWindowDeltaSeconds,
                bounds.RecoveryMinSeconds,
                bounds.RecoveryMaxSeconds);
            int cooldownWindow = ClampInt(
                (domainOverride?.CooldownSeconds ?? local.CooldownSeconds) + activeModifier.CooldownDeltaSeconds,
                bounds.CooldownMinSeconds,
                bounds.CooldownMaxSeconds);
            double confidenceGate = Clamp(
                domainOverride?.ConfidenceGate ?? local.ConfidenceGate,
                bounds.ConfidenceMin,
                bounds.ConfidenceMax);
            double hardConfidenceGate = Clamp(
                domainOverride?.HardConfidenceGate ?? local.HardConfidenceGate,
                bounds.ConfidenceMin,
                bounds.ConfidenceMax);

            RegulationEscalationLevel localMax = MinEscalation(local.MaxEscalationLevel, bounds.LocalMaxEscalation);
            RegulationEscalationLevel overrideMax = domainOverride?.MaxEscalationLevel ?? localMax;
            RegulationEscalationLevel modifierMax = ApplyEscalationDelta(localMax, activeModifier.MaxEscalationDelta);
            RegulationEscalationLevel effectiveMax = MinEscalation(
                MinEscalation(overrideMax, modifierMax),
                localMax);

            IReadOnlyList<string> allowedActions = ResolveAllowedActions(local, domainOverride);
            resolved[domain] = new EffectiveRegulationPolicy(
                PolicyId: local.PolicyId,
                Domain: local.Domain,
                Thresholds: new PolicyThresholds(soft, hard),
                ActivationWindowSeconds: activationWindow,
                HardActivationWindowSeconds: hardActivationWindow,
                RecoveryWindowSeconds: recoveryWindow,
                ConfidenceGate: confidenceGate,
                HardConfidenceGate: hardConfidenceGate,
                CooldownSeconds: cooldownWindow,
                MaxEscalationLevel: effectiveMax,
                AllowedActions: allowedActions);
        }

        return resolved;
    }

    public string ResolveAction(EffectiveRegulationPolicy policy, RegulationEscalationLevel level)
    {
        string[] candidates = ResolveCandidates(policy.Domain, level);

        var allowed = policy.AllowedActions.Count == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(policy.AllowedActions, StringComparer.OrdinalIgnoreCase);
        foreach (string candidate in candidates)
        {
            if (allowed.Count == 0 || allowed.Contains(candidate))
            {
                return candidate;
            }
        }

        return "monitor_only";
    }

    private static string[] ResolveCandidates(RegulationDomain domain, RegulationEscalationLevel level)
    {
        return domain switch
        {
            RegulationDomain.Focus => level switch
            {
                RegulationEscalationLevel.Restrict => ["temporary_app_restriction_enabled", "focus_assist_enabled", "focus_lock_in_suggestion", "inform_focus_protection"],
                RegulationEscalationLevel.Nudge => ["focus_assist_enabled", "focus_lock_in_suggestion", "inform_focus_protection"],
                RegulationEscalationLevel.Suggest => ["focus_lock_in_suggestion", "inform_focus_protection"],
                _ => ["inform_focus_protection"]
            },
            RegulationDomain.Fatigue => level switch
            {
                RegulationEscalationLevel.Restrict => ["temporary_app_restriction_enabled", "fatigue_relief_enabled", "inform_fatigue_prevention"],
                RegulationEscalationLevel.Nudge => ["fatigue_relief_enabled", "inform_fatigue_prevention"],
                RegulationEscalationLevel.Suggest => ["inform_fatigue_prevention"],
                _ => ["inform_fatigue_prevention"]
            },
            _ => level switch
            {
                RegulationEscalationLevel.Restrict => ["temporary_app_restriction_enabled", "focus_mode_enabled", "inform_overload_risk"],
                RegulationEscalationLevel.Nudge => ["focus_mode_enabled", "inform_overload_risk"],
                RegulationEscalationLevel.Suggest => ["inform_overload_risk"],
                _ => ["inform_overload_risk"]
            }
        };
    }

    private static IReadOnlyList<string> ResolveAllowedActions(RegulationPolicyDefinition local, RegulationPolicyOverride? domainOverride)
    {
        var localSet = new HashSet<string>(local.AllowedActions, StringComparer.OrdinalIgnoreCase);
        if (domainOverride?.AllowedActions == null || domainOverride.AllowedActions.Count == 0)
        {
            return localSet.ToArray();
        }

        var constrained = domainOverride.AllowedActions
            .Where(localSet.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return constrained.Length == 0 ? localSet.ToArray() : constrained;
    }

    private static PolicyIntensityModifier ResolveIntensityModifier(PolicyIntensityProfileModifiers modifiers, string intensity)
    {
        return intensity switch
        {
            "gentle" => modifiers.Gentle,
            "strong" => modifiers.Strong,
            _ => modifiers.Balanced
        };
    }

    private static string NormalizeIntensity(string? intensity)
    {
        return string.IsNullOrWhiteSpace(intensity)
            ? "balanced"
            : intensity.Trim().ToLowerInvariant();
    }

    private static IReadOnlyDictionary<RegulationDomain, RegulationPolicyDefinition> BuildLocalPolicies()
    {
        var intensity = new PolicyIntensityProfileModifiers(
            Gentle: new PolicyIntensityModifier(+0.08, +10, +20, +180, -1),
            Balanced: new PolicyIntensityModifier(0, 0, 0, 0, 0),
            Strong: new PolicyIntensityModifier(-0.08, -5, -10, -120, 0));

        return new Dictionary<RegulationDomain, RegulationPolicyDefinition>
        {
            [RegulationDomain.Overload] = new RegulationPolicyDefinition(
                PolicyId: "overload-relief-v1",
                Domain: RegulationDomain.Overload,
                Thresholds: new PolicyThresholds(Soft: 0.65, Hard: 0.80),
                ActivationWindowSeconds: 15,
                HardActivationWindowSeconds: 20,
                RecoveryWindowSeconds: 60,
                ConfidenceGate: 0.60,
                HardConfidenceGate: 0.70,
                CooldownSeconds: 600,
                MaxEscalationLevel: RegulationEscalationLevel.Restrict,
                AllowedActions: ["inform_overload_risk", "focus_mode_enabled", "temporary_app_restriction_enabled"],
                IntensityProfileModifiers: intensity),
            [RegulationDomain.Focus] = new RegulationPolicyDefinition(
                PolicyId: "focus-protection-v1",
                Domain: RegulationDomain.Focus,
                Thresholds: new PolicyThresholds(Soft: 0.75, Hard: 0.88),
                ActivationWindowSeconds: 60,
                HardActivationWindowSeconds: 60,
                RecoveryWindowSeconds: 60,
                ConfidenceGate: 0.60,
                HardConfidenceGate: 0.60,
                CooldownSeconds: 600,
                MaxEscalationLevel: RegulationEscalationLevel.Restrict,
                AllowedActions: ["inform_focus_protection", "focus_lock_in_suggestion", "focus_assist_enabled", "temporary_app_restriction_enabled"],
                IntensityProfileModifiers: intensity),
            [RegulationDomain.Fatigue] = new RegulationPolicyDefinition(
                PolicyId: "fatigue-prevention-v1",
                Domain: RegulationDomain.Fatigue,
                Thresholds: new PolicyThresholds(Soft: 0.70, Hard: 0.82),
                ActivationWindowSeconds: 120,
                HardActivationWindowSeconds: 120,
                RecoveryWindowSeconds: 180,
                ConfidenceGate: 0.60,
                HardConfidenceGate: 0.60,
                CooldownSeconds: 900,
                MaxEscalationLevel: RegulationEscalationLevel.Restrict,
                AllowedActions: ["inform_fatigue_prevention", "fatigue_relief_enabled", "temporary_app_restriction_enabled"],
                IntensityProfileModifiers: intensity)
        };
    }

    private static RegulationEscalationLevel MinEscalation(RegulationEscalationLevel left, RegulationEscalationLevel right)
    {
        return (RegulationEscalationLevel)Math.Min((int)left, (int)right);
    }

    private static RegulationEscalationLevel ApplyEscalationDelta(RegulationEscalationLevel start, int delta)
    {
        int value = Math.Clamp((int)start + delta, (int)RegulationEscalationLevel.Inform, (int)RegulationEscalationLevel.Restrict);
        return (RegulationEscalationLevel)value;
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Clamp(value, min, max);
    }

    private static int ClampInt(int value, int min, int max)
    {
        return Math.Clamp(value, min, max);
    }

    private sealed record PolicySafetyBounds(
        double ThresholdMin,
        double ThresholdMax,
        int ActivationMinSeconds,
        int ActivationMaxSeconds,
        int RecoveryMinSeconds,
        int RecoveryMaxSeconds,
        double ConfidenceMin,
        double ConfidenceMax,
        int CooldownMinSeconds,
        int CooldownMaxSeconds,
        RegulationEscalationLevel LocalMaxEscalation);
}
