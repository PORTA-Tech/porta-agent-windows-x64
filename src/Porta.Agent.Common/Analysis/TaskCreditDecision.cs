namespace Porta.Agent.Common.Analysis;

/// <summary>
/// What this bucket's seconds are, for the task that happens to be active.
/// </summary>
public enum TaskCredit
{
    /// <summary>Work on this task. Lands on active_seconds, task_day_hours and the rollup as today.</summary>
    Credit,

    /// <summary>
    /// Work, but not this task's work. The seconds are NEITHER credited NOR discarded: they are
    /// written to pending_task_seconds and surface as unassigned until something resolves them.
    /// </summary>
    Hold,

    /// <summary>Not work at all. offtask_seconds, exactly as before this change.</summary>
    OffTask,

    /// <summary>Away from the machine. Nothing is written anywhere, exactly as before.</summary>
    None
}

/// <summary>
/// What, if anything, this bucket teaches the task's footprint.
/// </summary>
public enum TaskLearning
{
    /// <summary>Nothing to learn: not credited, or no usable hash.</summary>
    None,

    /// <summary>
    /// Learn it as a member immediately. Either the task is inside its first 15 minutes of
    /// credited work, or its footprint is still empty and we do not yet know what it looks like.
    /// </summary>
    Member,

    /// <summary>The hash is already a member; bump its credited seconds so the 24-cap can rank it.</summary>
    Reinforce,

    /// <summary>
    /// Credited only because the drift streak has not reached the threshold yet. Accumulate the
    /// seconds, but WITHHOLD membership until the streak ends below the threshold — see the class
    /// comment. This is the difference between "you came back" and "you never did".
    /// </summary>
    Provisional
}

/// <summary>
/// The one place the task-relevance rule exists. Pure, Windows-agnostic, no SQLite, in the same
/// spirit as <see cref="BrowserDomainResolver"/> — so the rule can be argued about in a unit test
/// instead of inside a write transaction.
///
/// <b>The problem.</b> Until now the timer answered "was he working". It never asked "was he
/// working on THIS". An afternoon spent on something else landed on whichever task happened to be
/// marked active, and that number is the one under the client's report.
///
/// <b>The signal.</b> Not page content, not an LLM, not a comparison of a task title to what is on
/// screen. Every task already carries a record of which applications its credited hours were spent
/// in. The question is not "does this page mean what the title says" — it is "does this look like
/// the same kind of work this task has been". That is a set-membership test on a hash, and it needs
/// no understanding of anything.
///
/// Category decides what work looks like in general; footprint decides what work looks like FOR
/// THIS TASK. Where they disagree, footprint wins, because it is evidence from the user's own
/// behaviour rather than a guess from a lookup table.
///
/// <b>THE ORDER OF THE TESTS IS THE DESIGN.</b> The category gate runs FIRST and is untouched, so
/// a film in front of an open task can never reach the affinity test and can never be promoted
/// into credited time by a footprint. The film bug is not being reopened from the other side.
///
/// <b>Everything here fails OPEN.</b> Credit is the default and Hold is the exception, in every
/// ambiguous case: no footprint yet, an unreadable window, a short excursion. Silently not counting
/// real work is worse than visibly counting too much, because the user cannot see it happen.
///
/// <b>Why learning is withheld during a drift streak (<see cref="TaskLearning.Provisional"/>).</b>
/// A hash joins the footprint after the learning window once it has 2 minutes of credited time, and
/// the gate fires after 10 continuous minutes outside the footprint. Those two numbers are 2
/// minutes and 10 minutes: taken naively, every out-of-footprint stretch would join the footprint
/// at minute 2 and the gate could never reach minute 10 — the feature would be a no-op that passes
/// its own tests. So the seconds accumulate while drifting, but membership is only granted when the
/// streak ENDS below the threshold. You learn an application when you come back from it. Coming
/// back is the evidence that the excursion belonged to the task; not coming back is the evidence
/// that it did not.
/// </summary>
public static class TaskCreditDecision
{
    /// <summary>
    /// 10 continuous minutes outside the footprint before a single second is held — 120 buckets at
    /// <c>Time.bucketSeconds = 5</c>. Below it everything is credited normally: a two-minute lookup
    /// in the browser must not break a task.
    /// </summary>
    public const int DefaultDriftThresholdBuckets = 120;

    /// <summary>
    /// A task's first 15 minutes of CREDITED work. Every foreground hash seen in it is learned.
    /// This window is load-bearing, not a nicety: apply the gate to a brand-new task with an empty
    /// footprint and every hash is "outside", every task drifts at minute 10, and every task on the
    /// machine reads zero forever.
    /// </summary>
    public const double LearningWindowSeconds = 15d * 60d;

    /// <summary>
    /// After the learning window a hash needs 2 minutes of credited time before it joins the
    /// footprint. Together with <see cref="MaxAffinityHashesPerTask"/> this is what stops a
    /// week-old task accumulating a footprint containing most of the machine — at which point the
    /// gate silently stops firing and nobody notices.
    /// </summary>
    public const double LaterLearningMinimumSeconds = 120d;

    /// <summary>
    /// 24 hashes per task, keeping the highest credited seconds. Browsers produce one hash per
    /// site, so an uncapped footprint grows wide enough never to trip.
    /// </summary>
    public const int MaxAffinityHashesPerTask = 24;

    /// <summary>Saturation ceiling for the streak so a long hold cannot overflow the counter.</summary>
    public const int MaxDriftStreakBuckets = 1_000_000;

    private static readonly IReadOnlySet<string> NoAffinity =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The whole rule, in order. Every writer calls this and none of them re-derives it; two
    /// writers with two copies of this predicate is how a report ends up showing two different
    /// totals for the same work.
    /// </summary>
    public static TaskCredit Decide(
        string? attentionSegmentKind,
        string? dominantAppHash,
        IReadOnlySet<string> affinity,
        bool withinLearningWindow,
        int consecutiveDriftBuckets,
        int driftThresholdBuckets)
    {
        // 1. Away is not work and never was. Unchanged behaviour.
        if (AttentionSegmentKinds.IsAway(attentionSegmentKind))
        {
            return TaskCredit.None;
        }

        // 2. The category gate, untouched and FIRST. A film, a game, a social loop or an
        //    unclassifiable bucket is off-task no matter what any footprint says. Nothing below
        //    this line can promote a non-working kind into credited time.
        if (!AttentionSegmentKinds.IsWorkingKind(attentionSegmentKind))
        {
            return TaskCredit.OffTask;
        }

        // 3. Cold start. We do not yet know what this task looks like, so we are in no position to
        //    say this is not it.
        if (withinLearningWindow)
        {
            return TaskCredit.Credit;
        }

        // 4. An unreadable window is an ABSENCE of evidence, not evidence of drift. Fail open:
        //    failing closed here would bleed time out of every task on every unrecognised window.
        if (string.IsNullOrWhiteSpace(dominantAppHash))
        {
            return TaskCredit.Credit;
        }

        // 5. Never hold on an empty footprint, window or no window. A task that has somehow
        //    reached the end of its learning window without learning anything has told us nothing,
        //    and holding on nothing is how a user's whole day disappears.
        if (affinity is null || affinity.Count == 0)
        {
            return TaskCredit.Credit;
        }

        // 6. This is what the task has looked like. Credit it.
        if (affinity.Contains(dominantAppHash))
        {
            return TaskCredit.Credit;
        }

        // 7. Outside the footprint, but not for long enough to mean anything yet.
        if (consecutiveDriftBuckets < driftThresholdBuckets)
        {
            return TaskCredit.Credit;
        }

        // 8. Ten continuous minutes of real work that does not look like this task's work.
        return TaskCredit.Hold;
    }

    /// <summary>
    /// The streak after this bucket. Running state, not a scan: buckets are 5 seconds, so this is
    /// evaluated 720 times an hour and must not touch the database.
    ///
    /// An empty hash is NEUTRAL — it neither advances nor resets the streak. It is not evidence of
    /// drift, so it must not accumulate one; but it is not evidence of presence either, so a
    /// genuine drift stretch interrupted by one unreadable window must still be able to reach the
    /// threshold.
    ///
    /// The caller resets this to zero on the events this function cannot see: a task switch, a task
    /// auto-close, and a change to the affinity SET.
    /// </summary>
    public static int NextDriftStreak(
        int currentStreak,
        string? attentionSegmentKind,
        string? dominantAppHash,
        IReadOnlySet<string> affinity,
        bool withinLearningWindow)
    {
        if (AttentionSegmentKinds.IsAway(attentionSegmentKind)
            || !AttentionSegmentKinds.IsWorkingKind(attentionSegmentKind)
            || withinLearningWindow)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(dominantAppHash))
        {
            return Math.Max(0, currentStreak);
        }

        if (affinity is null || affinity.Count == 0 || affinity.Contains(dominantAppHash))
        {
            return 0;
        }

        return Math.Min(MaxDriftStreakBuckets, Math.Max(0, currentStreak) + 1);
    }

    /// <summary>
    /// What this bucket teaches the footprint. Learning happens on <see cref="TaskCredit.Credit"/>
    /// and nowhere else — a held bucket teaches nothing, which is the circular starvation the plan
    /// names and deliberately does not paper over. The exit from it is the 45-minute auto-close in
    /// this chat, and the user confirming in the next one.
    /// </summary>
    public static TaskLearning Learning(
        TaskCredit credit,
        string? dominantAppHash,
        IReadOnlySet<string> affinity,
        bool withinLearningWindow)
    {
        if (credit != TaskCredit.Credit || string.IsNullOrWhiteSpace(dominantAppHash))
        {
            return TaskLearning.None;
        }

        if (affinity is not null && affinity.Contains(dominantAppHash))
        {
            return TaskLearning.Reinforce;
        }

        // Inside the window, or with nothing learned at all yet, everything seen is the task.
        if (withinLearningWindow || affinity is null || affinity.Count == 0)
        {
            return TaskLearning.Member;
        }

        // Outside the window, outside the footprint, credited only because the streak is short.
        return TaskLearning.Provisional;
    }

    /// <summary>
    /// The pre-affinity behaviour, expressed through the SAME predicate rather than beside it.
    /// Used by the compatibility overloads of the three writers, so no caller anywhere re-derives
    /// "working" from the kind on its own.
    /// </summary>
    public static TaskCredit DecideWithoutAffinity(string? attentionSegmentKind)
        => Decide(
            attentionSegmentKind,
            dominantAppHash: null,
            affinity: NoAffinity,
            withinLearningWindow: true,
            consecutiveDriftBuckets: 0,
            driftThresholdBuckets: DefaultDriftThresholdBuckets);

    /// <summary>True when these seconds count as work on the task. One place, one answer.</summary>
    public static bool IsCredited(TaskCredit credit) => credit == TaskCredit.Credit;

    /// <summary>True when these seconds are recorded but not called work.</summary>
    public static bool IsOffTask(TaskCredit credit) => credit == TaskCredit.OffTask;
}
