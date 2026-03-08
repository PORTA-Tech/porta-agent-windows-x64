namespace Porta.Agent.Core;

public static class CalibrationTruth
{
    public const string StageCold = "cold";
    public const string StageProvisional = "provisional";
    public const string StageCalibrated = "calibrated";

    public const string TrustLow = "low";
    public const string TrustMedium = "medium";
    public const string TrustHigh = "high";

    public static string ResolveTrustTier(string? calibrationStage, double confidence)
    {
        string normalizedStage = NormalizeStage(calibrationStage);
        double clampedConfidence = Math.Clamp(confidence, 0d, 1d);
        if (string.Equals(normalizedStage, StageCold, StringComparison.Ordinal))
        {
            return TrustLow;
        }

        if (string.Equals(normalizedStage, StageCalibrated, StringComparison.Ordinal) && clampedConfidence >= 0.82d)
        {
            return TrustHigh;
        }

        if (clampedConfidence >= 0.55d || string.Equals(normalizedStage, StageProvisional, StringComparison.Ordinal))
        {
            return TrustMedium;
        }

        return TrustLow;
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
}
