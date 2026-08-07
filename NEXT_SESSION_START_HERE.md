# Start here — session handover, 2026-08-07 evening

**You are picking up mid-project.** Read this file first, then in this order:

1. `HANDOFF_Loop_And_Report.md` — the strategy and why
2. `Ari_Loop_And_Report_Plan.md` — the ten-step work queue
3. `Loop_And_Report_Build_Brief.md` — corrected file paths, pinned line numbers, locked decisions

Where the brief and the first two disagree about **code**, the brief wins. Where they disagree about
**intent**, the first two win. This file is the current state on top of all three.

---

## 1. The goal, in one paragraph

Ari is changing from *a thing that shows you your day* into *a thing that does a job for you*.
Today it asks the user questions and hands back facts about themselves — effort in, information out,
which nobody pays for. After this work: the user does their work, and Ari writes the report that
**someone else** is waiting for — a client, a boss, a co-founder — which the user approves with one
tap. The missing mechanism is a closed loop: Ari asks what you'll do each morning, then behaves as if
it never asked. **A commitment nobody follows up on is a note, not a commitment.**

Yadin is the founder and the only real user. He is not a developer — **explain findings in plain
language and lead with what it means, not how you found it.**

---

## 2. Where things stand right now

**Released to Yadin's machine: 1.2.129.** Installed via `scripts\install.ps1`, which copies payload
exes directly and never touches the MSI — so **the binaries are 1.2.129 while the registry still
reads 1.2.127.** Do not read the registry as the version. Nothing has gone to the fleet: no tag, no
release, no manifest. Pushed through `8596d5e`.

| Step | State |
|---|---|
| 1. Typing bug | **Done and confirmed live** on the morning of 2026-08-07 — `foreground=True` in the log, typing worked with no click. |
| 2. Stale card + timing | **Built, not yet confirmed.** `UserCheckInStalenessPolicy`, goal-day rollover rule, 15 tests. |
| Narrative async | **Built and working.** Card now shows ~3s before the line is even attempted. |
| Narrative diagnostics | **Built, awaiting first data.** Reason codes ride back to `user-agent.log`. |
| 3. Merge goals and tasks | **Not started. This is your job.** |

Test gate before the 1.2.129 build: ControlCenter 80, Runtime 6, Service 117, UserAgent 276 (+3
skipped) — all green. Core 711 passed / 4 failed, exactly the documented restart-reconciliation
baseline (`SECURITY_HANDOFF.md §4.8`) — **that red is expected, do not treat green as the baseline.**

---

## 3. Tomorrow morning — what Yadin will report, and what to do with it

He opens his PC as normal and reads three things out of `user-agent.log`. **Do not start step 3
work in a way that blocks on this; it can run alongside.**

### a. `checkin_card_focus … foreground=True`

Step 1 still working. If this reads `foreground=False` and he had to click, step 1 regressed —
stop and fix before anything else.

### b. `checkin_narrative kind=morning used=False reason=<code>` ← **the important one**

This ends a two-day investigation. The morning line has been failing ~7 times in 10 and nobody knew
why. The reason code says exactly where it stops:

| reason | meaning |
|---|---|
| `http_200_empty_line` | The control plane ran and declined. **Now go read the Render logs** — the server records which of its five reasons applied (`narrativeService.ts`: unknown account, daily cap, missing key, model declined, unusable reply, model call failed). |
| `http_401` | Device auth rejected it. Was ruled out on 08-07, so this would be new. |
| `client_disabled:…` | The agent never tried — no backend URL configured. |
| `gateway_budget_exceeded:9s` | A genuine timeout after all. |
| `local_api_no_response` / `transport:…` | It died before leaving the machine. |

If `used=True`, the line landed — check whether `checkin_narrative_upgrade … applied=True` also
appears, which means it reached the screen.

### c. `checkin_card_expired kind=EndOfDay … reason=stale_goal_day_rollover`

Step 2 working. **Absence is not failure** — this only fires if last night's card was still open.
If he dismissed it, tomorrow just shows the morning card normally.

---

## 4. Step 3 — merge goals and tasks. THE FOUNDATION.

Everything after this depends on it. Read brief §6 in full before writing code; the summary:

**What changes:** `HandleCheckInResponseAsync` (`UserAgentHost.cs:1639`) stops calling
`SetMorningGoalAsync` alone and starts **creating real tasks**. `daily_goals` becomes a view over
`tasks` instead of a parallel record.

**What does not change:** the store. Both halves already live in `SqliteBrainStateStore.cs` behind
one `IBrainStateStore`, in the same database. Task methods at `:2145–2350`, goal methods at
`:2387–2510`. Today `UserDesktopWidgetPresenter.cs:1142` is the **only** caller of the task API in
the entire product — that is the whole defect.

### Locked decisions — do not reopen

- **Up to three tasks.** One box, "+ add" for more, three is the ceiling.
- **Ari proposes the duration; the user taps, never types.** A sensible default plus quick chips —
  1h / 2h / 3h — reusing `MakeChip` / `HighlightChip` (`UserCheckInCardWindow.cs:390, :406`) so it
  matches the tracking-preference row already on the card. `CreateTask` rejects under 60s or over 8h
  (`:2154`). **The morning card must never require a number to be typed.**
- **Auto-start the first task** when the morning list is accepted. `AttributeBucketToActiveTask:2359`
  only credits time to a task in `state = 'active'`, so without this every task reads 0m and step 5
  shows nothing but zeros. This keeps the existing one-active-at-a-time invariant untouched —
  `ActOnTask(id, "start")` already returns the previous active task to `pending` with its accumulated
  time intact (`:2267`). **Do not spread time across tasks by inference** — an estimated number in a
  client's inbox is exactly what this product exists to avoid.

### Constraints that will bite

- Max 20 open tasks; `CreateTask` returns `null` past that — handle the null.
- `ReadTasks` piggybacks a 30-day cleanup of closed rows (`:2210`). Anything the report needs beyond
  30 days must live server-side.
- **The goal day boundary is 05:00 local** (`AgentCoreController.GoalDay:1809`), not midnight. Tasks
  have no goal-day concept — `ReadTasks` takes a `dayStartUtc`. Line these up or the two halves will
  disagree about which day it is between midnight and 5am.

### Five consumers of `daily_goals` that change

Not an isolated refactor:

1. `UserAgentHost.RefreshCheckInAsync:1534` — the scheduler's entire input.
2. `NarrativeGateway.cs:114` — `goalToday` / `carriedPlan` in the AI fact sheet.
3. `DailyStatsReporter.cs:118` — uploads goal + midday answer to the server every 2h.
4. `DailyDigestReporter.cs:142` — the nightly email's goal line.
5. **`AgentCoreController.SetMorningGoal:1820` writes a ground-truth label** (`target: "daily_goal"`),
   and `RecordMiddayAnswer:1871` writes another. These feed the ops console. **Still an open question
   for Yadin: what should a task-shaped morning check-in emit?** Ask, don't assume.

`GetCarriedOverPlan:1840` / `ResolveCarriedOverPlan:1850` already implement carry-forward in pure,
tested form. **Extend them to tasks rather than writing something new.**

---

## 5. Open items, ranked

1. **Render payment is failing.** Yadin has been told. If it lapses the whole control plane goes
   down — no emails, no AI lines. Not your fix, but flag it if you see it again.
2. **CI does not run the tests.** `validation-gate.yml` runs `scripts/build.ps1` and
   `Porta.Agent.ValidationRunner` — there is **no `dotnet test` anywhere**. Every suite is local-only
   and a real regression reaches `main` green. Adding a `dotnet test` step (allowing the 4 known Core
   failures **by name**) is the highest-value maintenance item.
3. The narrative failure itself, once tomorrow's reason code names it.
4. Ground-truth label decision for step 3.

---

## 6. Process rules — non-negotiable, all learned the hard way

- **Installing is Yadin's, always.** Build the MSI, hand over the path, stop. No elevated commands,
  no service control, no installs. A manual install broke his machine on 08-06.
- **Release builds use the numbered `build-<version>.ps1` at the repo root.** `scripts/build-msi.ps1`
  is a hard-fail stub — it published with `PublishSingleFile=false` and produced an MSI that bricked
  a machine. Seven documents used to point at it; all now corrected.
- **Name every live-data write before making it, and wait for the go.**
- **Mark every claim `verified` (measured it) or `inferred` (reasoned to it).** Two false alarms on
  08-06 were inferences stated as findings, and they cost an afternoon.
- **When a hypothesis is falsified, revert the change that was justified by it.** This has been done
  four times and it is the reason the codebase is still clean.
- Nothing reaches the fleet without Yadin's explicit go. Auto-update delivers a bad release to every
  user within ~6h.
- Any new test that creates a window on its own STA thread **must shut its dispatcher down**, or
  `GuiHandleLeakStressTests` fires spuriously.

---

## 7. Already falsified — do not re-propose these

Four rounds of investigation went into the narrative failure. All measured, all dead:

| Hypothesis | How it died |
|---|---|
| `daily_stats` isn't uploaded without the digest | `DailyStatsReporter` is an independent 2-hourly `BackgroundService`; rows exist |
| Render cold start | `/health` 199ms after 5.5h idle; two calls 119s apart **both** failed |
| The 8s Opus timeout | The call fails in **~3.0s** against 10s/9s/8s budgets |
| Device auth 401 | `LastActionSuccessUtc` shows authenticated POSTs succeeding under `enforce` |
| Payload validation / route difference | Kinds and `dateLocal` match; `/narrative`, `/daily-stats` and `/action-events` carry identical middleware |
| A widget GDI leak | Zero handles leaked, flat over 120 cycles — it was test harnesses not shutting down dispatchers |

**Do not guess a fifth time.** The instrument is built; read what it says.

---

## 8. The checkpoint

**Stop after step 7.** Yadin then uses it himself for a week. The test is one question: *has Ari
changed what he actually did that day?* If closing the loop doesn't change that for him, steps 8–10
won't save it. Steps 8, 9 and 10 are deliberately unspecified until then.

**Hard rules throughout:** the report never sends automatically; no financial reporting; no website
work; no features outside the plan; security capped at half a day a week.
