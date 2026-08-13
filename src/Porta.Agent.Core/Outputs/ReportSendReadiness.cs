namespace Porta.Agent.Core.Outputs;

/// <summary>
/// Step 10 phase C (2026-08-13) — whether a drafted report is fit to leave the machine.
///
/// <b>This is the ONLY implementation of that question.</b> Two things in the spec say a report
/// cannot be sent until it is named — the project's client-facing name (`Step10_Report_Spec.md`
/// §12.7: "the header is the biggest text a client reads") and every included line's client-facing
/// label (§3) — and both exist because the fallback is a private note typed on a morning card
/// reaching a paying client. A second copy of this rule somewhere in the UI is how one of them
/// quietly stops being enforced, which is the same failure the digest and the card already had once.
///
/// It is a pure function of the draft plus the set the user excluded, so the Reports page and the
/// send path cannot disagree about what "ready" means.
/// </summary>
public static class ReportSendReadiness
{
    /// <summary>
    /// Excluded lines are genuinely gone, not hidden: they are absent from the payload
    /// (`Step10_Report_Spec.md` §12.3), their hours leave the total (§3), and they therefore do
    /// <b>not</b> need a client-facing label. Requiring one would make the user name a line in
    /// order to not send it.
    /// </summary>
    public static AgentReportReadiness Evaluate(
        AgentReportDraft draft,
        IReadOnlyCollection<long>? excludedTaskIds = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        IReadOnlyCollection<long> excluded = excludedTaskIds ?? Array.Empty<long>();

        List<AgentReportLine> included = draft.Lines
            .Where(l => !excluded.Contains(l.TaskId))
            .ToList();

        // Whitespace is not a name. A label of " " would satisfy a length check and print an empty
        // line in a client's header, which is worse than being asked for it.
        bool projectNameMissing = string.IsNullOrWhiteSpace(draft.ClientName);

        IReadOnlyList<long> missingLabel = included
            .Where(l => string.IsNullOrWhiteSpace(l.ClientLabel))
            .Select(l => l.TaskId)
            .ToList();

        // Nothing to send is not a failure to report, but it is still not sendable: a report with
        // no lines is a document telling a client they were billed for nothing.
        bool noLines = included.Count == 0;

        return new AgentReportReadiness(
            CanSend: !projectNameMissing && missingLabel.Count == 0 && !noLines,
            HasNoLines: noLines,
            ProjectClientNameMissing: projectNameMissing,
            TaskIdsMissingLabel: missingLabel);
    }
}

/// <summary>
/// Why a report cannot be sent yet, in facts rather than in sentences: the wording belongs to
/// whatever is showing it to the user, and the rule belongs here.
/// </summary>
public sealed record AgentReportReadiness(
    bool CanSend,
    bool HasNoLines,
    bool ProjectClientNameMissing,
    IReadOnlyList<long> TaskIdsMissingLabel);
