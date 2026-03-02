using System.Security.Cryptography;
using Porta.Agent.Core.Security;

namespace Porta.Agent.Core.Buckets;

internal static class BucketPrivacyContract
{
#pragma warning disable CA1416
    private static readonly Lazy<byte[]> InstallSecret = new(() => Secrets.GetInstallSecret(), isThreadSafe: true);
#pragma warning restore CA1416

    private static readonly string[] SensitiveContributorPrefixes =
    [
        "app_id:",
        "window_id:",
        "process_id:",
        "foreground_app:"
    ];

    internal static AgentBrainSnapshot SanitizeForBucketPersistence(AgentBrainSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<string> contributors = snapshot.Contributors
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeContributor)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return snapshot with
        {
            TimestampUtc = snapshot.TimestampUtc.ToUniversalTime(),
            TypingBurstIntensity = UnitInterval(snapshot.TypingBurstIntensity),
            ScrollVelocityIndex = UnitInterval(snapshot.ScrollVelocityIndex),
            FocusSwitchRate = UnitInterval(snapshot.FocusSwitchRate),
            SmoothedTypingBurstIntensity = UnitInterval(snapshot.SmoothedTypingBurstIntensity),
            SmoothedScrollVelocityIndex = UnitInterval(snapshot.SmoothedScrollVelocityIndex),
            SmoothedFocusSwitchRate = UnitInterval(snapshot.SmoothedFocusSwitchRate),
            FocusStrength = UnitInterval(snapshot.FocusStrength),
            OverloadLikelihood = UnitInterval(snapshot.OverloadLikelihood),
            FatigueProbability = UnitInterval(snapshot.FatigueProbability),
            CognitiveStability = UnitInterval(snapshot.CognitiveStability),
            DominantStateValue = UnitInterval(snapshot.DominantStateValue),
            Confidence = UnitInterval(snapshot.Confidence),
            DominantStateName = string.IsNullOrWhiteSpace(snapshot.DominantStateName) ? "focus" : snapshot.DominantStateName.Trim(),
            LastAction = string.IsNullOrWhiteSpace(snapshot.LastAction) ? "monitor_only" : snapshot.LastAction.Trim(),
            PolicyId = string.IsNullOrWhiteSpace(snapshot.PolicyId) ? "n/a" : snapshot.PolicyId.Trim(),
            Domain = string.IsNullOrWhiteSpace(snapshot.Domain) ? "control" : snapshot.Domain.Trim(),
            EscalationLevel = string.IsNullOrWhiteSpace(snapshot.EscalationLevel) ? "Inform" : snapshot.EscalationLevel.Trim(),
            Contributors = contributors
        };
    }

    private static string NormalizeContributor(string contributor)
    {
        string trimmed = contributor.Trim();
        foreach (string prefix in SensitiveContributorPrefixes)
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string rawIdentifier = trimmed[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(rawIdentifier))
            {
                return prefix + "unknown";
            }

            if (LooksLikeTruncatedHash(rawIdentifier))
            {
                return prefix + rawIdentifier.ToLowerInvariant();
            }

            return prefix + HashIdentifier(rawIdentifier);
        }

        return trimmed;
    }

    private static string HashIdentifier(string rawIdentifier)
    {
        byte[] secret = InstallSecret.Value;
        using var hmac = new HMACSHA256(secret);
        byte[] hash = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawIdentifier));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static double UnitInterval(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 1d);
    }

    private static bool LooksLikeTruncatedHash(string rawIdentifier)
    {
        if (rawIdentifier.Length != 32)
        {
            return false;
        }

        foreach (char ch in rawIdentifier)
        {
            bool isHex = (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
