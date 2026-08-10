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

## 0. The build waiting for Yadin — 1.2.133, 2026-08-09

```
C:\Users\hp\porta-agent-windows-x64\installer\bin\x64\Release\AriAgentInstaller.msi
SHA256  59548084679499702B522913AFFC7CBF643E9B66C572C10AE48EE6B30D9E8399
Size    178,020,352 bytes
Built from 4400adf (build-1303.ps1); payload exes all stamped 1.2.133+4400adf
```

Steps 1-7, the two fixes from the first real day on 1.2.131 (per-row ✕; the keyboard follows a box
that opens later), and the instrumented foreground retry from §4d. **Steps 3 and 4 are CONFIRMED
LIVE** — see §4c. 1.2.132 was installed and is fine to keep running; 1.2.133 only adds the retry
and its logging.

**Built, not installed and not released. Installing is Yadin's, always.** No tag, no GitHub release,
no manifest — nothing reaches the fleet.

Guards that passed: publish output self-contained (single-file), payload SQLite 3.53.3 against
CVE-2025-6965, and `ProductVersion` read back out of the MSI rather than trusted from the csproj.

Test gate: Core 730 passed / **4 failed** — the documented restart-reconciliation baseline
(`SECURITY_HANDOFF.md §4.8`), confirmed by name, **that red is expected**. UserAgent 358 (+3
skipped), Service 117, ControlCenter 80 (+7 skipped), Runtime 6 — all green. CI ran none of these;
`validation-gate.yml` still has no `dotnet test`.

Each change has its own log line so a problem stays attributable:

| Change | What to look for in `user-agent.log` |
|---|---|
| Step 3 — morning check-in creates real tasks | `checkin_morning_task_created`, `checkin_morning_task_started` |
| Steps 4-7 — the closed loop | `checkin_task_outcome kind=… action=done\|abandon`, `checkin_eod_recorded … dropped=N` |
| Task list / goal row share the 05:00 boundary | (no line; visible as closed tasks staying on the list before 5am) |
| One ground-truth label per task | `checkin_morning_recorded … tasks=N` |
| Narrative reason codes one layer deeper | `checkin_narrative … reason=local_api_*` |
| Narrative route's own 12s budget | `checkin_narrative … used=True` far more often |
| Why a keyboard grab was refused | `checkin_card_focus … reason=refused_*:err=…,fg=…,locktimeout_ms=…` |
| Focus follows a box opened later | a SECOND `checkin_card_focus` line after a `+ add` |

**What is NOT in it:** nothing new for step 2. The stale-card expiry has still never run.

---

## 2. Where things stand right now

**Released to Yadin's machine: 1.2.133** (ops console confirms `1.2.133.0`). Previously 1.2.129: Installed via `scripts\install.ps1`, which copies payload
exes directly and never touches the MSI — so **the binaries are 1.2.129 while the registry still
reads 1.2.127.** Do not read the registry as the version. Nothing has gone to the fleet: no tag, no
release, no manifest. Pushed through `8596d5e`.

| Step | State |
|---|---|
| 1. Typing bug | **Done and confirmed live**, twice — `foreground=True` on 2026-08-07 and again 2026-08-08, typing worked with no click. |
| 2. Stale card + timing | **UNTESTED — not done.** The code and 15 tests exist, but `checkin_card_expired` has **never fired, on any date**. No EOD card has been shown since 2026-08-06, so the rule has never had an opportunity to run. Do not report this as working until a card is left open overnight and the next morning's card displaces it. |
| Narrative async | **Done and confirmed live.** Card renders instantly; on 2026-08-08 the line upgraded it 2.4s later with `applied=True`. |
| Narrative diagnostics | **Built and it worked.** One reason code in the field (2026-08-07 midday) named the cause — see §3a. |
| Narrative root cause | **FOUND 2026-08-08 and fixed.** A 3s HTTP client timeout, below all three documented budgets. See §3a. |
| 3. Merge goals and tasks | **CONFIRMED LIVE 2026-08-09.** Three tasks created, first auto-started. See §4c. |
| 4. Propose, never an empty box | **CONFIRMED LIVE 2026-08-09.** Card arrived pre-filled; chips used. See §4c. |
| 5. One-tap done | **CONFIRMED LIVE 2026-08-09 17:08.** Per-task time + Done/Not yet; the midday answer derived correctly (`answer=partly` from 1 of 2 ticked). |
| 6. Carry-forward | **Built, not yet confirmed live.** In 1.2.132. Costs nothing — open tasks never left. Untested: no task has yet survived a day. |
| 7. Evening closes the day | **CONFIRMED LIVE 2026-08-09 18:41.** "Nothing left open" + Good night rendered. NOTE: the card was **dismissed**, not completed — see §4e. |

Test gate before the 1.2.129 build: ControlCenter 80, Runtime 6, Service 117, UserAgent 276 (+3
skipped) — all green. Core 711 passed / 4 failed, exactly the documented restart-reconciliation
baseline (`SECURITY_HANDOFF.md §4.8`) — **that red is expected, do not treat green as the baseline.**

---

## 3a. The narrative failure — SOLVED 2026-08-08

**Four rounds of investigation looked at the server. The defect was a fourth budget on the agent,
one layer below the three everyone was reading.**

`LocalApiClient` used a single `HttpClient` with `Timeout = 3s` for every local call — including
`/v1/narrative`, the one route that does not end at local SQLite but is forwarded to the control
plane and on to Opus. The documented chain was 10s (host token) → 9s (`NarrativeGateway.CallBudget`)
→ 8s (server `REQUEST_TIMEOUT_MS`). **The 3s client timeout sat below all three and always fired
first.** A line could only ever land when the local API, the network, Render and Opus writing up to
250 tokens all finished inside three seconds.

Two field measurements, both landing exactly there:

| When | Elapsed |
|---|---|
| 2026-08-06 morning (brief §5b) | 3.03s |
| 2026-08-07 midday (`local_api_timeout` reason code) | 2.889s |

It explains every symptom that killed the other hypotheses: uniform failure across kinds, no
correlation with the overnight gap, retries that sometimes win (each draw independent), and the
~7-in-10 rate.

**Correction to what was believed on 2026-08-07:** `local_api_no_response` was read as "nothing ever
reached the control plane". That was inferred from a code comment written for a different
assumption. Under a client-side timeout the request **is** sent and forwarded — the agent just stops
waiting. **Render is likely logging successful narrative calls at the exact timestamps the agent
logged `used=False`.** Checking that is a free confirmation and nobody has done it yet.

**The fix:** the narrative route gets its own `HttpClient` at 12s, above the host's 10s ceiling so it
can never be the binding constraint again. Every other local call keeps 3s, which is correct for a
SQLite read. Waiting costs the card nothing — it renders instantly and the line upgrades it in
place. A test pins the two budgets apart.

**Also fixed, and it is what would have found this in one morning:** `GetNarrativeAsync` ended in
`catch { return null; }`, so the host reported timeouts, HTTP errors, transport failures and an
unconfigured agent as the single code `local_api_no_response`. It now returns a specific reason:
`local_api_timeout:12s`, `local_api_http_NNN`, `local_api_transport:<Type>`,
`local_api_not_configured`, `local_api_empty_body`.

---

## 3. Tomorrow morning — what Yadin will report, and what to do with it

He opens his PC as normal and reads three things out of `user-agent.log`. Step 3 is already built
and pushed, but it can run alongside — it is not in this build.

> **Superseded 2026-08-08.** The confirmation run happened on the morning of 08-08 and 1.2.129 came
> back clean on focus, the async line and the reason codes. Yadin then released the hold: step 3,
> the deeper reason codes and the narrative timeout fix ship together, because they touch different
> code and each has its own log line, so a problem is still attributable.

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

## 4a. Step 3 as built — 2026-08-07

Three commits: `c3e48e0` (day boundary), `284da6e` (the merge), `cd24f89` (per-task labels).
Pushed to `main`. **Not in a build, not released, nothing to the fleet.**

**What the morning card does now.** It collects the day's task list instead of one sentence: one
row to start, `+ add` for the second and third, and 1h / 2h / 3h chips on each row with 1h
pre-selected. A card answered in three seconds without touching a chip still produces legal tasks.
On accept, each row becomes a real task and **the first one is started**, so
`AttributeBucketToActiveTask` has something to credit — without that every task reads 0m and step 5
shows a loop that measures nothing.

**How `daily_goals` became a view.** It is still written, but the goal sentence is now *derived*
from the task titles (`"Pricing page; Client revisions"`) rather than authored separately. That
keeps all five readers in §4 working untouched. The alternative — computing goal text from `tasks`
at read time — would have changed read semantics for four consumers at once and could make the
scheduler think a morning goal exists because the widget created a task. Derive-on-write is the
reversible version.

**Decision logic is pure and reason-coded** in `UserCheckInTaskPlanner`: the three-task ceiling
(`over_max_tasks`), dedupe within one list (`duplicate_in_list`), the duration clamp `CreateTask`
demands, and the goal-text projection. Re-confirming work that is still open is **accepted but not
duplicated** — it starts the existing task instead. That is the shape step 6 needs.

**The day boundary was wrong and is fixed.** `GetTodayTasks` started at local midnight while
`GoalDay` starts at 05:00, so between midnight and 5am the two halves disagreed about which day it
was. `AgentCoreController.GoalDayStartLocal` is now the single boundary. Only affects which
*closed* tasks show; the digest reads the same window and fires at 23:30 local, so it is unmoved.

**The last open item is now closed.** Yadin decided (2026-08-07): **one ground-truth label per
task**, not one per morning — naming three things is three commitments. `SetMorningGoal` takes an
optional `taskCount` defaulting to 1, so nothing else changes. **Expect the ops console's per-user
label counts to rise; they are not comparable to the history before this change.**

**What has NOT been verified:** none of this has run on a live agent. The whole path is untested
against a real `LocalApi`, and per §8 of the brief the task feature has essentially never been used
in the field (three tasks ever, all in July). First live morning card is the real test.

---

## 4b. Steps 4-7 as built — 2026-08-08 (`aae5609`)

**This is the checkpoint. Do not start step 8.** See §8: Yadin uses it for a week and the test is one
question — *has Ari changed what he actually did that day?*

- **4. Propose.** The morning card arrives pre-filled from work that is still open, oldest first.
  An open task **outranks last night's carried plan** — a real piece of unfinished work is a better
  proposal than a note, and it is what the measured hours are attached to. No open tasks falls back
  to the carried plan, then to the blank ask; both unchanged.
- **5. One-tap done.** Midday lists today's tasks with the measured time and a Done / Not yet per
  row. The yes/partly/not_yet answer is **derived from what was ticked**, not asked separately, so
  `daily_goals` stays a view over the tasks and both the ground-truth label and the `MiddayRecorded`
  gate keep working untouched.
- **6. Carry-forward, which costs nothing.** `ReadTasks` returns pending/active rows regardless of
  age, so open work never left. **"Keep" writes nothing — the no-op IS the carry.** Unfinished work
  therefore cannot be lost by a failed write, because there is no write. Three days carried earns
  one plain sentence, once.
- **7. Evening closes the day.** The "what's tomorrow's task?" box is gone. What is left: the day's
  score, finished work shown but not asked about, and one tap per unfinished item — keep or drop.

**The measured-time warning from brief §8 is handled but needs watching.** The number beside each
task is present-and-working time (~64% of wall clock in the one live measurement), because time away
credits nothing. The card says "Time counted while you were at the machine." If Yadin still reads it
as undercounting, that copy is the thing to change — not the number.

**Not verified:** none of steps 4-7 has run on a live agent, and the midday/evening boards have never
been seen on a real screen. Card height in particular is untested with three tasks plus chips.

---

## 4c. First real day on 1.2.131 — 2026-08-09

**Steps 3 and 4 confirmed live.** The morning card arrived pre-filled, three tasks were created with
three different durations (so the chips were used, and nothing was typed as a number), and the first
task auto-started:

```
14:11:16  checkin_offered kind=Morning
14:11:16  checkin_card_focus kind=Morning foreground=True target=input
14:14:06  checkin_morning_recorded ok=True pref=remind_me tasks=3
14:14:06  checkin_morning_task_created id=5 planned_s=7200    (2h)
14:14:06  checkin_morning_task_created id=6 planned_s=10800   (3h)
14:14:06  checkin_morning_task_created id=7 planned_s=3600    (1h)
14:14:06  checkin_morning_task_started id=5 ok=True
```

`checkin_narrative_upgrade applied=False` on the same card is **not a defect** — the line arrived
2.5s in, by which point the user had started answering, and the rule is never to rewrite copy under
someone mid-answer.

**Why the card was late.** It fired at 14:11 for a 1pm start, and neither cause is a regression:
the user agent was not running at all until 14:03 (watchdog relaunch, `stale_heartbeat`), and the
Part 1c settle gate then held it three times waiting for 3 minutes of continuous activity. The gate
returns at `UserAgentHost.cs:1574`, well before any steps 3-7 code at :1603.

**Worth knowing: the test suite writes into the production log.** `UserAgentFileLog` has no path
override in tests, so bursts of dozens of `checkin_card_focus` lines in `user-agent.log` are test
runs, not field events (2026-08-07 06:48, 2026-08-08 09:16 and 19:21). It briefly had test output
being read as field data. Worth fixing — it corrupts exactly this kind of investigation.

---

## 4d. The keyboard, and why Ari cannot always have it — 2026-08-09

**Measured on the machine, before writing any fix:**

```
SPI_GETFOREGROUNDLOCKTIMEOUT              = 2147483647 ms  (~24.8 days)
HKCU\Control Panel\Desktop\ForegroundLockTimeout = 200000
```

The effective foreground lock is pinned at `Int32.MaxValue`. While it holds, Windows **refuses**
`SetForegroundWindow` by design and flashes the taskbar button instead. A `Topmost` window still
draws on top — so the card is visible, looks focused, and the keystrokes go to whatever is actually
in front. That is the `foreground=False` on the evening card of 2026-08-08, and it is how a whole
task got typed into another application unnoticed.

`AttachThreadInput` bypasses the lock only when the attach succeeds, which is why the morning card
usually wins and the evening card sometimes does not.

**What was NOT done, deliberately.** Overriding the lock means `SPI_SETFOREGROUNDLOCKTIMEOUT`, a
machine-wide system-settings change affecting every application — not Ari's to make. The other
common workaround, injecting a synthetic key to become the last input receiver, would fight the user
for a keyboard they are actively using. Yadin also ruled out the aggressive reading of "the keyboard
belongs to Ari" (re-grabbing whenever focus is lost): **do not trap the user.**

**Ruled out, 2026-08-09, before any further fix.** Ari is not doing this to itself. Every `user32`
entry point the agent imports was enumerated: `SystemParametersInfo` appears exactly once and it is
the GET. No `SPI_SET`, no `LockSetForegroundWindow`. `focus_mode_enabled` is inert agent-side — it
is not in `InterruptToastActions`, so applying it writes one log line and nothing else. Nothing is
toggling the value per-app either: it read `2147483647` across 40 minutes and across different
foreground applications while HKCU still said `200000`, so something in this Windows session set it
at runtime without persisting. **What set it is still unknown** — a reboot would say whether it
survives logon. Integrity is not the blocker: `claude`, `chrome` and `Porta.UserAgent` are all
readable from a non-elevated shell, so UIPI is not refusing the attach.

**Still unknown, and how 1.2.133 answers it.** Windows lists eight conditions for granting
foreground; several are transient (a menu open, a window mid-activation, the foreground app between
input events) and several are not. Which one refused the 08-08 evening card is not derivable from
the return value. So the retry is built as the experiment: two extra attempts at 120ms and 350ms,
each logging its `try=` and reason. **`try=1 foreground=True` means timing and the retry earned its
place; three identical refusals mean it is structural.** Bounded by a test — at most three attempts,
none over 500ms, under a second total — and abandoned the instant the user starts typing, per
Yadin's rule: never fight for a keyboard someone else is using.

**What was done.** Every refusal now carries a reason (`refused_attached:err=…,fg=…,locktimeout_ms=…`
— process NAME only, never a window title), and when the keyboard is refused the card **says so**
rather than looking focused. The harm was never that Ari lacked the keyboard; it was that there was
no way to tell.

---

## 4e. The dropped-task question — answered 2026-08-10

**Asked:** last night's evening card said nothing was left open, `writting` and `working on apollo`
were both dropped at 0m, and this morning's card appeared to pre-fill "continue writting". Should a
dropped task come back as a proposal?

**No — and it did not. VERIFIED against the live database, not reasoned.**

```
tasks
6  writting            abandoned  20s   ended 2026-08-09T13:42
7  working on apollo   abandoned   0s   ended 2026-08-09T10:28
9  continue writting   pending      0s  created 2026-08-10T07:41   <- a NEW task
```

Two independent facts, both measured:

1. **`Propose` filters on `IsOpen` (pending/active).** An abandoned task cannot be proposed, and
   `Propose_SkipsFinishedWork` pins it. Both were abandoned hours before the morning card.
2. **The other possible source was empty too.** The pre-fill's only other input is
   `PrefillGoalText` ← `GetCarriedOverPlan()` ← **yesterday's `daily_goals.plan_tomorrow`**. The
   2026-08-09 row has `plan_tomorrow` empty and `eod_at_utc` empty, because the evening card was
   **dismissed** rather than completed (`checkin_dismissed kind=EndOfDay` at 14:42 UTC, no
   `checkin_eod_recorded`).

So both pre-fill sources were empty and the box was blank. Task id=9 is a **new** task typed that
morning — note the second `checkin_card_focus … reason=ok_direct` at 07:40:41, which is the "+ add"
row being opened by hand.

**THE REAL GAP, and it is worth fixing.** *Inferred, not verified:* what almost certainly suggested
"continue writting" was **the AI line**, which landed on that card (`used=True`, `applied=True`).
And the AI is not bound by the rule the pre-fill obeys — **verified by code**: `NarrativeGateway`
sends the goal ROW only (`goalToday`, `carriedPlan`), and `DailyStatsReporter` carries goal + midday
answer. **No task outcome ever reaches the model.** Yesterday's goal sentence was
`"keep improving the system; writting; working on apollo"` — the projection written in the morning,
never updated when two of the three were abandoned.

So Ari will never *pre-fill* dropped work, but it can still *talk you into* it, because the model has
never been told what happened. This is exactly the gap `Loop_And_Report_Build_Brief.md §5c` flagged
("yesterday's tasks genuinely are missing") and it is now costing something real. Fixing it means
sending task outcomes with the narrative payload, not just the morning sentence.

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
