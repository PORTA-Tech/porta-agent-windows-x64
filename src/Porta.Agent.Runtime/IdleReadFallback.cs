namespace Porta.Agent.Runtime;

/// <summary>
/// Chat 3 (2026-08-24) — what "how long has the user been idle" means when Windows refuses to say.
///
/// Both idle readers returned <c>0</c> when <c>GetLastInputInfo</c> failed, and zero seconds idle is
/// indistinguishable from "the user just typed". So a broken idle channel did not read as broken —
/// it read as continuous activity, and every bucket it touched was credited as work. That is the
/// worst possible failure direction for a number a client's report is built on.
///
/// The rule here is fail CLOSED. A reading nobody could take is not evidence of presence:
///
///   * With a previous good reading, idle is that reading plus the time since it was taken. If the
///     user was 30 seconds idle a minute ago and nothing has been measurable since, they have been
///     idle at least 90 seconds. That is the honest continuation, and it keeps growing for as long
///     as the channel stays broken rather than resetting the clock every failed poll.
///   * With no good reading at all — the channel was broken from the start — assume idle. Assuming
///     ACTIVE would credit an entire session to a task on the strength of a failed API call.
///
/// Pure and separate from both hosts so the rule is stated once and unit-tested, rather than
/// duplicated in two files that then drift.
/// </summary>
internal static class IdleReadFallback
{
    /// <summary>
    /// What to assume when the idle channel has never produced a reading. Long enough that the
    /// pipeline treats it as away rather than as work, which is the point: unknown is not presence.
    /// </summary>
    internal const double AssumeIdleSeconds = 300d;

    /// <summary>
    /// Resolves an idle reading. <paramref name="readSucceeded"/> is the native call's own result;
    /// everything else is the caller's remembered state.
    /// </summary>
    internal static double Resolve(
        bool readSucceeded,
        double rawSeconds,
        double? lastGoodSeconds,
        TimeSpan sinceLastGood)
    {
        if (readSucceeded)
        {
            return Math.Max(0d, rawSeconds);
        }

        if (lastGoodSeconds is { } last)
        {
            return Math.Max(0d, last) + Math.Max(0d, sinceLastGood.TotalSeconds);
        }

        return AssumeIdleSeconds;
    }
}
