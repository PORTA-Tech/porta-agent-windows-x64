# Build Brief — Closing the Loop, and the Report

**Written:** 2026-08-06, after reading all three repos end to end.
**For:** Claude Code, doing the implementation.
**Read order:** `HANDOFF_Loop_And_Report.md` → `Ari_Loop_And_Report_Plan.md` → **this file**.

The first two documents are the strategy and the work queue. They are right about *what* and *why*.
They are wrong about several *wheres*, because they were written from a reading of the tests rather
than of the code. This file corrects those, pins the real paths, and records the decisions Yadin has
locked. **Where this file and the other two disagree about code, this file wins. Where they disagree
about intent, they win.**

---

## 1. The goal, so it isn't lost in the plumbing

Ari is changing from *a thing that shows you your day* into *a thing that does a job for you*.

Today Ari asks the user questions and hands back facts about themselves. Effort in, information out.
Nobody pays for that, and the founder — who has used it for months — says it has never once changed
what he actually did that day.

After this work: the user does their work, and Ari writes the report that **someone else** is waiting
for. A client, a boss, a co-founder. The user approves it with one tap and it sends.

The mechanism that was missing is a closed loop. Ari asks what you'll do each morning, then behaves
as if it never asked. **A commitment nobody follows up on is a note, not a commitment.** Every step
below exists to close that loop, and then to put a third party on the other end of it.

Two consequences for implementation:

- **The morning card is the highest-value three seconds in the product.** Anything that costs the
  user effort there — a click, a blank box, a slow model call — is worth more than it looks.
- **A number that reaches a client's inbox must be measured, never estimated.** When in doubt, omit
  the number. The digest builder already works this way (`digestEmailBuilder.ts:963` omits the whole
  tasks section rather than pad it). Keep that bar.

---

## 2. Corrections to the two plan documents

| Both docs say | Reality |
|---|---|
| `TasksStore` / `AgentTask` (agent core) | **No such class.** `TasksStore` and `DailyGoalsStore` are *test class names only*. The real code is methods on `SqliteBrainStateStore.cs`, behind `IBrainStateStore`. See §3. |
| `DailyGoalsStore` (agent core) | Same — methods on `SqliteBrainStateStore.cs`, lines 2387–2510. |
| `Porta.UserAgent/Interventions/...` | Correct, but the tree root is `src/` — `src/Porta.UserAgent/Interventions/...`. |
| Step 2: "the narrative payload needs local time now" | **Already sent.** `NarrativeGateway.cs:144` sends `LocalTimeHHmm`. |
| Step 2: "the payload needs yesterday's tasks and outcomes" | **Yesterday's *goal* is already loaded server-side** by `narrativeService.ts:158` `loadEnrichment`. Yesterday's *tasks* are not — nothing uploads them. See §5. |
| Step 3: "the biggest single item" | Smaller than billed. Both halves are adjacent sections of one file behind one interface. |
| `ONBOARDING.md`: widget USER-handle leak blocks rollout | Fixed 2026-07-20 (`349bc21`, `811c0e9`). Not build work. |
| `ONBOARDING.md`: version 1.2.122 | Released version is **1.2.127** (`f5e510b`), which reissued 1.2.126 after a bad manifest. |

---

## 3. The real code map

### Agent — `porta-agent-windows-x64`

| Thing | Where |
|---|---|
| Task record | `src/Porta.Agent.Core/AgentLocalStoreModels.cs:395` — `AgentTask` |
| Goal record | `src/Porta.Agent.Core/AgentLocalStoreModels.cs:412` — `AgentDailyGoal` |
| Task storage | `src/Porta.Agent.Core/SqliteBrainStateStore.cs:2145–2350` — `CreateTask` 2151, `ReadTasks` 2203, `ActOnTask` 2255 |
| **Time attribution** | `SqliteBrainStateStore.cs:2359` — `AttributeBucketToActiveTask` |
| Goal storage | `SqliteBrainStateStore.cs:2387–2510` — `UpsertMorningGoal` 2394, `GetDailyGoal` 2430, `RecordMiddayGoalAnswer` 2450, `RecordEndOfDayPlan` 2483 |
| Store interface | `src/Porta.Agent.Core/IBrainStateStore.cs:56–59` |
| Controller (goal-day boundary, ground-truth labels) | `src/Porta.Agent.Core/AgentCoreController.cs:1806–1920` — `GoalDay` 1809, `SetMorningGoal` 1812, `GetCarriedOverPlan` 1840 |
| Check-in card UI | `src/Porta.UserAgent/Interventions/UserCheckInCardWindow.cs` |
| Card presenter (STA thread, one-card-at-a-time) | `src/Porta.UserAgent/Interventions/UserCheckInCardPresenter.cs` |
| Pure scheduler (which check-in is due) | `src/Porta.UserAgent/Interventions/UserCheckInScheduler.cs` |
| Pure copy/mapping | `src/Porta.UserAgent/Interventions/UserCheckInCoordinator.cs` |
| Host orchestration | `src/Porta.UserAgent/UserAgentHost/UserAgentHost.cs:1501–1740` — `RefreshCheckInAsync` 1501, `HandleCheckInResponseAsync` 1629 |
| Widget task commands (**the only current caller of the task API**) | `src/Porta.UserAgent/Interventions/UserDesktopWidgetPresenter.cs:1142–1184` |
| Narrative payload assembly | `src/Porta.Agent.Service/Fleet/NarrativeGateway.cs` |
| Digest upload (incl. closed tasks) | `src/Porta.Agent.Service/Fleet/DailyDigestReporter.cs:222, 250` |
| Daily-stats upload (goal + midday answer) | `src/Porta.Agent.Service/Fleet/DailyStatsReporter.cs:118` |

### Control plane — `porta-control-plane`

| Thing | Where |
|---|---|
| Live check-in copy (Opus, grounded, fail-soft) | `src/core/narrativeService.ts` — `buildSituationFacts` 195, `buildNarrativeLine` 318 |
| Yesterday enrichment | `src/core/narrativeService.ts:158` — `loadEnrichment` |
| Narrative route | `src/api/controllers/agentController.ts:754` |
| Daily-stats write (**the only writer of `daily_stats`**) | `src/api/controllers/agentController.ts:683`, again at 850 |
| Digest email, plan-vs-reality section | `src/core/digestEmailBuilder.ts:62` (`tasks`), `:67` (`DigestTaskEntry`), `:963` (render) |
| Email delivery | `src/core/emailService.ts` (Resend) |

### Ops console — `porta-operations-console`

Read-only operator dashboard. Relevant only because two of its cards are fed by things this work
changes: per-user ground-truth label counts and prompt shown/answered rates, both on
`src/pages/UserActivity/UserActivityPage.tsx:270–283`. Nothing here blocks the build; just expect
those numbers to move.

---

## 4. Step 1 — the typing bug

**Decision locked (Yadin, 2026-08-06): the card takes foreground.** Instant typing is worth the risk
of interrupting another app. The card only fires on the first activity after the 5:00 boundary, so
the blast radius is one interruption a day.

**The plan says this is one missing focus call. It is three separate defects.**

`UserCheckInCardWindow.cs:253` (and `:339` for the EOD box):

```csharp
Loaded += (_, _) => goalBox.Focus();
```

1. **The handler is registered inside `BuildMorning`, which runs on every `ShowPrompt`** — but WPF's
   `Loaded` does not re-fire on `Show()` after `Hide()`, and the window is deliberately reused
   (`UserCheckInCardPresenter.EnsureWindowOnCurrentDispatcher` keeps it alive; `ShowPrompt:154`
   only calls `Show()` when not visible). So the box is focused on the **first card of the process
   lifetime and never again**. This is the bug the founder sees.
2. **Handlers accumulate.** Every `ShowPrompt` adds another `Loaded` subscription closing over a
   `TextBox` that is no longer in the visual tree. Leak plus stale closure.
3. **Logical focus is not foreground.** `ShowPrompt` calls `Show()` and only then `Activate()`
   (`:161`), and Windows refuses foreground to a process that doesn't already own it. Even with the
   TextBox focused, keystrokes go to whatever app is actually in front. Clicking the box works
   because the click grants foreground.

**Fix shape:**

- Move focus to a per-show hook that fires every time — `Activated` or `ContentRendered` — not
  `Loaded`. Register it **once in the constructor**, and have `ShowPrompt` record which control
  should receive focus, so `BuildMorning` / `BuildEndOfDay` never subscribe to window events.
- Acquire foreground properly before focusing (the usual `AttachThreadInput` / `SetForegroundWindow`
  dance, or an equivalent). The window is already `ShowActivated = true` and
  `WS_EX_TOOLWINDOW` (`OnSourceInitialized:361`), so it stays off Alt-Tab either way.
- **Keep the old path as a fallback**, per repo convention: if foreground acquisition fails, still
  focus the box so a click-then-type user is no worse off than today.

**Test bar:** a `UserAgent.Tests` case proving the focus target is set on the *second and third*
`ShowPrompt` on a reused window, not only the first — that is the actual regression. Foreground
acquisition itself needs one manual confirmation on Yadin's machine; it can't be unit-tested.

---

## 5. Step 2 — time and memory

The plan lists two symptoms. They have **three different causes**, and one of them is already fixed.

### 5a. "Ari sent last night's message this morning" — found mechanism

`UserCheckInCardPresenter.TryShow:59–70` refuses a new card with `checkin_card_busy` while
`_activeResponse` is set, and **an unanswered card never expires**. The sequence:

1. Yesterday 18:00 — EOD fires to an empty chair (Yadin left at 16:00). Card shows, `_activeResponse`
   is set, and the card sits on screen all night.
2. This morning — `RefreshCheckInAsync` correctly decides Morning is due (`UserCheckInScheduler:40`
   blocks EOD before hour 18, so the scheduler is innocent), but `TryShow` refuses: busy.
3. The user sees last night's EOD card, dismisses it, `OnCardResponse:278` clears the slot, and only
   then does the morning card appear.

**Fix:** expire an open, unanswered card. Two candidate rules, both cheap — an absolute age
(a card older than N hours is stale) or a goal-day rollover (a card from a previous `GoalDay` is
stale). Prefer the goal-day rule; it reuses `AgentCoreController.GoalDay:1809` and has no new
constant to tune. On expiry, hide the card and clear `_activeResponse` so the due check-in can show.

Also consider re-validating the kind at render time: if the prompt's kind is no longer what
`DecideCheckIn` would return, drop it.

**Keep this decision logic pure.** `UserCheckInScheduler` is the house style — an I/O-free function
with a reason code for every refusal. A staleness predicate belongs there, unit-tested, not inline
in the presenter.

### 5b. "Ari doesn't know yesterday's goal" — CORRECTED 2026-08-06

> **This section was wrong in the first draft and has been rewritten.** The original claimed
> `daily_stats` is written only by the digest path, and that the fix was to decouple them. That is
> false. Recorded here rather than deleted, because the correction is the useful part.

Everything on the payload side is already built:

- `NarrativeGateway.cs:144` already sends `LocalTimeHHmm`.
- `narrativeService.ts:158` `loadEnrichment` already loads yesterday's goal, midday answer, focus
  seconds and tracked seconds, and `buildSituationFacts:248` already renders them into the fact sheet.

**And the upload is already decoupled from the digest.** `DailyStatsReporter` is an independent
`BackgroundService` (`DailyStatsReporter.cs:19`) that pushes every 2 hours whenever the machine is on
and linked, with no minimum-activity gate, carrying `Goal` and `MiddayAnswer` (`:180`). Its own
docstring says it exists so the server has fresh stats *even when the machine is off at the 19:30 UTC
digest time*. The digest's `LastSentDate` does not gate it. Verified in the field: `state_buckets`
shows 9+ hours of uptime on 2026-08-05 — four to five upload opportunities — and a goal was set at
07:47 UTC with a midday answer at 10:00 UTC.

**So there is no delivery bug here and nothing to fix in this direction.** The real cause is one of
the following, and they must be distinguished by measurement before any code changes:

1. **The narrative call is failing outright, roughly seven times in ten. This is the single largest
   defect found so far and it is in neither plan document.** Across 43 logged calls:
   morning 12F/6T (67%), midday 7F/4T (64%), eod 11F/3T (79%). On every failure Ari falls back to the
   templated copy in `UserCheckInCoordinator.BuildPrompt:75`, which cannot mention yesterday, cannot
   be specific, and cannot be the thing that makes Ari feel like it is paying attention.

   **Cold start is ruled out — measured, 2026-08-06.** `GET /health` after 5.5h idle returned in
   **199ms**, indistinguishable from warm (173–183ms); a Render spin-down is 30–60s+. Two further
   falsifiers from the log: failure rates are **uniform across kinds**, when morning — always the
   first call after an overnight shutdown — should be far worse if cold start were the cause; and on
   **2026-07-21 two calls 119 seconds apart both failed**, when the first should have warmed the
   instance for the second. Six overnight gaps of 12–27h returned `used=True`. This is not a hosting
   config change. *(This section previously proposed cold start as the leading hypothesis. It was
   wrong, and the falsification is the useful record.)*

   **TIMEOUT HYPOTHESIS ALSO FALSIFIED — 2026-08-06 morning.** Measured in the field: the call
   failed in **~3.0 seconds** (fired 06:28:07.61, logged `used=False` at 06:28:10.64) against budgets
   of 10s/9s/8s. **A 3-second failure is not a timeout.** Something is failing fast. Also ruled out:
   the request carries no `temperature`/`top_p`/`budget_tokens`, so it is not a universal 400.

   **Do not propose a fourth hypothesis from the agent side — the answer is in the server logs.**
   Every remaining path in `buildNarrativeLine` logs its own reason on the control plane:
   `'[narrative] Rejected unknown account'` (`isKnownAccount` false — note the route is
   unauthenticated and gated on the account UUID existing), `'Daily call cap reached'`
   (`NARRATIVE_DAILY_CALL_CAP = 16`), `'ANTHROPIC_API_KEY not set'`, `'Model declined'`,
   `'Unusable reply'`, or `'[narrative] Model call failed:'` with the API error. **Read the Render
   logs for a timestamp where the agent logged `used=False` and the answer will be sitting there.**
   Three hypotheses have now been guessed and falsified; this one is free to measure.

   *(Superseded, kept as record.)* The former leading cause was the timeout chain:
   10s (`UserAgentHost.TryGetNarrativeLineAsync:1750`) → 9s (`NarrativeGateway.CallBudget:29`) →
   **8s (`narrativeService.REQUEST_TIMEOUT_MS:22`, which fires first)**, against an Opus call
   (`MODEL = 'claude-opus-4-8'`, `max_tokens: 250`, `:21–23`). A hard 8s wall on an Opus request with
   a fact-sheet prompt explains everything cold start could not: uniform failure across kinds, no
   correlation with gap length, and retries that sometimes win because each attempt is an independent
   draw. **Not yet measured directly** — timing the narrative endpoint costs an Opus call against the
   daily cap (`NARRATIVE_DAILY_CALL_CAP = 16`) and populates the line cache.

   **Decision locked (Yadin, 2026-08-06): show the card instantly, upgrade the line when it arrives.**
   Do not raise the agent-side budget and make the card wait — that spends the three seconds of
   willingness the whole product depends on. The card renders immediately with the honest template,
   and the LLM line replaces it in place when it lands. Implementation notes:
   - Move the narrative fetch off the blocking path in `RefreshCheckInAsync:1586`, which currently
     `await`s it *before* `TryShowGatedCheckIn:1598`.
   - The card must be able to update its message after it is on screen. `UserCheckInCardWindow`
     already owns `_message` as a field, so this is a dispatcher-marshalled setter, not a rebuild.
   - **Do not swap text under a user who has started typing or answering.** If the box has focus and
     content, or a chip has been clicked, drop the upgrade silently.
   - The swap must be quiet — no flash, no layout jump. A card that visibly rewrites itself reads as
     broken.
   - Fail-soft is unchanged: no line, or a line that arrives after the card is gone, changes nothing.

   While implementing, also verify `MODEL = 'claude-opus-4-8'` is a currently valid model string. An
   invalid model would fail closed exactly like a timeout, and it has never been checked.
2. **The fact sheet reaches the model but the model doesn't use it.** `KIND_INSTRUCTION.morning`
   (`narrativeService.ts:101`) only says "reference yesterday's outcome *when provided*" — nothing
   requires it. On a `used=True` day where Ari still ignored yesterday, this is the likelier cause,
   and the fix is prompt-side, not transport.
3. **Date-key mismatch.** `daily_stats.date_local` is stamped with the **UTC** date
   (`DailyStatsReporter.cs:137`), while the narrative's `dateLocal` is **machine-local with a 05:00
   boundary** (`NarrativeGateway.cs:123`). `loadEnrichment` selects yesterday with
   `s.dateLocal < dateLocal`, so the two schemes must agree. They roughly do at UTC+4; they will not
   for a user in another timezone — see the note below.

**Latent fleet bug, worth logging now and fixing before step 10:** `TimezoneOffsetMinutes` is
hardcoded to `DubaiOffsetMinutes = 240` (`DailyStatsReporter.cs:26, :145`). Every user's day is
stamped as if they were in Dubai. Harmless while the only real user is in Dubai; wrong the moment a
report with dated hours goes to someone else's client.

### 5c. Yesterday's *tasks* genuinely are missing

`daily_stats` carries goal and midday answer only. Tasks reach the server on the digest payload
(`DailyDigestReporter.cs:222`) and are used to render the email, but are never persisted for later
recall. Once step 3 makes tasks the day's real record, **the report in step 10 needs them stored**.
Add a tasks column/table alongside `daily_stats` when step 3 lands, not before.

---

## 6. Step 3 — merge goals and tasks (the foundation)

### What actually has to change

The two halves already live in the same file behind the same interface and the same SQLite database.
The widget's task commands and any check-in path both travel
`LocalApi → AgentCoreController → SqliteBrainStateStore` — **one store, no split, confirmed**. Today
`UserDesktopWidgetPresenter.cs:1142` is the *only* caller of `CreateTask` / `ActOnTask` in the entire
product. That's the whole defect: the check-in writes `daily_goals`, the widget writes `tasks`, and
nothing joins them.

So step 3 is: **`HandleCheckInResponseAsync` (`UserAgentHost.cs:1639`) stops calling
`SetMorningGoalAsync` alone and starts creating tasks**, and `daily_goals` becomes a view over
`tasks` rather than a parallel record.

### The decision that could sink the checkpoint

`AttributeBucketToActiveTask:2359` does:

```sql
UPDATE tasks SET active_seconds = ... WHERE state = 'active'
```

**Time only lands on a task somebody explicitly started.** If the morning check-in creates three
tasks and the user never opens the widget to press start — and the founder never opens the widget —
all three read `0m`. Step 5's *"Pricing page — 1h 12m · Done · Not yet"* would show nothing but
zeros, and the loop would look closed while measuring nothing.

**Decision locked (Yadin, 2026-08-06): auto-start the first task.** When the user accepts the morning
list, task 1 goes `active` immediately. Midday and evening cards let them switch. This keeps
`AttributeBucketToActiveTask` and the one-active-at-a-time invariant **completely untouched** — the
existing `ActOnTask(id, "start")` already handles switching correctly (`:2267–2275` returns the
previous active task to `pending` with its accumulated reality intact).

Accept the known cost: hours land on the wrong task when the user works on something other than what
Ari started. Mitigate by making the switch obvious and one tap at midday. **Do not** spread time
across tasks by inference — an estimated number in a client's inbox is exactly the failure mode the
whole product is built to avoid.

### Constraints the existing store imposes

- `CreateTask` rejects `plannedSeconds < 60` or `> 8h` (`:2154`). The morning card currently collects
  no duration at all — it needs one, or a sane default per task. Decide and state it.
- Max 20 open tasks (`MaxOpenTasks:2147`); `CreateTask` returns `null` past that. Handle the null.
- `ReadTasks` piggybacks a 30-day cleanup of closed rows (`:2210`). Anything the report needs beyond
  30 days must be persisted server-side.
- The goal day boundary is **05:00 local** (`GoalDay:1809`), not midnight. Tasks currently have no
  goal-day concept — `ReadTasks` takes a `dayStartUtc`. Line these up or the two halves will disagree
  about which day it is between midnight and 5am.

### Consumers that change when `daily_goals` becomes a view

Do not treat this as an isolated refactor. Five things read it:

1. `UserAgentHost.RefreshCheckInAsync:1534` — the scheduler's whole input.
2. `NarrativeGateway.cs:114` — `goalToday` / `carriedPlan` in the fact sheet.
3. `DailyStatsReporter.cs:118` — uploads goal + midday answer to the server.
4. `DailyDigestReporter.cs:142` — the digest's goal line.
5. **`AgentCoreController.SetMorningGoal:1820` writes a Phase-1b ground-truth label**
   (`target: "daily_goal"`), and `RecordMiddayAnswer:1871` writes another (`target: "goal_progress"`).
   These feed the ops console's per-user label-engagement card. Decide deliberately what a
   task-shaped morning check-in emits, rather than dropping the labels by accident.

`GetCarriedOverPlan:1840` is also worth reading before touching anything — the carry-forward rule
step 6 wants **already exists in pure, tested form** (`ResolveCarriedOverPlan:1850`). Extend it to
tasks rather than writing a new one.

---

## 7. Steps 4–10 — intent only

Deliberately not specified in detail. Step 3 will change what these should look like, and a spec
written now would be stale before it's read. What matters is that step 3 leaves the door open.

- **4. Propose, never an empty box.** Morning card arrives pre-filled from yesterday's unfinished
  tasks. `PrefillGoalText` already exists on `UserCheckInPrompt:19` and the carry-forward machinery
  is `ResolveCarriedOverPlan:1850`. Mostly re-pointing what's there at tasks.
- **5. One-tap done.** Midday/evening cards list today's tasks with measured time beside them.
  Depends entirely on the auto-start decision in §6 being real.
- **6. Carry-forward.** Unfinished work rolls to tomorrow. Say something once if an item has carried
  three days — plainly, not naggingly.
- **7. Evening closes the day.** Delete the "what's tomorrow's task?" text box (`BuildEndOfDay:324`).
  The founder stopped answering it, which is the clearest signal available.
- **8. Morning screen.** Full screen, yesterday's result and today's proposal. **Must be a glance,
  not a meeting** — the nightly email is already ignored, and a heavy morning screen will be learned
  away the same way.
- **9. Project/client tags.** Ask once per project, never per task. Needed only because a report has
  to say "3h 40m on the Nolan project".
- **10. The report.** Assembled from 3–9. Ari drafts, the user taps, then it sends. The drafting
  machinery exists (`narrativeService.ts`), as does delivery (Resend) and a grounded HTML builder
  (`digestEmailBuilder.ts`).

**Stop after step 7.** Yadin uses it himself for a week. The test is one question: *has Ari changed
what he actually did that day?* If closing the loop doesn't change that for him, 8–10 won't save it.

---

## 8. Verified vs. still unverified

`HANDOFF §9` listed three things read but not run. Two are now answered by reading the code:

| Claim | Status |
|---|---|
| Widget task UI and check-in would write to the same store | **Confirmed.** Both go LocalApi → `AgentCoreController` → `SqliteBrainStateStore`. One database, one table. |
| `DailyGoalsStore` consumers that break | **Confirmed — five, listed in §6.** The ground-truth labels are the non-obvious one. |
| Time attribution works end to end | **CONFIRMED on a live agent, 2026-08-06, on 1.2.127.** Probe task started 16:53:34, read at 17:04:39 (665s wall clock) → `active_seconds` 425, `focused_seconds` 425, `offtask_seconds` 0. Independent historical corroboration in the live DB: task id=3 ran a 210s window and recorded exactly 210s. Step 3 can lean on this. |

**Two things the verification also revealed, which step 5 must design around:**

- **425s of 665s is ~64%, and that is correct, not lossy.** `AttributeBucketToActiveTask:2365` returns
  early on away buckets, so the figure is *present-and-working* time, not wall clock. That is the
  right semantic for a number going to a client — but it means the time beside a task will always
  read lower than the clock on the wall, and **step 5's UI must not look broken because of it.** Say
  what the number means, or users will read it as undercounting and stop trusting it.
- **The feature has essentially never been used.** Task ids 1–3 are the entire production history —
  three tasks, all on 14–15 July, none since. Nothing in the field has ever exercised this path, so
  the §6 auto-start decision is carrying the full load with no usage evidence behind it.

**Also confirmed: §5a reproduced with timestamps** on the current release, not merely inferred:

```
2026-08-05 16:40:32  checkin_card_shown   kind=EndOfDay
2026-08-06 07:37:21  checkin_dismissed    kind=EndOfDay    ← dismissed the next morning
2026-08-06 07:37:24  checkin_offered      kind=Morning
```

Exactly the sequence the stale-card mechanism predicts. Build the expiry.

---

## 9. Environment and test bar

- **Baseline is RED and that's expected.** `Porta.Agent.Core.Tests` → 711 passed, **4 failed** as of
  2026-08-04 (`SECURITY_HANDOFF.md §4.8`). All four are restart-reconciliation scenarios, confirmed
  pre-existing. Do not treat green as the baseline; do not assume red means you broke something.
- **The other four suites are a real gate** — a failure there is yours: ControlCenter 80,
  Service 104, UserAgent 247, Runtime 6. (UserAgent is now 251 after step 1's four focus tests.)
- **`GuiHandleLeakStressTests` — RESOLVED 2026-08-06. Not flaky, and there is no widget leak.**
  Earlier text in this file called it a 1-in-5 flake. That was wrong. Measured: the widget open/close
  cycle leaks **zero** USER and GDI handles, flat at 10, 20, 40, 60 and 120 cycles — a real per-cycle
  leak scales, this does not. The failures were test harnesses starting an STA thread per test and
  never shutting the `Dispatcher` down, leaving WPF resources resident for the rest of the run; the
  stress test reads process-wide counts and attributed them to the widget. Fixed in `15c83ee`.
  Causally confirmed: without the shutdown 3/3 runs fail, with it 6/6 pass. **Neither threshold was
  relaxed** — this gate guards a defect that reached real users. Any new test that creates a window
  on its own STA thread must shut that dispatcher down, or this fires again.

- **THE VALIDATION GATE DOES NOT RUN THE TEST SUITES — found 2026-08-06.** `validation-gate.yml`
  runs `scripts/build.ps1` and `Porta.Agent.ValidationRunner`. There is **no `dotnet test` anywhere
  in the workflow.** So the 711/4 Core baseline, the 261 UserAgent suite, and the four suites this
  section calls "a real gate" are **local-only, and CI never runs them**. A genuine regression in any
  of the five reaches `main` green. Two consequences: never read a green check on GitHub as evidence
  the suites pass, and run them locally before every push. **Adding a `dotnet test` step to the
  workflow is the highest-value item on the maintenance list** — the whole "real gate" model in this
  brief was wrong until it exists.
- Control-plane tests are vitest (`vitest.config.ts`).
- **RELEASE BUILDS — corrected 2026-08-06, this cost a broken machine.**

  > `HANDOFF_Loop_And_Report.md §8` and the first draft of this brief both said releases are built
  > "only via `scripts/build-msi.ps1`". **That is wrong and following it will produce an MSI that
  > bricks the machine it is installed on.** The claim was copied forward without being checked.

  The real release script is **`build-1297.ps1` at the repo root** — one numbered script per release,
  so 1.2.128 means copying `build-1297.ps1` to `build-1298.ps1`. The difference is not cosmetic:

  | | publish flags |
  |---|---|
  | `build-1297.ps1` (real) | `PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true` |
  | `scripts/build-msi.ps1` (**trap**) | `PublishSingleFile=false` (`:185, :196, :207`) |

  With single-file off, publish emits hundreds of loose DLLs the MSI does not carry, so the installed
  service starts, fails to find its own assembly, times out twice, and Windows rolls the transaction
  back. The numbered script has a guard at `:60` that throws *"Do NOT ship this MSI"* on exactly this
  condition; `scripts/build-msi.ps1` has no such guard. It also checks payload SQLite against
  CVE-2025-6965 (`:120`) — another gate the unguarded script skips.

  **`scripts/build-msi.ps1` is a live trap and must be neutralised** — port the single-file flags and
  both guards into it, or make it fail immediately with a pointer to the numbered script. Do not
  leave it runnable.

  Installed layout for reference, so a healthy install is not mistaken for a gutted one: the binaries
  are **self-contained single-file bundles** — `ari.exe` ~101 MB, `Porta.UserAgent.exe` ~202 MB,
  `Porta.ControlCenter.exe` ~193 MB. **There is no `ari.dll`, and nine files plus locale folders is
  correct.** A low file count is not evidence of a damaged install.

- Version must be bumped in three `.csproj` files before building.
- **Auto-update reaches the whole fleet within ~6h of publishing a manifest** — a bad release reaches
  every user automatically. `f5e510b` exists because a manifest pointed at a missing release; don't
  repeat it.
- **Installing is Yadin's, always.** Claude Code builds the MSI and hands it over. No elevated
  commands, no service control, no installs on the live machine — a manual install over a running
  agent is its own hazard even with a correct MSI.

---

## 10. Hard rules — unchanged

- **The report never sends automatically.** Ari drafts, the user taps, then it goes.
- **No financial reporting.** Ari knows time and tasks, not money.
- **No website work** until the loop is proven on Yadin himself.
- **No new app features outside this plan.**
- **Security capped at half a day a week** unless something is actively exploitable.
- **Nothing rolls to the fleet without Yadin's explicit go.**
- House style: measure before changing; keep the old path as a fallback; small reversible commits;
  decision logic in pure I/O-free functions with a reason code for every refusal; never guess.

---

## 11. Open — Yadin's call, not the implementer's

**Decided 2026-08-07:**

- **Up to three tasks** on the morning card. One box, "+ add" for more, three is the ceiling.
- **Ari proposes the duration; the user taps, never types.** A sensible default per task plus quick
  chips — 1h / 2h / 3h — in the same style as the existing `UserCheckInChoices` rows. The morning
  card must never require a number to be typed. Reuse `MakeChip` / `HighlightChip`
  (`UserCheckInCardWindow.cs:390, :406`) so it matches the tracking-preference row already there.
  Remember `CreateTask` rejects anything under 60s or over 8h (`SqliteBrainStateStore.cs:2154`).

**Still open:**

- What ground-truth label a task-shaped morning check-in should emit (§6, consumer 5).
