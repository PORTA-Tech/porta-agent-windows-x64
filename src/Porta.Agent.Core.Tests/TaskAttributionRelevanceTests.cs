using Microsoft.Data.Sqlite;
using Porta.Agent.Common.Analysis;
using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Task attribution (2026-09-01) — credit only the work that belongs to the task.
///
/// Two halves. The first is the rule itself, exercised with no database at all, because a rule you
/// can only test through a write transaction is a rule nobody will change safely. The second
/// replays whole synthetic afternoons through the REAL writers in the real order, because the thing
/// that actually matters is that the three accumulators agree — <c>tasks.active_seconds</c>,
/// <c>task_day_hours</c> and <c>app_usage_rollup</c> are the same measurement at three grains and
/// the client's report reads all three.
///
/// The inverse bug is the one to watch for here. Over-crediting is visible: the user sees a number
/// that is too big and says so. Under-crediting is silent — Ari simply stops counting real work and
/// nobody finds out until a client is invoiced for half a week. So nearly every test below is
/// checking that something is still CREDITED.
/// </summary>
public sealed class TaskAttributionRelevanceTests
{
    private const int Threshold = TaskCreditDecision.DefaultDriftThresholdBuckets;

    private static IReadOnlySet<string> Affinity(params string[] hashes)
        => new HashSet<string>(hashes, StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> Empty = Affinity();

    // ── the rule, with no database ────────────────────────────────────────────────────

    [Fact]
    public void Away_IsNothing_WhateverTheFootprintSays()
    {
        Assert.Equal(
            TaskCredit.None,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Away, "code", Affinity("code"), false, 0, Threshold));

        // ...and equally when the hash is a stranger. Away is not an application.
        Assert.Equal(
            TaskCredit.None,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Away, "stranger", Affinity("code"), false, 999, Threshold));
    }

    /// <summary>
    /// The category gate runs FIRST and the affinity test can never reach a non-working kind. This
    /// is the guard against reopening the film bug from the other side: a footprint must not be
    /// able to promote passive consumption into credited time.
    /// </summary>
    [Theory]
    [InlineData(AttentionSegmentKinds.PassiveConsumption)]
    [InlineData(AttentionSegmentKinds.SocialMediaLoop)]
    [InlineData(AttentionSegmentKinds.ActiveGamingDistraction)]
    [InlineData(AttentionSegmentKinds.Unknown)]
    public void ANonWorkingKind_IsOffTask_WhateverTheFootprintSays(string kind)
    {
        // In the footprint, out of the footprint, inside the window, deep into a drift streak:
        // the answer is the same every time.
        Assert.Equal(
            TaskCredit.OffTask,
            TaskCreditDecision.Decide(kind, "youtube", Affinity("youtube"), false, 0, Threshold));
        Assert.Equal(
            TaskCredit.OffTask,
            TaskCreditDecision.Decide(kind, "youtube", Affinity("code"), true, 0, Threshold));
        Assert.Equal(
            TaskCredit.OffTask,
            TaskCreditDecision.Decide(kind, "youtube", Affinity("code"), false, 10_000, Threshold));
    }

    [Fact]
    public void InsideTheLearningWindow_WithNoFootprintAtAll_IsCredited()
    {
        // Cold start. Apply the gate here and every new task reads zero forever.
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "anything", Empty, true, 0, Threshold));

        // And a brand-new task learns what it sees, rather than provisionally hoarding it.
        Assert.Equal(
            TaskLearning.Member,
            TaskCreditDecision.Learning(TaskCredit.Credit, "anything", Empty, true));
    }

    [Fact]
    public void AnEmptyFootprint_NeverHolds_EvenPastTheLearningWindow()
    {
        // "Never hold on an empty footprint" is unconditional. A task that somehow reached the end
        // of its window without learning anything has told us nothing, and holding on nothing is
        // how a user's whole day disappears.
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "anything", Empty, false, 10_000, Threshold));
    }

    [Fact]
    public void AHashInTheFootprint_IsCredited()
    {
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "code", Affinity("code", "github"), false, 0, Threshold));

        // Even after a long drift: matching the footprint is what ENDS a drift, not what survives one.
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "code", Affinity("code"), false, 10_000, Threshold));
    }

    /// <summary>The threshold itself: 119 buckets is a lookup, 120 is a different afternoon.</summary>
    [Fact]
    public void OutsideTheFootprint_119BucketsIsCredited_120IsHeld()
    {
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "stranger", Affinity("code"), false, 119, Threshold));

        Assert.Equal(
            TaskCredit.Hold,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, "stranger", Affinity("code"), false, 120, Threshold));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnresolvableHash_IsAlwaysCredited_AndNeverHeld(string? hash)
    {
        // Fail OPEN. An unreadable window is an absence of evidence, not evidence of drift; failing
        // closed here would bleed time out of every task on every unrecognised window.
        Assert.Equal(
            TaskCredit.Credit,
            TaskCreditDecision.Decide(
                AttentionSegmentKinds.Focus, hash, Affinity("code"), false, 10_000, Threshold));
    }

    [Fact]
    public void AnUnresolvableHash_IsNeutralForTheStreak_NeitherAdvancingNorResettingIt()
    {
        IReadOnlySet<string> affinity = Affinity("code");

        // It must not manufacture a drift...
        Assert.Equal(
            50,
            TaskCreditDecision.NextDriftStreak(
                50, AttentionSegmentKinds.Focus, "", affinity, false));

        // ...and it must not wipe one out either, or a single unreadable window inside a long
        // stretch of somebody else's work would restart the clock and the gate would never fire.
        Assert.Equal(
            0,
            TaskCreditDecision.NextDriftStreak(
                0, AttentionSegmentKinds.Focus, null, affinity, false));
    }

    [Fact]
    public void TheDriftStreak_ResetsOnAnInFootprintBucket_AndOnANonWorkingKind()
    {
        IReadOnlySet<string> affinity = Affinity("code");

        Assert.Equal(
            0,
            TaskCreditDecision.NextDriftStreak(
                90, AttentionSegmentKinds.Focus, "code", affinity, false));

        Assert.Equal(
            0,
            TaskCreditDecision.NextDriftStreak(
                90, AttentionSegmentKinds.PassiveConsumption, "stranger", affinity, false));

        Assert.Equal(
            0,
            TaskCreditDecision.NextDriftStreak(
                90, AttentionSegmentKinds.Away, "stranger", affinity, false));

        // Inside the learning window there is no such thing as drift yet.
        Assert.Equal(
            0,
            TaskCreditDecision.NextDriftStreak(
                90, AttentionSegmentKinds.Focus, "stranger", affinity, true));

        // ...and it advances only in the one case it is meant to.
        Assert.Equal(
            91,
            TaskCreditDecision.NextDriftStreak(
                90, AttentionSegmentKinds.Focus, "stranger", affinity, false));
    }

    /// <summary>
    /// The streak is per-task running state, so a switch has to zero it. Proven through the store
    /// rather than the pure function, because the switch is exactly the event the pure function
    /// cannot see.
    /// </summary>
    [Fact]
    public void TheDriftStreak_ResetsOnATaskSwitch()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask a = Start(store, dbPath, "task A", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            // Drift most of the way to the threshold on A, but not over it.
            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "stranger", Threshold - 5);
            Assert.Equal(0, HeldRowCount(dbPath));

            // Switch to B and back to A. The streak is running state about "how long has this task
            // been away from its own work", and starting a different task ends that sentence.
            AgentTask b = Start(store, dbPath, "task B", t);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "stranger", 3);
            store.ActOnTask(a.Id, "start", t);

            // Ten more buckets on A. If the switch had not reset the streak this would be over the
            // threshold and holding; it must not be.
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "stranger", 10);
            Assert.Equal(0, HeldRowCountForTask(dbPath, a.Id));
            Assert.True(b.Id > 0);
        });
    }

    // ── whole afternoons, through the real writers ────────────────────────────────────

    /// <summary>
    /// THE HEADLINE CASE. Task A is running. The user works for fifteen minutes in the application
    /// A has always been worked in, then spends an hour and a half of genuine, heads-down,
    /// every-bucket-is-focus work in something else entirely.
    ///
    /// Before this change every one of those 90 minutes landed on A, and A's number went under a
    /// client's invoice. Now A gains the ten minutes the rule allows before it will call anything
    /// drift, and not one second more.
    /// </summary>
    [Fact]
    public void NinetyMinutesOfSomebodyElsesWork_CreditsOnlyTheFirstTenMinutes()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            AgentTask afterLearning = Read(store);
            Assert.Equal(900d, afterLearning.ActiveSeconds, precision: 6);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", 90 * 60 / BucketSecondsInt);

            AgentTask after = Read(store);

            // 15 minutes of learning + exactly 10 minutes of allowance. Nothing else.
            Assert.Equal(900d + 600d, after.ActiveSeconds, precision: 6);

            // Not one second of it was called a distraction. It was real work; it simply was not
            // this task's work, and mislabelling it would be its own kind of dishonesty.
            Assert.Equal(0d, after.OfftaskSeconds, precision: 6);

            // The two grains agree, exactly. This is the one assertion that must never be relaxed.
            Assert.Equal(after.ActiveSeconds, SumTaskDayHours(dbPath, task.Id), precision: 6);

            // And the rollup — which is what the client's report is built from — agrees too.
            Assert.Equal(after.ActiveSeconds, SumRollupActiveForTask(dbPath, task.Id), precision: 6);

            // The held stretch is in the rollup against task 0, never against the task. If this id
            // were left in place the Reports page would report the exact hours this change removes,
            // while the Tasks page showed the corrected number — two totals for the same work.
            //
            // Task 0 carries MORE than the held seconds, and that is right: once the task
            // auto-closes there is no active task at all, and the rest of the afternoon is work on
            // nobody's task, which is where untagged time has always gone.
            Assert.True(HeldSeconds(dbPath) > 0d);
            Assert.True(
                SumRollupActiveForTask(dbPath, 0) >= HeldSeconds(dbPath),
                "every held second must be in the rollup against task 0");
            Assert.Equal(
                after.ActiveSeconds,
                SumRollupActiveWhereTaskAssigned(dbPath),
                precision: 6);

            // Held, not discarded: the seconds are on the record against the task they were
            // measured beside, waiting for somebody to say where they belong.
            Assert.Equal(HeldSeconds(dbPath), store.ReadUnassignedSecondsForDay(GoalDayOf(DayStart)), precision: 6);
        });
    }

    /// <summary>
    /// The auto-close was built for idleness and for a film in front of an open task, and it did
    /// that job correctly. It could never catch this case: heads-down work on something else is
    /// every-bucket focus, which credited the wrong task AND refreshed the stale timer in the same
    /// statement. The net was disarmed by exactly the behaviour it needed to catch.
    ///
    /// Because Hold no longer stamps last_credited_at_utc, it is armed. The task closes 45 minutes
    /// after the last GENUINELY credited bucket — not 45 minutes after now.
    /// </summary>
    [Fact]
    public void AfterDrifting_TheTaskAutoCloses_AtTheLastCreditedBucket_NotAtNow()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", 90 * 60 / BucketSecondsInt);

            AgentTask after = Read(store);
            Assert.Equal("pending", after.State);

            // The last credited bucket is the 120th of the drift stretch: 15 minutes of learning
            // plus 119 further buckets. The close is stamped THERE.
            DateTimeOffset lastCredited = DayStart.AddSeconds((LearningBuckets + Threshold - 1) * BucketSeconds);
            Assert.Equal(
                lastCredited.ToUniversalTime().ToString("O"),
                ReadColumn(dbPath, task.Id, "last_credited_at_utc"));
            Assert.Equal(
                lastCredited.ToUniversalTime().ToString("O"),
                ReadColumn(dbPath, task.Id, "auto_closed_at_utc"));

            // 45 minutes of holding, and then the task is no longer active so nothing more is held.
            // Anything after the close belongs to no task at all, which is honest and visible.
            double expectedHeld = (SqliteBrainStateStore.StaleActiveTaskSeconds + BucketSeconds) - 0d;
            Assert.Equal(expectedHeld, HeldSeconds(dbPath), precision: 6);
        });
    }

    /// <summary>
    /// Regression guard on Chat 3, from the other direction. A film is not "work on another task",
    /// it is not work, and it must go where it has always gone. Holding it would quietly turn a
    /// solved problem into an open question the user gets asked about.
    /// </summary>
    [Fact]
    public void NinetyMinutesOfYouTube_IsStillEntirelyOffTask_AndHoldsNothing()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.PassiveConsumption, "youtube", 90 * 60 / BucketSecondsInt);

            AgentTask after = Read(store);

            // Still only the learning window's work, exactly as Chat 3 left it.
            Assert.Equal(900d, after.ActiveSeconds, precision: 6);
            Assert.True(after.OfftaskSeconds > 0d);

            // Nothing held. The film is not an open question.
            Assert.Equal(0, HeldRowCount(dbPath));
            Assert.Equal(0d, store.ReadUnassignedSecondsForDay(GoalDayOf(DayStart)), precision: 6);

            // And it closed on the stale timer, not on the drift gate.
            Assert.Equal("pending", after.State);
            Assert.True(task.Id > 0);
        });
    }

    /// <summary>
    /// A two-minute lookup in the browser must not break a task, and — because the user came back —
    /// it JOINS the task's footprint rather than being forgotten. This is the whole reason
    /// provisional learning exists: the 2-minute learning minimum sits below the 10-minute drift
    /// threshold, so learning during the excursion has to wait until the excursion ends.
    /// </summary>
    [Fact]
    public void AShortExcursionIsCredited_AndJoinsTheFootprintOnlyOnceTheUserComesBack()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);

            // Three minutes in the docs. Below the threshold, so credited...
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "docs", 3 * 60 / BucketSecondsInt);
            Assert.Equal(0, HeldRowCount(dbPath));

            // ...but NOT yet a member of the footprint, or the gate could never reach ten minutes.
            Assert.False(IsFootprintMember(dbPath, task.Id, "docs"));

            // Back to work. Coming back is the evidence that the excursion belonged to this task.
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "code", 1);
            Assert.True(IsFootprintMember(dbPath, task.Id, "docs"));

            // So a later hour in the docs is now this task's work and is credited in full.
            double before = Read(store).ActiveSeconds;
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "docs", 30 * 60 / BucketSecondsInt);
            Assert.Equal(before + 30d * 60d, Read(store).ActiveSeconds, precision: 6);
            Assert.Equal(0, HeldRowCount(dbPath));
        });
    }

    /// <summary>
    /// The other half of bug 10. The 2-minute minimum is what stops a week-old task accumulating a
    /// footprint containing most of the machine: a glance at something is not adoption of it, and
    /// a footprint wide enough to match everything is a gate that never fires.
    /// </summary>
    [Fact]
    public void AGlanceUnderTwoMinutes_IsCredited_ButNeverJoinsTheFootprint()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);

            // Ninety seconds — a glance. Credited, because a short lookup must not break a task.
            double before = Read(store).ActiveSeconds;
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "a-glance", 90 / BucketSecondsInt);
            Assert.Equal(before + 90d, Read(store).ActiveSeconds, precision: 6);

            // Back to work: the streak ends below the threshold, so provisional rows are settled.
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "code", 1);

            // ...and a glance did not earn membership. Nothing of it is left at all.
            Assert.False(IsFootprintMember(dbPath, task.Id, "a-glance"));
            Assert.Equal(0, ProvisionalRowCount(dbPath, task.Id));
        });
    }

    /// <summary>
    /// The other half of the same mechanism. A stretch the user never comes back from must NOT
    /// join the footprint, or the very application the gate just fired on would be adopted the
    /// moment they returned and the gate would never fire on it again.
    /// </summary>
    [Fact]
    public void AStretchTheUserNeverComesBackFrom_DoesNotJoinTheFootprint()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 20);

            Assert.False(IsFootprintMember(dbPath, task.Id, "elsewhere"));
            Assert.Equal(0, ProvisionalRowCount(dbPath, task.Id));
            Assert.True(HeldSeconds(dbPath) > 0d);
        });
    }

    /// <summary>
    /// A release replays held buckets through the same three writers AT THEIR ORIGINAL TIMESTAMPS.
    /// A hold spanning the 05:00 goal-day boundary therefore lands on the two correct days, never
    /// both on today — the same mistake AutoCloseStaleActiveTask documents in its own summary.
    /// </summary>
    [Fact]
    public void Release_ReplaysAtTheOriginalTimestamps_AcrossTheGoalDayBoundary()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "night shift", BeforeBoundary);
            Learn(store, dbPath, BeforeBoundary, "code", LearningBuckets);

            // Straight into somebody else's work, running well through 05:00 local: 04:15 to 05:35,
            // of which 04:15–04:25 is the ten-minute allowance and 04:25 onwards is held until the
            // task auto-closes at 05:10. So the hold genuinely straddles the boundary.
            DateTimeOffset t = BeforeBoundary.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", 80 * 60 / BucketSecondsInt);

            string dayBefore = GoalDayOf(BeforeBoundary);
            string dayAfter = GoalDayOf(BeforeBoundary.AddHours(2));
            Assert.NotEqual(dayBefore, dayAfter);

            double heldBefore = store.ReadUnassignedSecondsForDay(dayBefore);
            double heldAfter = store.ReadUnassignedSecondsForDay(dayAfter);
            Assert.True(heldBefore > 0d, "the hold must start before the 05:00 boundary");
            Assert.True(heldAfter > 0d, "and continue after it");

            double dayBeforeHoursBefore = SumTaskDayHoursForDay(dbPath, task.Id, dayBefore);
            double dayAfterHoursBefore = SumTaskDayHoursForDay(dbPath, task.Id, dayAfter);

            double released = store.ReleaseHeldSecondsToTask(task.Id, task.Id);
            Assert.Equal(heldBefore + heldAfter, released, precision: 6);

            // Each day gained exactly its own held seconds. Replaying at "now" would have put the
            // whole lot on one of them.
            Assert.Equal(
                dayBeforeHoursBefore + heldBefore,
                SumTaskDayHoursForDay(dbPath, task.Id, dayBefore),
                precision: 6);
            Assert.Equal(
                dayAfterHoursBefore + heldAfter,
                SumTaskDayHoursForDay(dbPath, task.Id, dayAfter),
                precision: 6);

            // And the released seconds are no longer unassigned, because they now sit on a task.
            Assert.Equal(0d, store.ReadUnassignedSecondsForDay(dayBefore), precision: 6);
            Assert.Equal(0d, store.ReadUnassignedSecondsForDay(dayAfter), precision: 6);
        });
    }

    /// <summary>
    /// The bucket finalizer has a write guard and buckets can be replayed. A replayed bucket must
    /// neither double-hold nor double-credit, and a second release must credit nothing at all.
    /// </summary>
    [Fact]
    public void HoldAndRelease_AreBothIdempotent()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 60);

            double heldOnce = HeldSeconds(dbPath);
            int rowsOnce = HeldRowCount(dbPath);
            Assert.Equal(60, rowsOnce);
            Assert.True(heldOnce > 0d);

            // Replay the held stretch — the same buckets, at the same timestamps. The primary key
            // (task_id, bucket_start_utc) makes the hold idempotent, so nothing is held twice and
            // nothing is credited twice.
            double creditedBeforeReplay = Read(store).ActiveSeconds;
            DateTimeOffset replay = DayStart.AddSeconds((LearningBuckets + Threshold) * BucketSeconds);
            Run(store, dbPath, ref replay, AttentionSegmentKinds.Focus, "elsewhere", 60);
            Assert.Equal(rowsOnce, HeldRowCount(dbPath));
            Assert.Equal(heldOnce, HeldSeconds(dbPath), precision: 6);
            Assert.Equal(creditedBeforeReplay, Read(store).ActiveSeconds, precision: 6);

            double creditedBefore = Read(store).ActiveSeconds;
            double first = store.ReleaseHeldSecondsToTask(task.Id, task.Id);
            Assert.Equal(heldOnce, first, precision: 6);
            Assert.Equal(creditedBefore + heldOnce, Read(store).ActiveSeconds, precision: 6);

            // The state flip happens in the same transaction as the replay, so a second release
            // finds no held rows and credits nothing.
            double second = store.ReleaseHeldSecondsToTask(task.Id, task.Id);
            Assert.Equal(0d, second, precision: 6);
            Assert.Equal(creditedBefore + heldOnce, Read(store).ActiveSeconds, precision: 6);
        });
    }

    /// <summary>
    /// A sent report is a permanent snapshot behind a client link. Held time covered by one may
    /// only ever resolve to unassigned — reattributing it would change history a client has already
    /// read. Amending a sent report is a separate decision and is not this code's to make.
    /// </summary>
    [Fact]
    public void HeldTimeCoveredByASentReport_CanOnlyEverBecomeUnassigned()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 60);

            double held = HeldSeconds(dbPath);
            Assert.True(held > 0d);

            // A report covering this day has already gone to a client.
            InsertSentReport(dbPath, GoalDayOf(DayStart));

            double creditedBefore = Read(store).ActiveSeconds;
            double released = store.ReleaseHeldSecondsToTask(task.Id, task.Id);

            Assert.Equal(0d, released, precision: 6);
            Assert.Equal(creditedBefore, Read(store).ActiveSeconds, precision: 6);

            // Not lost, though — still visible as unassigned, which is the honest answer.
            Assert.Equal(held, store.ReadUnassignedSecondsForDay(GoalDayOf(DayStart)), precision: 6);
            Assert.Equal(0, HeldRowCountInState(dbPath, "held"));
            Assert.Equal(held, SumPendingInState(dbPath, "unassigned"), precision: 6);
        });
    }

    /// <summary>
    /// Browsers produce one hash per site, so an uncapped footprint grows wide enough never to
    /// trip. The cap evicts the lowest learned row and never touches a confirmed one — the user
    /// said that application is this task's work, and a background counter does not overrule them.
    /// </summary>
    [Fact]
    public void TheFootprintCap_EvictsTheLowestLearnedRow_AndNeverAConfirmedOne()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "wide task", DayStart);

            // 27 sites, all seen INSIDE the learning window so every one is learned as a member —
            // which is exactly the browser case the cap exists for. site-00 gets a single bucket
            // and every other site gets six, so there is an unambiguous least-used row.
            //
            // 157 buckets at 5 seconds is 785 seconds, comfortably inside the 15-minute window.
            DateTimeOffset t = DayStart;
            store.ConfirmTaskAppAffinity(task.Id, "site-confirmed", DayStart);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "site-00", 1);
            for (int i = 1; i < 27; i++)
            {
                Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, $"site-{i:00}", 6);
            }

            Assert.Equal(
                TaskCreditDecision.MaxAffinityHashesPerTask,
                FootprintMemberCount(dbPath, task.Id));

            // The confirmed row survives despite having no credited seconds at all — it is the
            // lowest by every measure the cap ranks on, and it is still there. The user said this
            // application is this task's work, and a background counter does not overrule them.
            Assert.True(IsFootprintMember(dbPath, task.Id, "site-confirmed"));
            Assert.Equal("confirmed", ReadAffinitySource(dbPath, task.Id, "site-confirmed"));

            // ...and the least-used learned row is the one that went.
            Assert.False(IsFootprintMember(dbPath, task.Id, "site-00"));
        });
    }

    /// <summary>
    /// app_hash is HMAC-SHA256(installSecret, identifier). A reinstall or a rotated secret changes
    /// EVERY hash on the machine at once, so every task would drift simultaneously and the user's
    /// whole day would be held. That is the loudest possible version of the inverse bug, so the
    /// condition is detected and the footprints are cleared rather than trusted.
    /// </summary>
    [Fact]
    public void WhenEveryHashOnTheMachineChangesAtOnce_TheFootprintIsClearedRatherThanHoldingTheDay()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);
            Assert.True(IsFootprintMember(dbPath, task.Id, "code"));

            // A rotated install secret, as the agent would actually meet it: the stored epoch no
            // longer matches what the hashing produces, and it is a NEW PROCESS that notices,
            // because the secret is resolved once per process and cannot change under a running one.
            SetRuntimeState(dbPath, SqliteBrainStateStore.AffinityEpochRuntimeStateKey, "a-secret-that-is-gone");
            var afterRestart = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(afterRestart, dbPath, ref t, AttentionSegmentKinds.Focus, "new-hash-for-code", Threshold + 60);

            // Nothing held. The old footprint was addressed to a secret that no longer exists, so it
            // was cleared and the task fell back to its learning window and re-learned the machine
            // under the new one. That is what failing OPEN means here: every task on the machine
            // would otherwise drift at once and the user's whole day would be held.
            Assert.Equal(0, HeldRowCount(dbPath));
            Assert.True(IsFootprintMember(dbPath, task.Id, "new-hash-for-code"));
            Assert.False(IsFootprintMember(dbPath, task.Id, "code"));
        });
    }

    /// <summary>
    /// context_excursion is a working kind by design, so short hops are credited. It must be judged
    /// on its OWN dominant hash: the bucket transaction passes InputBucketRow.DominantAppHash, not
    /// the continuity anchor's, or a long excursion into anything at all would keep passing the
    /// affinity test because the anchor still matched.
    /// </summary>
    [Fact]
    public void AContextExcursion_IsJudgedOnItsOwnApp_NotTheAnchors()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.ContextExcursion, "elsewhere", Threshold + 60);

            // A working kind, and still held once it ran long enough — because the hash it carried
            // was the excursion's own and that hash is not this task's work.
            Assert.True(HeldSeconds(dbPath) > 0d);
            Assert.Equal(900d + 600d, Read(store).ActiveSeconds, precision: 6);
            Assert.True(task.Id > 0);
        });
    }

    /// <summary>
    /// The whole-day reconciliation the handover has to report. Three accumulators, one number.
    /// Every credited bucket in these tests carries an application, so the rollup's known gap for
    /// app-less buckets is zero here and the three must agree exactly.
    /// </summary>
    [Fact]
    public void TheThreeAccumulators_AgreeToTheSecond_AcrossADayWithDriftInIt()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);

            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "code", 120);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 200);
            Run(store, dbPath, ref t, AttentionSegmentKinds.PassiveConsumption, "youtube", 60);

            double tasksTotal = SumAllTaskActiveSeconds(dbPath);
            double dayTotal = SumAllTaskDayHours(dbPath);
            double rollupTotal = SumRollupActiveWhereTaskAssigned(dbPath);

            Assert.Equal(tasksTotal, dayTotal, precision: 6);
            Assert.Equal(tasksTotal, rollupTotal, precision: 6);
            Assert.True(task.Id > 0);
        });
    }

    /// <summary>
    /// Bug 8, the name collision. <c>unattributed_hours</c> already exists on the report line and
    /// means something completely different — the gap between a task's active_seconds and the
    /// rollup's lifetime seconds, from buckets with no identifiable application. The new concept
    /// travels as <c>unassigned_seconds</c>, and it has to stay that way all the way to the wire:
    /// one label on the screen computed two incompatible ways is worse than no label at all.
    /// </summary>
    [Fact]
    public void TheHeldTotal_ReachesTheWireAsUnassignedSeconds_NotAsUnattributedHours()
    {
        var response = new Porta.Agent.LocalApi.LocalApiTasksTodayResponse(
            Tasks: Array.Empty<Porta.Agent.LocalApi.LocalApiTask>(),
            TimestampUtc: DayStart,
            UnassignedSeconds: 2705d);

        string json = System.Text.Json.JsonSerializer.Serialize(response);

        Assert.Contains("\"unassigned_seconds\":2705", json);
        Assert.DoesNotContain("unattributed", json);
    }

    /// <summary>
    /// Fleet telemetry — seconds say how much was held, this says how often it fired.
    ///
    /// The pair is the point. 90 minutes held in ONE stretch is the feature working; 90 minutes
    /// held across fifteen stretches is the gate thrashing through somebody's afternoon in slices,
    /// and from the seconds alone the two are identical.
    /// </summary>
    [Fact]
    public void TheDriftHoldCount_CountsEpisodes_NotBuckets()
    {
        RunWithStore((store, dbPath) =>
        {
            AgentTask task = Start(store, dbPath, "landing page copy", DayStart);
            Learn(store, dbPath, DayStart, "code", LearningBuckets);
            string day = GoalDayOf(DayStart);

            Assert.Equal(0, store.ReadDriftHoldCountForDay(day));

            // One long drift: hundreds of held buckets, but Ari decided ONCE.
            DateTimeOffset t = DayStart.AddSeconds(LearningBuckets * BucketSeconds);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 300);
            Assert.True(HeldSeconds(dbPath) > 1000d);
            Assert.Equal(1, store.ReadDriftHoldCountForDay(day));

            // Back to the task's own work, then away again: that is a second decision.
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "code", 5);
            Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, "elsewhere", Threshold + 10);
            Assert.Equal(2, store.ReadDriftHoldCountForDay(day));

            // And a different day reads zero rather than inheriting yesterday's total — which is
            // also how the count rolls over with no scheduled reset for anyone to forget.
            Assert.Equal(0, store.ReadDriftHoldCountForDay("2026-08-31"));
            Assert.True(task.Id > 0);
        });
    }

    // ── harness ───────────────────────────────────────────────────────────────────────

    private const double BucketSeconds = 5d;
    private const int BucketSecondsInt = 5;

    // 15 minutes of credited work, at 5-second buckets.
    private const int LearningBuckets = 15 * 60 / BucketSecondsInt;

    // 09:00 LOCAL, so a run of a few hours can never cross the 05:00 goal-day boundary whatever
    // zone the test machine is in.
    private static readonly DateTimeOffset DayStart = LocalAt(2026, 9, 1, 9);

    // 04:00 LOCAL, so a run of a couple of hours crosses it deliberately.
    private static readonly DateTimeOffset BeforeBoundary = LocalAt(2026, 9, 1, 4);

    private static DateTimeOffset LocalAt(int year, int month, int day, int hour)
    {
        var naive = new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(naive, TimeZoneInfo.Local.GetUtcOffset(naive));
    }

    private static string GoalDayOf(DateTimeOffset instant)
    {
        DateTimeOffset local = instant.ToLocalTime();
        return (local.Hour < 5 ? local.AddDays(-1) : local).ToString("yyyy-MM-dd");
    }

    private static AgentTask Start(
        SqliteBrainStateStore store, string dbPath, string title, DateTimeOffset at)
    {
        AgentTask created = store.CreateTask(title, 7200, at)!;
        store.ActOnTask(created.Id, "start", at);
        return created;
    }

    private static void Learn(
        SqliteBrainStateStore store, string dbPath, DateTimeOffset from, string app, int buckets)
    {
        DateTimeOffset t = from;
        Run(store, dbPath, ref t, AttentionSegmentKinds.Focus, app, buckets);
    }

    /// <summary>
    /// N consecutive finalized buckets, through the REAL decision and the REAL writers in the REAL
    /// order the bucket transaction uses. A copy of the ordering here would be a test of the copy.
    /// </summary>
    private static void Run(
        SqliteBrainStateStore store,
        string dbPath,
        ref DateTimeOffset cursor,
        string kind,
        string app,
        int buckets)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        for (int i = 0; i < buckets; i++)
        {
            string at = cursor.ToUniversalTime().ToString("O");
            using (SqliteTransaction tx = conn.BeginTransaction())
            {
                SqliteBrainStateStore.TaskAttributionContext ctx = store.ResolveTaskAttribution(
                    conn, tx, kind, app, at);
                SqliteBrainStateStore.AttributeBucketToTask(
                    conn, tx, ctx.TaskId, BucketSeconds, ctx.Credit, at);
                SqliteBrainStateStore.HoldBucketForTask(
                    conn, tx, ctx.TaskId, BucketSeconds, ctx.Credit, app, kind, at);
                store.LearnAffinityForBucket(
                    conn, tx, ctx.TaskId, ctx.Learning, app, BucketSeconds, at);
                SqliteBrainStateStore.AutoCloseStaleActiveTask(conn, tx, at);
                SqliteBrainStateStore.AccumulateTaskDayHours(
                    conn, tx, ctx.TaskId, BucketSeconds, ctx.Credit, at);
                SqliteBrainStateStore.AccumulateAppUsageRollup(
                    conn, tx, ctx.TaskId, BucketSeconds, ctx.Credit, app, at);
                tx.Commit();
            }

            cursor = cursor.AddSeconds(BucketSeconds);
        }
    }

    private static AgentTask Read(SqliteBrainStateStore store)
        => store.ReadTasks(DayStart.AddDays(-2), DayStart.AddDays(2))
            .OrderBy(t => t.Id)
            .First();

    // ── raw reads ─────────────────────────────────────────────────────────────────────

    private static double Scalar(string dbPath, string sql, params (string, object)[] args)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        object? scalar = cmd.ExecuteScalar();
        return scalar is null || scalar == DBNull.Value ? 0d : Convert.ToDouble(scalar);
    }

    private static string Text(string dbPath, string sql, params (string, object)[] args)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static double HeldSeconds(string dbPath)
        => Scalar(dbPath, "SELECT COALESCE(SUM(seconds),0) FROM pending_task_seconds WHERE state='held';");

    private static int HeldRowCount(string dbPath)
        => (int)Scalar(dbPath, "SELECT COUNT(*) FROM pending_task_seconds;");

    private static int HeldRowCountInState(string dbPath, string state)
        => (int)Scalar(
            dbPath,
            "SELECT COUNT(*) FROM pending_task_seconds WHERE state = $s;",
            ("$s", state));

    private static double SumPendingInState(string dbPath, string state)
        => Scalar(
            dbPath,
            "SELECT COALESCE(SUM(seconds),0) FROM pending_task_seconds WHERE state = $s;",
            ("$s", state));

    private static int HeldRowCountForTask(string dbPath, long taskId)
        => (int)Scalar(
            dbPath,
            "SELECT COUNT(*) FROM pending_task_seconds WHERE task_id = $t;",
            ("$t", taskId));

    private static double SumTaskDayHours(string dbPath, long taskId)
        => Scalar(
            dbPath,
            "SELECT COALESCE(SUM(active_seconds),0) FROM task_day_hours WHERE task_id = $t;",
            ("$t", taskId));

    private static double SumTaskDayHoursForDay(string dbPath, long taskId, string day)
        => Scalar(
            dbPath,
            "SELECT COALESCE(SUM(active_seconds),0) FROM task_day_hours WHERE task_id = $t AND local_date = $d;",
            ("$t", taskId), ("$d", day));

    private static double SumRollupActiveForTask(string dbPath, long taskId)
        => Scalar(
            dbPath,
            "SELECT COALESCE(SUM(active_seconds),0) FROM app_usage_rollup WHERE task_id = $t;",
            ("$t", taskId));

    private static double SumRollupActiveWhereTaskAssigned(string dbPath)
        => Scalar(dbPath, "SELECT COALESCE(SUM(active_seconds),0) FROM app_usage_rollup WHERE task_id > 0;");

    private static double SumAllTaskActiveSeconds(string dbPath)
        => Scalar(dbPath, "SELECT COALESCE(SUM(active_seconds),0) FROM tasks;");

    private static double SumAllTaskDayHours(string dbPath)
        => Scalar(dbPath, "SELECT COALESCE(SUM(active_seconds),0) FROM task_day_hours;");

    private static bool IsFootprintMember(string dbPath, long taskId, string app)
        => Scalar(
            dbPath,
            """
            SELECT COUNT(*) FROM task_app_affinity
            WHERE task_id = $t AND app_hash = $a AND source IN ('learned','confirmed');
            """,
            ("$t", taskId), ("$a", app)) > 0d;

    private static int FootprintMemberCount(string dbPath, long taskId)
        => (int)Scalar(
            dbPath,
            """
            SELECT COUNT(*) FROM task_app_affinity
            WHERE task_id = $t AND source IN ('learned','confirmed');
            """,
            ("$t", taskId));

    private static int ProvisionalRowCount(string dbPath, long taskId)
        => (int)Scalar(
            dbPath,
            "SELECT COUNT(*) FROM task_app_affinity WHERE task_id = $t AND source = 'provisional';",
            ("$t", taskId));

    private static string ReadAffinitySource(string dbPath, long taskId, string app)
        => Text(
            dbPath,
            "SELECT source FROM task_app_affinity WHERE task_id = $t AND app_hash = $a;",
            ("$t", taskId), ("$a", app));

    private static string ReadColumn(string dbPath, long taskId, string column)
        => Text(dbPath, $"SELECT {column} FROM tasks WHERE id = $id;", ("$id", taskId));

    private static void SetRuntimeState(string dbPath, string key, string value)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO runtime_state (key, value_json, updated_utc) VALUES ($k, $v, $u)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void InsertSentReport(string dbPath, string coverEndLocal)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO sent_reports
                (report_id, range_start_local, range_end_local, recipient_email,
                 requested_at_utc, sent_at_utc, send_state)
            VALUES ('r1', $start, $end, 'client@example.com', $now, $now, 'sent');
            """;
        cmd.Parameters.AddWithValue("$start", coverEndLocal);
        cmd.Parameters.AddWithValue("$end", coverEndLocal);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void RunWithStore(Action<SqliteBrainStateStore, string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "porta-attribution-tests", Guid.NewGuid().ToString("N"));
        string dbPath = Path.Combine(dir, "core-state.db");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteBrainStateStore(
                new DeepWorkOutputActionCoordinator(new NoOpNotificationController()),
                new OverloadBrightnessActionCoordinator(new NoOpBrightnessController()),
                dbPathOverride: dbPath);
            body(store, dbPath);
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
