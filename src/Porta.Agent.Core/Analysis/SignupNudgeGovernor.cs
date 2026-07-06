namespace Porta.Agent.Core.Analysis;

// Phase 1c — Step 3a: decides whether an UNLINKED device should be nudged to sign in right now.
// PURE logic (no I/O): the caller gathers the inputs (link status, days of local data, cadence) and
// acts on the decision. Conservative and self-limiting by design — see PortaExecutionConstants.SignupNudge.
// Worst case is "no nudge"; it can never touch tracking, pressure, or actuation.

internal readonly record struct SignupNudgeGovernorSettings(
    int MinDaysOfData,
    int MinDaysBetweenNudges,
    int MaxLifetimeNudges);

internal readonly record struct SignupNudgeGovernorInput(
    DateTimeOffset NowUtc,
    bool IsLinked,
    int DaysOfData,
    bool Paused,
    bool ConsentActive,
    int NudgesShownTotal,
    DateTimeOffset? LastNudgeAtUtc);

internal readonly record struct SignupNudgeDecision(
    bool ShouldNudge,
    string ReasonCode);

internal static class SignupNudgeGovernor
{
    internal static SignupNudgeDecision Decide(
        SignupNudgeGovernorInput input,
        SignupNudgeGovernorSettings? settings = null)
    {
        SignupNudgeGovernorSettings s = settings ?? DefaultSettings();

        // Never interrupt when paused or without consent (no data is being captured anyway).
        if (input.Paused)
        {
            return No("paused");
        }

        if (!input.ConsentActive)
        {
            return No("consent_inactive");
        }

        // The whole point is to convert UNLINKED devices — a linked device never sees this.
        if (input.IsLinked)
        {
            return No("already_linked");
        }

        // Don't ask before there's something worth saving.
        if (input.DaysOfData < Math.Max(0, s.MinDaysOfData))
        {
            return No("insufficient_data");
        }

        // Soft lifetime cap: after a few tries, take the hint and stop for good.
        if (input.NudgesShownTotal >= Math.Max(0, s.MaxLifetimeNudges))
        {
            return No("lifetime_cap_reached");
        }

        // Weekly cap: at most one nudge per interval.
        if (input.LastNudgeAtUtc is DateTimeOffset last
            && (input.NowUtc - last).TotalDays < s.MinDaysBetweenNudges)
        {
            return No("weekly_cap_not_elapsed");
        }

        return new SignupNudgeDecision(ShouldNudge: true, ReasonCode: "nudge_due");
    }

    private static SignupNudgeGovernorSettings DefaultSettings() => new(
        MinDaysOfData: PortaExecutionConstants.SignupNudge.MinDaysOfData,
        MinDaysBetweenNudges: PortaExecutionConstants.SignupNudge.MinDaysBetweenNudges,
        MaxLifetimeNudges: PortaExecutionConstants.SignupNudge.MaxLifetimeNudges);

    private static SignupNudgeDecision No(string reasonCode) =>
        new(ShouldNudge: false, ReasonCode: reasonCode);
}
