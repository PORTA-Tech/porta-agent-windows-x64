using System.Reflection;
using Porta.Agent.Core.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class CalibrationStageActionGateTests
{
    [Fact]
    public void ApplyCalibrationStageActionGate_OverloadSystemAction_DowngradesToInform()
    {
        string action = InvokeApplyCalibrationStageActionGate(
            "focus_mode_enabled",
            "overload",
            BaselineGatingResult.Provisional(PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCap),
            CalibrationTruth.TrustMedium);

        Assert.Equal("inform_overload_risk", action);
    }

    [Fact]
    public void ApplyCalibrationStageActionGate_FatigueSystemAction_DowngradesToInform()
    {
        string action = InvokeApplyCalibrationStageActionGate(
            "fatigue_relief_enabled",
            "fatigue",
            BaselineGatingResult.Provisional(PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCap),
            CalibrationTruth.TrustMedium);

        Assert.Equal("inform_fatigue_prevention", action);
    }

    [Fact]
    public void ApplyCalibrationStageActionGate_FocusSystemAction_MayDowngradeToSuggestion()
    {
        string action = InvokeApplyCalibrationStageActionGate(
            "focus_assist_enabled",
            "focus",
            BaselineGatingResult.Provisional(PortaExecutionConstants.BaselineReadiness.ProvisionalConfidenceCap),
            CalibrationTruth.TrustMedium);

        Assert.Equal("focus_lock_in_suggestion", action);
    }

    private static string InvokeApplyCalibrationStageActionGate(
        string action,
        string domain,
        BaselineGatingResult gating,
        string trustTier)
    {
        MethodInfo method = typeof(SqliteBrainStateStore).GetMethod(
            "ApplyCalibrationStageActionGate",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ApplyCalibrationStageActionGate was not found.");

        return (string)(method.Invoke(null, [action, domain, gating, trustTier])
            ?? throw new InvalidOperationException("ApplyCalibrationStageActionGate returned null."));
    }
}
