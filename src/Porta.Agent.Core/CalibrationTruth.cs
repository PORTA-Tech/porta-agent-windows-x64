namespace Porta.Agent.Core;

public static class CalibrationTruth
{
    public const string StageCold = "cold";
    public const string StageProvisional = "provisional";
    public const string StageCalibrated = "calibrated";

    public const string TrustLow = "low";
    public const string TrustMedium = "medium";
    public const string TrustHigh = "high";

    public const double MediumTrustConfidenceMin = 0.50d;
    public const double HighTrustConfidenceMin = 0.75d;
    public const double UnstableQualityThreshold = 0.55d;
    public const double HighTrustQualityThreshold = 0.65d;

    public static string ResolveTrustTier(
        string? calibrationStage,
        double confidence,
        double signalQualityScore = 1.0d,
        bool partialData = false)
    {
        string normalizedStage = NormalizeStage(calibrationStage);
        double clampedConfidence = Math.Clamp(confidence, 0d, 1d);
        double clampedQuality = Math.Clamp(signalQualityScore, 0d, 1d);
        if (string.Equals(normalizedStage, StageCold, StringComparison.Ordinal))
        {
            return TrustLow;
        }

        if (partialData || clampedQuality < UnstableQualityThreshold || clampedConfidence < MediumTrustConfidenceMin)
        {
            return TrustLow;
        }

        if (string.Equals(normalizedStage, StageCalibrated, StringComparison.Ordinal)
            && clampedConfidence >= HighTrustConfidenceMin
            && clampedQuality >= HighTrustQualityThreshold)
        {
            return TrustHigh;
        }

        if ((string.Equals(normalizedStage, StageProvisional, StringComparison.Ordinal)
                || string.Equals(normalizedStage, StageCalibrated, StringComparison.Ordinal))
            && clampedConfidence >= MediumTrustConfidenceMin)
        {
            return TrustMedium;
        }

        return TrustLow;
    }

    public static bool AllowsGuidanceForTrust(string? trustTier)
    {
        return NormalizeTrustTier(trustTier) is TrustLow or TrustMedium or TrustHigh;
    }

    public static bool AllowsSuggestionsForTrust(string? trustTier)
    {
        string normalized = NormalizeTrustTier(trustTier);
        return string.Equals(normalized, TrustMedium, StringComparison.Ordinal)
            || string.Equals(normalized, TrustHigh, StringComparison.Ordinal);
    }

    public static bool AllowsAutomaticActionsForTrust(string? trustTier)
    {
        return string.Equals(NormalizeTrustTier(trustTier), TrustHigh, StringComparison.Ordinal);
    }

    public static bool AllowsSuggestions(string? calibrationStage)
    {
        string normalizedStage = NormalizeStage(calibrationStage);
        return string.Equals(normalizedStage, StageProvisional, StringComparison.Ordinal)
            || string.Equals(normalizedStage, StageCalibrated, StringComparison.Ordinal);
    }

    public static bool AllowsSystemActions(string? calibrationStage)
    {
        return string.Equals(NormalizeStage(calibrationStage), StageCalibrated, StringComparison.Ordinal);
    }

    public static string NormalizeStage(string? calibrationStage)
    {
        if (string.IsNullOrWhiteSpace(calibrationStage))
        {
            return StageCold;
        }

        string normalized = calibrationStage.Trim().ToLowerInvariant();
        return normalized switch
        {
            StageProvisional => StageProvisional,
            StageCalibrated => StageCalibrated,
            _ => StageCold
        };
    }

    public static string NormalizeTrustTier(string? trustTier)
    {
        if (string.IsNullOrWhiteSpace(trustTier))
        {
            return TrustLow;
        }

        string normalized = trustTier.Trim().ToLowerInvariant();
        return normalized switch
        {
            TrustMedium => TrustMedium,
            TrustHigh => TrustHigh,
            _ => TrustLow
        };
    }
}
