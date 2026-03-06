namespace Porta.Agent.Core.Tests.Acceptance;

internal sealed class AcceptanceGateReport
{
    private readonly List<GateResult> _results = [];

    public void AddThreshold(
        string gateName,
        string scenarioName,
        double measuredValue,
        double thresholdValue,
        bool passed,
        string? details = null)
    {
        _results.Add(new GateResult(
            GateName: gateName,
            ScenarioName: scenarioName,
            Passed: passed,
            MeasuredValue: measuredValue.ToString("0.###"),
            ThresholdValue: thresholdValue.ToString("0.###"),
            Details: details ?? string.Empty));
    }

    public void AddCheck(
        string gateName,
        string scenarioName,
        bool passed,
        string details = "")
    {
        _results.Add(new GateResult(
            GateName: gateName,
            ScenarioName: scenarioName,
            Passed: passed,
            MeasuredValue: passed ? "ok" : "failed",
            ThresholdValue: "required",
            Details: details));
    }

    public void AssertAllPassed()
    {
        bool allPassed = _results.All(r => r.Passed);
        Assert.True(allPassed, BuildSummary());
    }

    public string BuildSummary()
    {
        var lines = new List<string>(_results.Count + 1)
        {
            "Acceptance Gate Report:"
        };

        foreach (GateResult result in _results)
        {
            lines.Add(
                $"{result.GateName} [{result.ScenarioName}]: {(result.Passed ? "PASS" : "FAIL")} " +
                $"(measured={result.MeasuredValue}, threshold={result.ThresholdValue}) " +
                $"{result.Details}".TrimEnd());
        }

        return string.Join(Environment.NewLine, lines);
    }

    private readonly record struct GateResult(
        string GateName,
        string ScenarioName,
        bool Passed,
        string MeasuredValue,
        string ThresholdValue,
        string Details);
}
