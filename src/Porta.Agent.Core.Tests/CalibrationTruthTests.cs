using Porta.Agent.Core;

namespace Porta.Agent.Core.Tests;

public sealed class CalibrationTruthTests
{
    [Fact]
    public void ResolveTrustTier_ColdStage_IsAlwaysLow()
    {
        string trust = CalibrationTruth.ResolveTrustTier(
            CalibrationTruth.StageCold,
            confidence: 0.95d,
            signalQualityScore: 0.95d,
            partialData: false);

        Assert.Equal(CalibrationTruth.TrustLow, trust);
    }

    [Fact]
    public void ResolveTrustTier_ProvisionalWithUsableConfidenceAndQuality_IsMedium()
    {
        string trust = CalibrationTruth.ResolveTrustTier(
            CalibrationTruth.StageProvisional,
            confidence: 0.62d,
            signalQualityScore: 0.72d,
            partialData: false);

        Assert.Equal(CalibrationTruth.TrustMedium, trust);
    }

    [Fact]
    public void ResolveTrustTier_CalibratedHighConfidenceAndStableQuality_IsHigh()
    {
        string trust = CalibrationTruth.ResolveTrustTier(
            CalibrationTruth.StageCalibrated,
            confidence: 0.80d,
            signalQualityScore: 0.83d,
            partialData: false);

        Assert.Equal(CalibrationTruth.TrustHigh, trust);
    }

    [Fact]
    public void ResolveTrustTier_UnstableQuality_ForcesLow()
    {
        string trust = CalibrationTruth.ResolveTrustTier(
            CalibrationTruth.StageCalibrated,
            confidence: 0.81d,
            signalQualityScore: 0.40d,
            partialData: false);

        Assert.Equal(CalibrationTruth.TrustLow, trust);
    }

    [Fact]
    public void ResolveTrustTier_PartialData_DoesNotOverrideUsableConfidenceAndQuality()
    {
        string trust = CalibrationTruth.ResolveTrustTier(
            CalibrationTruth.StageProvisional,
            confidence: 0.62d,
            signalQualityScore: 0.72d,
            partialData: true);

        Assert.Equal(CalibrationTruth.TrustMedium, trust);
    }
}
