# Task attribution — credit only the work that belongs to the task

**Goal:** stop Ari crediting time to whichever task is marked `active` regardless of what the
user is actually doing. Today the timer answers "was he working". It never asks "was he working
on *this*". An afternoon spent on something else lands on the task that happens to be open, and
that number is the one under the client's report.

**Scope:** `porta-agent-windows-x64` only. Agent side. No control-plane, ops-console or website
changes. No UI in this chat (see "Not in this chat" at the end).

**Read first:** `COWORK_START_HERE.md`, then this file end to end before writing anything.
The reasoning in section 3 is the point of the change. If you implement section 4 without it you
will ship the inverse bug (silently not counting real work), which is worse than the bug we are
fixing because the user cannot see it happen.

**Hard rule:** the three accumulators must agree, always. `tasks.active_seconds`,
`task_day_hours` and `app_usage_rollup` are the same measurement at three grains and the report
reads all three. One gate, one predicate, one place. If you write the drift test more than once
you have already introduced the bug.

---

## 1. What is wrong, confirmed in code

`SqliteBrainStateStore.cs`, bucket transaction around line 470:

```
AttributeBucketToActiveTask(connection, tx, Time.bucketSeconds, derivedSession.AttentionSegmentKind, mapped.StartUtc);
AutoCloseStaleActiveTask(connection, tx, mapped.StartUtc);
AccumulateTaskDayHours(connection, tx, Time.bucketSeconds, derivedSession.AttentionSegmentKind, mapped.StartUtc);
AccumulateAppUsageRollup(connection, tx, Time.bucketSeconds, derivedSession.AttentionSegmentKind, mapped.DominantAppHash, mapped.StartUtc);
```

`AttributeBucketToActiveTask` (line ~3844) is:

```sql
UPDATE tasks SET active_seconds = active_seconds + $seconds, ... WHERE state = 'active';
```

Two gates exist and neither is about the task:

1. `AttentionSegmentKinds.IsAway(kind)` — was he at the machine.
2. `AttentionSegmentKinds.IsWorkingKind(kind)` — allow-list of `focus`, `context_excursion`,
   `in_meeting`. Everything else (passive consumption, social loops, gaming, `unknown`) goes to
   `offtask_seconds`.

There is no third test. `mapped.DominantAppHash` is **already in scope at this call site** (three
lines later `AccumulateAppUsageRollup` uses it) and is not consulted.

### Why the existing safety net cannot catch this

`AutoCloseStaleActiveTask` (line ~3906) returns a task to `pending` after
`StaleActiveTaskSeconds` (45 min) with no credited working time. `last_credited_at_utc` is
stamped whenever `working == 1`. So when the user is genuinely heads-down on **different** work,
every bucket is `focus`, which credits the wrong task **and refreshes the stale timer in the same
statement**. The net is disarmed by exactly the behaviour it needs to catch. It was built for
idleness and for a film playing in front of an open task, and it does that job correctly.

### The two failures, and which one is still open

| Failure | Example | Status |
|---|---|---|
| Not work at all | YouTube, gaming, a film | **Solved.** Category → non-working kind → `offtask_seconds`, and `last_credited_at_utc` does not refresh, so the 45-min close fires. Chat 3, 2026-08-24. |
| Work, but not this task | coding task B, answering email, a research rabbit hole, admin with no task at all | **Open.** Genuinely `focus`. Passes every gate that exists. This is the whole of this plan. |

The second row does not split into "another task" and "nothing at all". Detection is identical
for both: the foreground app is not what this task looks like. Only the destination differs.

---

## 2. The signal, and why it is not page reading

Do **not** add page-content reading, LLM relevance scoring, or any comparison of a task title to
what is on screen. Three reasons, in order of weight:

1. **Privacy posture.** Today only an HMAC hash and a coarse registrable-domain token persist
   (`BrowserDomainResolver`, `AppCategory.cs`). That contract is part of what makes this sellable
   to a consultant billing a corporate client. Reading content to judge relevance breaks it.
2. **It fails where it matters.** Are the Supabase docs relevant to "fix the digest bug"? A
   classifier that says no makes Ari silently stop counting real work. Invisible undercounting is
   worse than visible overcounting.
3. **We do not need it.** The signal is already in the database.

`app_usage_rollup` is keyed `(hour_start_utc, task_id, app_hash)`. Every task already carries a
record of which apps and sites its credited hours were spent in. The question is not "does this
page mean what the title says". It is **"does this look like the same kind of work this task has
been"**. That is a set-membership test on a hash. No understanding required.

**Principle to hold on to:** category decides what work looks like in general; footprint decides
what work looks like *for this task*. Where they disagree, footprint wins, because it is evidence
from the user's own behaviour instead of a guess from a lookup table.

---

## 3. Locked decisions — do not change these without asking Yadin

| Decision | Value |
|---|---|
| Footprint storage | New table `task_app_affinity`, not a query over `app_usage_rollup` (the rollup already contains historic drift and would teach the wrong footprint) |
| Learning window | Every foreground hash seen during a task's **first 15 minutes of credited work** is learned |
| Later learning | A hash reaching **≥ 2 minutes of credited time** during that task's credited stretches is learned |
| Affinity cap | **24 hashes per task**, keep the highest credited seconds; browsers produce one hash per site, so an uncapped footprint would grow wide enough never to trip |
| Drift threshold | **10 continuous minutes** (120 buckets at `Time.bucketSeconds = 5`) of working-kind buckets whose dominant hash is outside the affinity set |
| Below threshold | Credited normally. A two-minute lookup in the browser must not break a task |
| On drift | Seconds are **held**, not credited and not discarded. Written to `pending_task_seconds` |
| Held seconds in reads | Surface as **unassigned**, never as task time, never silently dropped |
| Release | Replays the held buckets through the same three writers **at their original bucket timestamps** |
| Empty affinity | Task is inside its learning window → credit and learn. Never hold on an empty footprint |
| Naming | New concept is `unassigned_seconds`. **`unattributed_hours` already exists and means something else** (see bug 8) |
| Inverse case (YouTube that IS the task) | **Not in this chat.** Do not let a confirmed affinity promote a non-working kind |

---

## 4. What ships

### 4.1 Migration V35

`Db/Migrations.cs` — append a new step, never edit a shipped one (see the V31 comment for why).
`LatestSchemaVersion` is derived from the list. `Db/Schema.cs` — new `ApplyTaskAffinityV35`,
following `ApplyTaskAutoCloseV34`'s shape. Additive, defaulted, LOCAL ONLY.

```sql
CREATE TABLE IF NOT EXISTS task_app_affinity (
    task_id         INTEGER NOT NULL,
    app_hash        TEXT    NOT NULL,
    credited_seconds REAL   NOT NULL DEFAULT 0,
    source          TEXT    NOT NULL DEFAULT 'learned',   -- 'learned' | 'confirmed'
    first_seen_utc  TEXT    NOT NULL,
    PRIMARY KEY (task_id, app_hash)
) WITHOUT ROWID;

CREATE TABLE IF NOT EXISTS pending_task_seconds (
    task_id          INTEGER NOT NULL,
    bucket_start_utc TEXT    NOT NULL,
    local_date       TEXT    NOT NULL,
    seconds          REAL    NOT NULL,
    app_hash         TEXT    NOT NULL,
    kind             TEXT    NOT NULL,
    state            TEXT    NOT NULL DEFAULT 'held',     -- 'held' | 'released' | 'unassigned'
    PRIMARY KEY (task_id, bucket_start_utc)                -- idempotency, see bug 7
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS ix_pending_task_seconds_state ON pending_task_seconds(state, local_date);
```

`local_date` is stored at hold time using `AgentCoreController.GoalDay` on the **bucket's own**
local timestamp, the same 05:00-boundary key `daily_goals` and `task_day_hours` use. Storing it
at hold time is what makes release safe (bug 5).

### 4.2 One decision function

New pure type, `src/Porta.Agent.Common/Analysis/TaskCreditDecision.cs`. Pure, Windows-agnostic,
no SQLite, fully unit-testable, in the same spirit as `BrowserDomainResolver`.

```csharp
public enum TaskCredit { Credit, Hold, OffTask, None }

public static TaskCredit Decide(
    string? attentionSegmentKind,
    string? dominantAppHash,
    IReadOnlySet<string> affinity,
    bool withinLearningWindow,
    int consecutiveDriftBuckets,
    int driftThresholdBuckets)
```

Order of tests, and this order matters:

1. `IsAway(kind)` → `None`. Unchanged behaviour.
2. `!IsWorkingKind(kind)` → `OffTask`. Unchanged behaviour. **The category gate still runs first
   and is untouched.** YouTube never reaches the affinity test.
3. `withinLearningWindow` → `Credit`.
4. hash empty or unresolvable → `Credit`. Fail *open* here: an unreadable app is not evidence of
   drift, and failing closed would hold time on every unrecognised window.
5. hash in `affinity` → `Credit`.
6. `consecutiveDriftBuckets < driftThresholdBuckets` → `Credit`.
7. otherwise → `Hold`.

This function is the only place the rule exists. All three writers call it.

### 4.3 Wire the writers

Compute the decision **once**, above the three calls in the bucket transaction, and pass the
result down. Change each writer to take `TaskCredit` instead of re-deriving `working`:

- `AttributeBucketToActiveTask` — `Credit` behaves as today. `Hold` writes nothing to `tasks`
  (not `active_seconds`, not `offtask_seconds`, and **not** `last_credited_at_utc`). `OffTask`
  behaves as today.
- `AccumulateTaskDayHours` — same, and `PromoteActiveTaskToInProgress` fires only on `Credit`.
- `AccumulateAppUsageRollup` — on `Hold` the row is still written but with **`task_id = 0`**, not
  the active task id. See bug 2; this one will silently break the report if you get it wrong.
- New `HoldBucketForActiveTask` — on `Hold`, insert the `pending_task_seconds` row.

### 4.4 The drift counter

`consecutiveDriftBuckets` is running state, not a scan. Buckets are 5 seconds, so this executes
720 times an hour; do not query anything per bucket that you can hold in memory.

- Increment when a working-kind bucket's hash is outside affinity.
- Reset to zero on: a hash inside affinity, a non-working kind, `away`, a task switch, a task
  auto-close, and an affinity write.
- Persist in `runtime_state` (key `task_drift_streak_v1`, with the task id) so a service restart
  mid-drift does not silently restart the count at zero.
- Cache the active task's affinity set in memory. Invalidate on task switch, on affinity write,
  and on confirm.

### 4.5 Affinity learning

Inside the same transaction, on `Credit` only:

- upsert `(active task id, dominant hash)`, `credited_seconds += bucketSeconds`
- insert with `source = 'learned'`
- learning window = task's first 15 minutes of credited work, measured from
  `tasks.started_at_utc`, or `credited_seconds` summed for that task, whichever you can read
  cheaply — state which you used
- after the window, a hash only enters the set once its `credited_seconds` ≥ 120
- enforce the 24-hash cap by dropping the lowest `credited_seconds` row with `source = 'learned'`.
  **Never drop a `confirmed` row.**

### 4.6 Auto-close, which now starts working

No code change to `AutoCloseStaleActiveTask`. Because `Hold` no longer stamps
`last_credited_at_utc`, a task the user has drifted away from will now go stale and return to
`pending` after 45 minutes, closing at the last genuinely credited bucket. That is the intended
outcome and it is the single largest accuracy win in this plan. Confirm it in a test.

### 4.7 Read side, minimum

Held time must be visible, not invisible. In this chat, the minimum:

- a store method `ReadUnassignedSecondsForDay(localDate)` summing `pending_task_seconds` where
  `state = 'held'`
- Today's totals gain an `UnassignedSeconds` field carried through to `LocalApi` contracts

Rendering it is Chat B. But the number must exist and be correct at the end of this chat, because
the whole justification for holding time is that the user can see it.

---

## 5. Bugs this will cause if you are not careful

This section is not optional reading. Each item is a real failure mode of this specific design.

**1. Three writers drifting apart.** The existing comments say it plainly:
"these two are the same measurement at two grains and must never disagree, so the rule lives in
one predicate." If the drift test is re-derived inside each writer, the report will show two
different totals for the same work. Compute once, pass down.

**2. `app_usage_rollup` crediting a task the `tasks` table does not.** The rollup takes the
active task id from its own `SELECT id FROM tasks WHERE state='active'`. On `Hold` this **must**
become `task_id = 0`. Lines ~3473 and ~3523 read per-task hours as
`SUM(active_seconds) FROM app_usage_rollup WHERE task_id = t.id`, so leaving the id in place
means the Reports page reports the exact hours this change exists to remove.

**3. Cold-start starvation.** A brand-new task has an empty affinity set. Apply the gate
immediately and every task reads zero forever. The learning window is load-bearing, not a nicety.

**4. Circular starvation, the subtle one.** Drift stops crediting; no crediting means no
learning; so a tool genuinely adopted for this task mid-session can never enter the footprint on
its own, and the task never recovers. In this chat the exit is the 45-minute auto-close: the task
stops being active and stops accruing, which is safe and visible. Do **not** add a
learn-anyway-after-N-minutes escape hatch to paper over it. The real exit is the user confirming,
and that is Chat B.

**5. Releasing at "now".** `AccumulateTaskDayHours` resolves the goal day from the bucket, and
`AccumulateAppUsageRollup` resolves the hour from the bucket. A release that replays with the
current timestamp moves yesterday's work onto today. This is the same mistake
`AutoCloseStaleActiveTask` documents in its own summary. Store `bucket_start_utc` and
`local_date` at hold time and replay with them.

**6. Held rows orphaned by auto-close.** Because the auto-close now fires on drift, the task
returns to `pending` while its held rows still exist. Every held row must carry its own explicit
`task_id`. Nothing in the hold or release path may use `WHERE state = 'active'`.

**7. Double hold and double release.** The bucket finalizer has a write guard
(`BucketFinalizerWriteGuard`) and buckets can be replayed. The primary key
`(task_id, bucket_start_utc)` makes the hold idempotent. The release must flip `state` inside the
same transaction as the replay, so a crash between them cannot credit the same seconds twice.

**8. The `unattributed_hours` name collision.** It already exists (line ~4453) and means
something completely different: the diagnostic gap between a task's `active_seconds` and the
rollup's lifetime seconds, arising because the rollup drops buckets with no identifiable app. If
you reuse that name the Reports page will show one label computed two incompatible ways. The new
concept is `unassigned_seconds`. Keep them separate everywhere, including the LocalApi contracts.

**9. Frozen sent reports.** A sent report is a permanent snapshot behind a client link
(`sent_report_archive_v32`). Reattributing time for a day already covered by a sent report
changes history the client has read. Rule for this chat: held rows whose `local_date` falls on or
before the newest sent report's cover date may only ever resolve to `unassigned`, never onto a
task. Amending a sent report is a separate decision and is not yours to make.

**10. Footprints too wide to trip.** Browsers produce one hash per site. Without the 24-hash cap
and the 2-minute minimum, a week-old task accumulates a footprint containing most of the machine
and the gate silently stops firing. Cap and minimum are both required; test both.

**11. Hash instability.** `app_hash` is `HMAC-SHA256(installSecret, identifier)`. A reinstall or a
rotated secret changes every hash on the machine at once, so every task drifts simultaneously and
the user's whole day is held. Detect the condition (active task with a non-empty affinity set
where zero hashes have matched for a full drift window) and fall back to the learning window
rather than holding everything. Log it. Do not fail closed here.

**12. Cost per bucket.** 5-second buckets, 720 an hour, inside the write transaction alongside
the existing capture pipeline. No per-bucket query of `task_app_affinity`. Cache, and invalidate
on the four events listed in 4.4.

**13. `context_excursion` inherits the wrong app.** It is in `WorkingKinds`, so short hops are
credited by design. Evaluate the excursion on its **own** dominant hash, not the anchor's, or a
five-minute hop into anything will keep passing the affinity test because the anchor still
matches.

**14. Do not resurrect the film bug from the other side.** A `confirmed` affinity must not
promote a non-working kind into credited time in this chat. The legitimate case (a task whose
work genuinely is YouTube) needs the stillness cap as well as the affinity, and that is Chat C.
Section 4.2's ordering enforces this: the category gate runs before the affinity test, always.

**15. Visible behaviour change.** The widget's task timer will now stop advancing while the user
works on something else. That is correct and intended, but it will look like a bug the first time
it happens. Do not "fix" it. Note it in the handover so Yadin sees it before a user does.

**16. Existing failures.** Do not touch the four allowed Core test failures. Do not modify
`DerivedSessionMetricsComputer.cs` in this chat at all.

---

## 6. Tests

New `src/Porta.Agent.Core.Tests/TaskAttributionRelevanceTests.cs`, plus additions to
`TasksStoreTests.cs`, `StaleTaskAutoCloseTests.cs`, `AppUsageRollupTests.cs`,
`MigrationUpgradePathTests.cs` and `MigrationsConsistencyTests.cs`.

Pure decision tests (no DB):

- away → `None`; passive consumption → `OffTask`, whatever the affinity says
- inside learning window with empty affinity → `Credit`
- hash in affinity → `Credit`
- hash outside affinity, 119 buckets → `Credit`; 120 buckets → `Hold`
- empty or unresolvable hash → `Credit`, never `Hold`
- drift streak resets on an in-affinity bucket, on a non-working kind, and on task switch

Store tests:

- **the headline case:** task A active, 90 minutes of `focus` on a hash outside A's affinity →
  A's `active_seconds` gains only the first 10 minutes; `task_day_hours` matches
  `tasks.active_seconds` exactly; `app_usage_rollup` rows for the held stretch carry `task_id = 0`
- the same 90 minutes leaves `last_credited_at_utc` at the last credited bucket, and
  `AutoCloseStaleActiveTask` returns A to `pending` 45 minutes after it, not 45 minutes after now
- YouTube for 90 minutes still lands entirely in `offtask_seconds` and holds nothing (regression
  guard on Chat 3)
- release replays at original timestamps: a hold spanning the 05:00 goal-day boundary lands on
  the two correct days, never both on today
- release is idempotent; a replayed bucket neither double-holds nor double-credits
- affinity cap evicts the lowest `learned` row and never a `confirmed` row
- V35 upgrade from a V34 database preserves every existing task's hours unchanged

---

## 7. Prove it before handover

- Build the whole solution. Run all five test projects. State the pass/fail counts.
- On a live machine: start a task, work in its usual apps for 15 minutes, then spend 20 minutes
  in an unrelated app. Show that the task's seconds stop advancing at the 10-minute mark, that
  `pending_task_seconds` holds the remainder against that task id, and that the day's
  unassigned seconds equal the held total.
- Read `ProductVersion` off the built exes and report it. Do not assert what is installed.
- Report the three totals for a full day and show they agree:
  `SUM(tasks.active_seconds)`, `SUM(task_day_hours.active_seconds)`, and
  `SUM(app_usage_rollup.active_seconds WHERE task_id > 0)`.

---

## 8. Not in this chat

Say so and stop if you think any of these is needed to finish; do not widen scope.

- **Chat B — the ask.** Widget and evening-card prompt: "Last 20 minutes were in `<app>`. Still on
  `<task>`?" with three answers: still this task (release held seconds onto it, write the hash as
  `confirmed`), different task (release onto the chosen task), not work (mark `unassigned`). Plus
  rendering unassigned time in Today and the report.
- **Chat C — retro reattribution.** Moving a block of time between tasks on the timeline after the
  fact, including the sent-report amend rule from bug 9. This is what ultimately makes the number
  defensible; no live inference will be right every time.
- **Chat D — the inverse case.** Crediting a confirmed non-working-category app (a task whose work
  genuinely is YouTube or Netflix) using affinity plus the stillness cap.
