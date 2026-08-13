using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase C — the gate in front of the wire. Each of these guards a way a private note or an
/// empty header could reach a paying client, which is the one failure this whole feature exists to
/// make impossible.
/// </summary>
public sealed class ReportSendReadinessTests
{
    private static AgentReportDraft Draft(string clientName, params AgentReportLine[] lines)
        => new(
            ProjectId: 1,
            ProjectName: "nolan - chase invoice",
            ClientName: clientName,
            RangeStartLocal: "2026-08-12",
            RangeEndLocal: "2026-08-13",
            TotalHours: lines.Sum(l => l.Hours),
            DaysWorked: 1,
            LinesFinished: lines.Count(l => l.Finished),
            Lines: lines,
            Days: Array.Empty<AgentReportDay>());

    private static AgentReportLine Line(long id, string label, double hours = 1.0d)
        => new(
            TaskId: id,
            Title: $"finish my writting part {id}",
            ClientLabel: label,
            Finished: true,
            Hours: hours,
            UnattributedHours: 0d,
            UnattributedMeasurable: true);

    [Fact]
    public void AFullyNamedReportCanBeSent()
    {
        AgentReportReadiness ready = ReportSendReadiness.Evaluate(
            Draft("Nolan Studio", Line(1, "Chapter three — first draft")));

        Assert.True(ready.CanSend);
        Assert.False(ready.ProjectClientNameMissing);
        Assert.Empty(ready.TaskIdsMissingLabel);
        Assert.False(ready.HasNoLines);
    }

    /// <summary>
    /// §12.7 — the project name is the largest text a client reads, and the local one is typed on a
    /// morning card in the same private hurry as a task title. "nolan - chase invoice" in a header
    /// is the failure this blocks.
    /// </summary>
    [Fact]
    public void AProjectWithNoClientFacingNameCannotBeSent()
    {
        AgentReportReadiness ready = ReportSendReadiness.Evaluate(
            Draft(string.Empty, Line(1, "Chapter three — first draft")));

        Assert.False(ready.CanSend);
        Assert.True(ready.ProjectClientNameMissing);
    }

    /// <summary>§3 — the raw title is a private note and must never be the thing that ships.</summary>
    [Fact]
    public void AnUnlabelledLineCannotBeSent_AndIsNamed()
    {
        AgentReportReadiness ready = ReportSendReadiness.Evaluate(
            Draft("Nolan Studio", Line(1, "Chapter three"), Line(7, string.Empty)));

        Assert.False(ready.CanSend);
        Assert.Equal(new long[] { 7 }, ready.TaskIdsMissingLabel);
    }

    /// <summary>
    /// Whitespace is not a name. A single space passes a length check and prints an empty header.
    /// </summary>
    [Fact]
    public void WhitespaceIsNotAName()
    {
        Assert.True(ReportSendReadiness.Evaluate(Draft("   ", Line(1, "Chapter three")))
            .ProjectClientNameMissing);
        Assert.NotEmpty(ReportSendReadiness.Evaluate(Draft("Nolan Studio", Line(1, "  ")))
            .TaskIdsMissingLabel);
    }

    /// <summary>
    /// An excluded line is gone, not hidden (§12.3), so it needs no client-facing label. Requiring
    /// one would make the user name a line in order to not send it.
    /// </summary>
    [Fact]
    public void AnExcludedLineNeedsNoLabel()
    {
        AgentReportDraft draft = Draft("Nolan Studio", Line(1, "Chapter three"), Line(7, string.Empty));

        AgentReportReadiness ready = ReportSendReadiness.Evaluate(draft, new long[] { 7 });

        Assert.True(ready.CanSend);
        Assert.Empty(ready.TaskIdsMissingLabel);
    }

    /// <summary>A report with every line excluded bills a client for nothing.</summary>
    [Fact]
    public void AReportWithNoIncludedLinesCannotBeSent()
    {
        Assert.True(ReportSendReadiness.Evaluate(Draft("Nolan Studio")).HasNoLines);

        AgentReportReadiness allExcluded = ReportSendReadiness.Evaluate(
            Draft("Nolan Studio", Line(1, "Chapter three")), new long[] { 1 });

        Assert.False(allExcluded.CanSend);
        Assert.True(allExcluded.HasNoLines);
    }
}
