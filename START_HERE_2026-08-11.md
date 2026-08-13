# Start here — new session, 2026-08-11

You are picking up mid-project. Read this file first. It is the current state on top of the
four existing documents, and where it disagrees with them about **today**, this file wins.

Then read, in this order:

1. `HANDOFF_Loop_And_Report.md` — the strategy and why
2. `Ari_Loop_And_Report_Plan.md` — the ten-step queue
3. `Loop_And_Report_Build_Brief.md` — corrected paths, locked decisions, falsified theories
4. `NEXT_SESSION_START_HERE.md` — the previous session's state (accurate up to step 8; does not
   contain today's design work or the caret regression)

---

## 1. Who is who

Yadin is the founder and the only real user. **He is not a developer.** Explain findings in plain
language and lead with what it means, not how you found it. He has said "you talk too much" more
than once — lead with the answer.

There is a second assistant (Cowork) sitting between you and Yadin. It reads your reports, explains
them to him, drafts his replies, and independently verifies your claims against the repo. Expect
your numbers to be checked. That is working as intended and has already caught two errors today.

**Mark every claim `verified` (you measured it) or `inferred` (you reasoned to it).** This is not
a formality. Inferences stated as findings have cost real time on this project.

---

## 2. What Ari is

A Windows desktop app that watches how you work and checks in through the day. It is changing from
*a thing that shows you your day* into *a thing that does a job for you*: you do the work, Ari
writes the report a client or boss is waiting for, and you approve it with one tap.

The evidence for the change: 18 installs ever, 1–2 active users, and the founder himself said it
never once changed what he did that day. The missing mechanism was a closed loop — Ari asked what
you'd do each morning, then behaved as if it never asked.

---

## 3. Where the project is

Steps 1–8 and 9a are built, plus the database work in §13–§15.

> ## DO NOT WRITE A VERSION NUMBER INTO THIS FILE FROM MEMORY
>
> This file has been wrong about the installed version **five times in three days**, in both
> directions. Every one of them was fixed in one second by running:
>
> ```powershell
> (Get-Item 'C:\Program Files\Ari\ari.exe').VersionInfo.ProductVersion
> ```
>
> **Read the exes. Every time.** Never infer the installed version from what Yadin said he would
> do, from a build script, from a commit message, or from this file. The registry lags several
> releases and must not be used.

**Installed: `1.2.150+c966dc5`** — read off all three exes in `C:\Program Files\Ari` on
**2026-08-13** *(verified)*. That build is commit `c966dc5`, step 10 phase B. The line above said
1.2.148 until 2026-08-13; that was the sixth time this field has been wrong. Read the exes.

**1.2.149 is the current build** — 2026-08-13, and it exists because `69beaa5` changed card code
while the csproj still said 1.2.148, the number already on the machine. It carries the three
changes Yadin approved and had **not** been running: projects shown on midday and evening rows,
a visible **Add** button on the new-project box, and the honest evening copy.

> **1.2.149 handed over 2026-08-13** — his machine only. No tag, no release, no manifest, nothing
> to the fleet.
> `installer\bin\x64\Release\AriAgentInstaller.msi` · 178,110,464 bytes ·
> ProductVersion read back out of the MSI · payload exes stamped `1.2.149+428f400` ·
> SHA256 `627E700B58265DAABF568123C4761B272BB16F53A5CD49A1FC44C9135F3125A3`.
> Gate before building: Core 766/770 with the four documented failures allowed by name, UserAgent
> 476, Service 134, ControlCenter 80, Runtime 6.
>
> **Owed once he installs it:** he has never actually used the Add button or seen projects on the
> midday and evening rows, because no build carrying them was ever on the machine.

> **1.2.150 handed over 2026-08-13** — his machine only. No tag, no release, no manifest.
> `installer\bin\x64\Release\AriAgentInstaller.msi` · 178,118,656 bytes ·
> ProductVersion read back out of the MSI · payload exes stamped `1.2.150+c966dc5` ·
> SHA256 `AD5343D6EA4F6D8160C4ACB88C38A2399D119B9705D6FC7FE68F634720B07ECB`.
> Gate: Core 790/794 with the four documented failures allowed by name, UserAgent 476, Service 132,
> ControlCenter 80, Runtime 6.
>
> It carries **step 10 phases A and B** — client-facing labels, the project's client-facing name,
> the user-side rate (schema v28), and the **Reports page** under Settings. Nothing in it sends
> anything: there is no Send button, no recipient field and no email anywhere. See
> `Step10_Report_BuildBrief.md`.
>
> **The first thing to look at is `unattributed_hours` on the Reports page.** The report counts the
> app-usage rollup, which skips buckets with no identifiable application, so it can read lower than
> the widget for the same task. How much lower is **unmeasured** — reading the live database needs
> elevation. Each line prints the gap. If it is large, that is a finding about the capture pipeline,
> not the report.

The version history, short: 1.2.137 the caret fix; 1.2.138 the four-card timing, close-out card and
persisted "Tomorrow"; 1.2.139 the day-adds-up and label-survival fixes; 1.2.140 `dayTimeline`;
1.2.142 the test-suite cleanups; 1.2.143 step 9a; 1.2.144 the `primitive_buckets` fix; 1.2.145 the
hourly app rollup (§14) and the backup constants (§15); 1.2.148 the preview harness.

**Anything before 1.2.140 sends NO `dayTimeline`** — the digest's THE DAY section cannot appear
until 1.2.140 or later is installed. That floor is long since passed.

| Step | State |
|---|---|
| 1. Typing bug — card takes the keyboard | **Fix SHIPPED in 1.2.137, installed 2026-08-11.** See §5. |
| 2. Stale card expires overnight | Still never run. Needs an evening card left open overnight. |
| 3. Morning check-in creates real tasks | Confirmed live |
| 4. Card arrives pre-filled | Confirmed live |
| 5. One-tap done, hours beside each task | **Confirmed live** 2026-08-09 17:08 |
| 6. Carry-forward | **Still never run.** Every task so far has been finished or dropped the same day, so nothing has ever needed to carry. Needs a task left genuinely open overnight. |
| 7. Evening closes the day | **Confirmed live** 2026-08-09 18:41 |
| 8. The morning screen | Built and installed. First real sighting 2026-08-11. |
| 9a. Projects exist, locally | **BUILT 2026-08-12, committed `861b4c5`, first shipped in 1.2.143.** See §12. Projects also show on midday and evening rows as of `69beaa5` / 1.2.149. |
| 9b, 9c | **RETIRED as phase labels.** They existed to defer one decision — whether client names leave the machine — and choosing "Ari emails a report to a client" made it. See `Step10_Report_Spec.md` §2. |
| 10. The report | **Specified and approved, not built.** `Step10_Report_Spec.md` is fully answered as of 2026-08-13 — field list approved, §12.0 names the third-party-data line. Build brief: `Step10_Report_BuildBrief.md`. Nothing is built yet. |

Step 10 remains gated on the week of real use — *has Ari changed what Yadin actually did that
day?* He knows, and started 9a anyway because it mostly adds information rather than changing the
loop. **Remind him once if step 10 comes up; do not repeat it.** On 2026-08-13 he said for the
first time that he had nothing to complain about, which is when that week can actually start.

---

## 4. What happened on 2026-08-10 and 11

Four pieces of work, all shipped to his machine except the last.

**a. Task outcomes reach the AI.** The narrative payload now carries `title`, `state`,
`plannedSeconds`, `actualSeconds` per task, looking back to yesterday's 05:00 boundary, capped at
10. Before this, the model only ever heard the morning plan — so Ari suggested back work that had
been dropped. The control-plane half validates the titles and renders them into the fact sheet with
an explicit instruction not to propose dropped work. **Deployed to Render and live.**

`DailyStatsReporter` was deliberately left alone. `daily_stats` has no `tasks` column and the
digest's task array is rendered into the email then discarded, so **nothing persists task outcomes
server-side today.** Step 10 will need that, and it is a separate decision because it creates a
durable server-side record of things the user typed. Do not fold it into other work.

**b. Step 8 — the morning screen.** Not full screen. Yadin overruled the plan on this, correctly:
he answers the card every day, so attention was never the failure, and a full-screen window that
can't take the keyboard is far worse than a small one that can't. It is the same card, taller
(~550 DIPs against a 672-DIP work area).

**c. All three cards share one renderer.** `AddTaskRow` is used by morning, midday and evening so
they cannot drift. One deliberate divergence, via `fadeUnfinished`: yesterday's unfinished work
fades on the morning card because it is history; live unfinished work does **not** fade on midday
and evening because it is what the user is about to act on.

**d. The design values.** These were specified by Yadin and are now enforced by tests. Do not
change them without asking.

```
Card       bg #14171C · border 0.5px #2A3038 · radius 12 · padding 16
Headline   16px #F2F4F7
AI line    12px #8B929E · line height 1.5
Section    10px #6F7885 uppercase + 0.5px #2A3038 hairline (no letter-spacing — WPF cannot)
Task row   check 13px #3FBF7F · title 12px #DFE3E9 live / #78808C faded
           bar 62x5 radius 3, track #232931, fill #3FBF7F finished / #5A626D unfinished, 2px floor
           number 42px wide, right aligned, tabular, 12px, same colour as its title
Input      radius 7 · padding 8/10 · 13px #F2F4F7 · border 0.5px #2A3038
           refused: 1px #8B929E plus the hint above the row
Chip       radius 5 · 11px · selected fill #2E353E text #F2F4F7 · unselected border 0.5px #2A3038
Button     full width · radius 7 · 13px · fill #3FBF7F · text #0E2A1C
Hint/meta  11px #6F7885
```

**Green means exactly three things: the finished check, a finished bar, the primary button.**
Nothing else. It drifted to seven at one point; a test now fails if an input or chip goes green.
Two weights only, regular and medium. Nothing bold. No gradients, no shadows.

A render harness exists in the scratchpad. Re-rendering all 51 card states after a visual change
is a two-minute job — do it and send the PNGs whenever anything visual changes. Yadin judges
design from images, not descriptions.

---

## 5. The caret fix — SHIPPED as 1.2.137, installed 2026-08-11

`27da358`, pushed to `main`; built as 1.2.137 and **installed by Yadin on 2026-08-11**. No tag, no
release, no manifest — nothing reached the fleet.

The morning card of 2026-08-11 logged `foreground=True` and still would not accept typing. Verified
from the log, not inferred. The cause: the code took `SetForegroundWindow`'s return value as truth.
Under an active foreground lock Windows can return nonzero while only flashing the taskbar button —
so the card believed it had won, suppressed the refusal hint, and left the user typing into another
application with nothing on screen to explain it.

The grab now asks `GetForegroundWindow` who actually holds it. A reported-but-not-granted result is
treated as a refusal, with reason code
`refused_reported_ok_but_not_foreground:fg=<process>,locktimeout_ms=<n>`.

**This makes the failure visible, not impossible.** The user may still have to click; he will now
know why.

The test gap that let it through: every focus test asserted *which control was armed*, none asserted
*the caret actually arrives*. Now covered per card.

Next action: push and cut **1.2.137** for Yadin's machine only, on his say-so.

---

## 5b. Complaints 1, 2, 3 and 6 — BUILT 2026-08-11, shipped as 1.2.138

Yadin's list is **seven** complaints, not the four in §6 below. The full set, in his numbering:

| # | What | State |
|---|---|---|
| 1 | First midday timed off the TASK's estimate, not a fixed 2h | Built |
| 2 | Evening card timing — fractions of the day, break guard | Built |
| 3 | A fourth card, and "Tomorrow" that persists | Built |
| 4+5 | Done/start both require pressing the widget | **Options owed, no decision** |
| 6 | Finished-task confirmation dwells too long | Built (30s → 6s) |
| 7 | Digest says "other browsing" for Google Docs | Two fixes shipped in 1.2.139; **re-measure owed** |
| — | Digest email redesign (asked for separately) | Agent half pushed `d4626a0`; **rendering not written** — see §5c |

**Complaint 7 — what was actually wrong, measured 2026-08-11 before writing any rule.**

Adding site rules would have changed nothing: `google docs` was **already** in the table
(`BrowserTitleClassifier.cs:205`). Two real defects, both now fixed:

1. **Unlabelled time was dropped**, not pooled. `FocusAppUsageBuilder` skipped any identity with no
   label, so the report covered 91–95% of the day and lost the rest (42m 30s across 14 of 19
   identities on 2026-08-10). Now shows as **"Unnamed"** — kept separate from "Other browsing" on
   Yadin's call, because an unlabelled identity may be a desktop app and naming it "browsing" would
   be a guess. Days now report 100%.
2. **THE ROOT CAUSE — labels were being erased on every user-agent restart.** The
   `AppCategoryClassifier` registry is per-process and starts empty; the user agent (which *writes*
   `focus-app-labels.v1.json`) never seeded itself from it — `CategoryRegistrySeeder` exists but
   **only the service calls it** — and `Persist()` wrote the partial registry over the top. Five
   labels existed for the whole machine while one day alone had 19 identities. The write now MERGES
   with the file, capped at 5000 identities. **The classifier was working; its output was binned.**

**Measured split (2026-08-10):** named 55.7% · Other browsing 35.6% · Unnamed 8.7%.
**Still nameless: 44.3%** — but that is an upper bound measured against a label map the bug had
already emptied, NOT a prediction. Fix 2 restores evidence the bug deleted, so its effect can only
be measured on days recorded after it ships.

**Owed to Yadin:** re-measure after days of real use on 1.2.139, then come back about local-only
title sampling *with that number*. He declined sampling until the residue can be measured without
being wiped. A read-only probe for this lives in the session scratchpad (`dwellprobe`).

**The model.** Three cards sit at fractions of the working day — morning at the start, the close-out
two thirds in, the evening at the end — measured in WALL CLOCK from the first captured activity.
The first midday is different: it fires when the **active task has spent the time the user
estimated**, floored at 90m of active work and capped at half the day so it cannot drift into the
close-out. Two currencies, deliberately: wall clock for the fractions, active work for the task
trigger and the guards, because active work is the only currency time is credited to a task in.

**Day length** is learned (`UserWorkDayLengthStore`, last 5 completed days, clamped 4–12h, default
8h). Yadin is never asked how long he intends to work.

**Work start is the first CAPTURED activity, not when the user agent started.** This is what makes
2026-08-09 come out right: the watchdog lives in `Porta.Agent.Service`, so the service captured all
morning while the user agent was down until 14:03.

**Known and accepted (Yadin, 2026-08-11):** on an 8h day from 08:00 the evening hard ceiling lands
at exactly 18:00, so a card at 18:04 can still appear while the 8h default is in place. He decided
to leave it — it self-corrects once real day lengths are learned.

**"Tomorrow" persists — schema v25**, `tasks.deferred_until_day`, a goal-day key. Local only;
nothing added to the digest or narrative payloads. It records WHEN a task is due back and never
changes its state, so deferring cannot lose work, and it self-expires because it stores a day rather
than a flag. The evening card stops asking about moved work and says what is moving.

**Both anti-spam holes are closed.** `MuteClassFor` is exhaustive with no `_` arm and **CS8509 is a
build error** in `Porta.UserAgent.csproj` — verified by deleting an arm and confirming the build
fails naming the missing kind. A failed mute write now logs (`mute_write_failed`) instead of
vanishing silently.

**A live gap found and closed on the way:** `MorningRecorded` keys off non-empty goal text, and
since step 3 the goal text is derived from task titles — so on a "just track" day with no tasks
**neither midday card could ever fire**. The midday cards now gate on the morning having been
*asked*, not answered.

**What to watch in `user-agent.log`:** `checkin_timing kind=… reason=… start=… day_len_m=…` on every
card, `checkin_midday_close_recorded … deferred=N`, and `mute_write_failed` (should never appear).

---

## 5c. The digest email redesign — **DONE 2026-08-12, live on Render**

> **STATUS UPDATE 2026-08-12.** The rendering is written, gated, pushed and deployed. Control
> plane `1eb72fe` (the redesign) + `122c17c` (a test-send script); `/health` confirmed serving
> `1eb72fe` at 09:05 UTC. **Render auto-deploys the control plane on push to `main`** — measured
> twice now (the previous deploy started 8 minutes after its push). No manual deploy needed; that
> rule applies to the AGENT manifest env vars, not to control-plane code.
>
> All eight items below are built. Two things the list did not mention had to change or none of
> it could ever render: `parseTasks` rejected open task states (so a moved task could never
> arrive) and `dayTimeline` was dropped at ingest entirely — both fixed in the same commit.
>
> **Gate:** tsc clean · **297/297 vitest** · `npm run build` clean. §8's "268 pass / 3 fail,
> order-dependent flake family" is STALE — the suite is fully green under the single-thread pool
> the `test` script already uses.
>
> **Degradation verified before deploying, because 1.2.140 is not installed yet:** with no
> `dayTimeline` the whole THE DAY section is omitted — no orphan label, no empty strip, no
> dangling legend or gap line. Rendered and looked at, not reasoned about.
>
> **Owed / open:** ARI'S TAKE needs `ANTHROPIC_API_KEY` on Render. It has never been confirmed
> set, and without it the section silently omits by design. `digestNarrative.ts` also still names
> `claude-opus-4-8`.
>
> **1.2.140 built for Yadin's machine only** — no tag, no release, no manifest, nothing to the
> fleet. `installer\bin\x64\Release\AriAgentInstaller.msi`, embedded ProductVersion 1.2.140
> verified from the MSI, exes stamped `1.2.140+a83f587`, SHA256
> `3386EAA931805FFD531B696C4D38A417F82CAE9C90F11025BC7D0A66DF6B275F`.

### The original section, kept for the reasoning

**Pushed:** `d4626a0`. **Not built into an MSI**, and nothing Yadin receives changes until
`digestEmailBuilder.ts` is written — the payload carries new data that nothing renders yet.

### The spec Yadin gave, in order, top to bottom

1. "Ari by Porta" wordmark — keep.
2. "YOUR DAY AT A GLANCE" label — keep.
3. Full date, "Tuesday, 11 August 2026" — keep.
4. Offline notice, **only when it applies**. Keep the subject-line streak ("9 days in a row").
5. **Headline:** a two-line sentence from the tasks — "You planned two things. You finished one." —
   then one line of context: "6h 39m focused across an 8h 35m day." **The "Not yet" badge is gone.**
6. **THE DAY — the timeline, and the centrepiece.** Horizontal strip: focused / at the machine /
   away. Hour markers underneath, one line of legend.
7. **Tasks**, one block each: title, bar, actual time large, planned small beneath. Finished gets
   the accent and a check. **Anything moved reads "moved on", never as a failure.**
8. **Four figures in a row:** Worked, Focused, Best run, Away. Away replaces the old "Breaks 0/6"
   and carries the locked/away-not-counted meaning without the extra sentence.
9. WHERE YOUR TIME WENT — top sites, bars, times.
10. WHAT PULLED YOU OFF — **only rows with real numbers. Never "no data", never zeros. Nothing to
    show → the whole section including its label is omitted.**
11. **ARI'S TAKE — in serif**, so it reads as someone talking rather than another data row. Built
    from actual task outcomes, never inference. If it cannot see an outcome, it says so.
12. "Open Ari" button, linking where it links today.
13. "Sent by Ari · useporta.com" footer — keep both.

**UNRECOGNISED TIME IS NOT SHOWN.** No "Other browsing", no "Unnamed". §9 is a ranking of top
sites, not an accounting of every minute, so it never claimed to add up. The honest totals live in
the four figures.

### THE GREEN RULE — restated 2026-08-11, not widened

> **Green means work that counted: a finished task, a finished bar, focused time on the timeline,
> plus the primary button. Nothing else, ever.**

Yadin chose to fix the rule rather than grant an exception each time the accent was needed. Cards
have no timeline, so the enforceable set in `UserCheckInCardVisualRulesTests` is unchanged; the
wording there now matches. Nothing on an input, a chip, or an unfinished row may ever be green.

### THE GAP RULE

An hour with **nothing recorded** renders as **empty — no fill, bare track**. Never as away.
"Nothing there reads as nothing." Do **not** add a fourth legend item. Instead, on days that
actually have a gap, add **one** line under the legend: *"Gaps are hours your machine was off."*
Same principle as omitting an empty section — never explain a thing that is not on the page.

Measured design note: the track must be visibly *emptier* than away, not merely different.
`#171A20` track against `#232931` away works; a first attempt at `#1B1F26` read as away and was
wrong.

### What is PUSHED (agent side)

| Piece | Where |
|---|---|
| `ReadDayTimeline` — day shape by local hour | `SqliteBrainStateStore.cs`, + `IBrainStateStore`, `BucketFinalizerStateStore` |
| `GetDayTimeline` | `AgentCoreController.cs` / `IAgentCoreController.cs` |
| `AgentDayTimelineHour` record | `AgentLocalStoreModels.cs` |
| `dayTimeline` on the payload, `DigestTimelineHourPayload` | `FleetContracts.cs` |
| Open tasks + `deferredUntilDay` on `DigestTaskPayload` | `FleetContracts.cs`, `DailyDigestReporter.BuildTaskPayloads` |
| Green-rule wording | `UserCheckInCardVisualRulesTests.cs` |

**The timeline uses the capture pipeline's OWN attention classification**, exactly as
`AttributeBucketToActiveTask` does — away is away, distraction kinds are "at the machine",
everything else is focused. No new inference, and the timeline cannot disagree with the per-task
numbers. **Hours with no buckets are absent, never zeroed.**

**Live-data change, named and approved before making it:** open task titles now leave the machine.
Closed ones already did; nothing is persisted server-side.

### The payload, with real values

```json
"dayTimeline": [
  { "hourLocal": 8,  "focusSeconds": 1980, "activeSeconds": 900, "awaySeconds": 720 },
  { "hourLocal": 9,  "focusSeconds": 3120, "activeSeconds": 300, "awaySeconds": 180 },
  { "hourLocal": 12, "focusSeconds": 240,  "activeSeconds": 600, "awaySeconds": 2760 },
  { "hourLocal": 16, "focusSeconds": 2820, "activeSeconds": 480, "awaySeconds": 300 }
],
"tasks": [
  { "title": "Pricing page",     "plannedSeconds": 7200, "actualSeconds": 4020,
    "offtaskSeconds": 480, "state": "done",    "deferredUntilDay": "" },
  { "title": "Client revisions", "plannedSeconds": 3600, "actualSeconds": 1140,
    "offtaskSeconds": 60,  "state": "pending", "deferredUntilDay": "2026-08-12" }
]
```

### EXACTLY what is left, in `porta-control-plane/src/core/digestEmailBuilder.ts`

1. **Render the 13 sections above** to the design values in §4. A finished mock is in the repo root:
   **`Digest_Redesign_Mock.html`** (600px, email-safe tables, inline styles) and
   **`Digest_Redesign_Mock.png`** (what Yadin approved). It is the template, not generated output —
   port it, do not redesign it.
2. **The timeline strip** from `dayTimeline`: one table row, cell widths proportional to the hours
   spanned, three fills plus bare track for absent hours. Hour markers beneath. Legend, plus the gap
   line **only when an hour is missing**.
3. **The headline through the SAME rule as `UserCheckInTaskBoard.SummarizeDay`** — planned = every
   task on the board, finished = `state === 'done'`. This is the whole point of sending open tasks;
   if it is reimplemented differently the card and the email will disagree again, which is the bug
   this replaced.
4. **"moved on"** for any task with a non-empty `deferredUntilDay`: status under the title, neutral
   grey bar, never the accent, never phrased as a failure.
5. **Omit `WHAT PULLED YOU OFF` entirely** — label included — when no row has a real number.
6. **ARI'S TAKE in serif**, built only from actual task outcomes. When no outcome is visible it must
   say so rather than fill the space.
7. **Drop the residue rows** from `WHERE YOUR TIME WENT`: no "Other browsing", no "Unnamed".
8. Existing constant to reuse: `digestEmailBuilder.ts:78` already holds the residue label.

**Not done, and not started:** any of the above. The agent half is inert until it exists.

---

## 6. The queue as Yadin first named it (superseded by §5b — kept for the reasoning)

**a. Card timing — the big one.** The evening card fired at 18:04 while he was still working. A
card that closes the day before the day ends breaks the whole loop. Two parts:

- Never show the evening card while the user is actively working.
- Space the cards from **when work actually started**, not off the wall clock.

The agreed model: four cards at fixed **fractions** of the day, not fixed offsets — start, one
third in, two thirds in, end. An 8-hour day gives 08:00 / 10:40 / 13:20 / 16:00; a 10-hour day
gives 08:00 / 11:20 / 14:40 / 18:00. One rule, no special cases for 8 / 9 / 10 hours.

This adds a **fourth card** — a second midday check, which should differ from the first.

**Do not ask the user how long they intend to work.** Ari has been measuring his days for weeks.
Default to 8 hours, learn from the recent average. Adding that question turns a glance into a
meeting.

**b. Done and start both require pressing the widget.** If the user finishes a task or starts a new
one without telling Ari, Ari does not know. Note the locked rule this collides with: **Ari must
never infer how time splits across tasks** — a guessed number in a client's report is exactly what
this product exists to avoid. So the answer cannot be automatic detection. It has to be asking
sooner, or nudging when a task runs past its estimate. Bring options, do not pick one.

**c. The finished-task confirmation on the widget dwells too long.** Shorter, but not instant.
Applies to all tasks.

**d. The nightly digest says "other browsing".** He was writing in Google Docs and it was bucketed
as "other browsing". A report that says "other browsing — 2h 23m" is useless to a client, so this
matters for step 10. Separate area from the cards.

---

## 7. Locked decisions — do not reopen

- Morning card asks for up to three tasks. One box, `+ add` for more, three is the ceiling.
- **Ari proposes the duration; the user taps 1h / 2h / 3h. Never types a number.**
- The first task auto-starts, otherwise measured time lands on nothing and every task reads 0m.
- The report never sends automatically. Ari drafts, Yadin taps.
- The card grabs the keyboard when it appears and when a new box opens — but never grabs it back
  if the user deliberately clicks away. **Do not trap the user.**
- Never fight for a keyboard someone else is actively using.
- No financial reporting. No website work. No features outside the plan.
- The morning screen is a **card, not a full screen**. Settled 2026-08-10.

---

## 8. Process rules — non-negotiable, all learned the hard way

- **Installing is Yadin's, always.** Build the MSI, hand over the path and hash, stop. A manual
  install broke his machine on 2026-08-06.
- **Release builds use the numbered `build-<version>.ps1` at the repo root.** Copy the highest
  number to the next, bump all four version fields in all three `.csproj` first, run that.
  `scripts/build-msi.ps1` is a hard-fail stub — it produced an MSI that bricked a machine.
- **NEVER REUSE A VERSION NUMBER, and bump as part of the commit that changes code.** Learned
  2026-08-11: `d4626a0` landed with the csproj still at 1.2.139, the number already installed on
  Yadin's machine — so "1.2.139" briefly meant two different builds. The commit-hash suffix on
  `ProductVersion` made them distinguishable, but only by accident. Bump BEFORE building anything.
- **Nothing reaches the fleet without his explicit go.** Auto-update delivers to every user within
  ~6h. Pushing to `main` is safe; a tag, release and manifest are what ship.
- **Name every live-data write before making it, and wait for the go.** What leaves his machine is
  his decision, every time.
- **When a hypothesis is falsified, revert the change it justified.**
- **The CI check does not run the tests.** `validation-gate.yml` has no `dotnet test` anywhere — a
  green tick means it compiles, nothing more. Adding one (allowing the 4 known Core failures **by
  name**) is the highest-value maintenance item and is still not done.
- Any new test that creates a window on its own STA thread must shut its dispatcher down.
- Check-in card test classes share a static seam and must stay in `CheckInCardCollection`
  (non-parallel), or they flake.

**Test baseline — re-measured 2026-08-12 after 9a.** Core **752 passed / 4 failed**, and those four
are the documented restart-reconciliation baseline (`SECURITY_HANDOFF.md §4.8`) — confirm them BY
NAME against `scripts/test-gate.ps1`, which allows exactly those four; that red is expected.
UserAgent 470, Service 133, ControlCenter 80, Runtime 6 — all green. The suites keep growing, so
older counts written here and at `SECURITY_HANDOFF.md:857` are stale rather than a regression.
Control plane **297 pass / 0 fail** — the "3 order-dependent flakes" note is stale too; `npm test`
already pins a single thread.

**Run the gate through a clean PowerShell process**, not the workspace shell:
`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gate.ps1`. Windows PowerShell 5.1
turns `dotnet test`'s stderr into a terminating NativeCommandError, so invoking it any other way
reports a failure the script did not have.

---

## 9. Already falsified — do not re-propose

The narrative line failed ~7 times in 10 for days. **Solved:** a 3-second HTTP client timeout on
the agent, sitting below all three documented budgets (10s / 9s / 8s). The narrative route now has
its own 12s client.

Dead, all measured: Render cold start · the 8s Opus timeout · device auth 401 · payload validation
and route differences · missing `daily_stats` uploads · a widget GDI handle leak · the check-in
card's `TextBox` ControlTemplate as the cause of the caret failure.

Also worth knowing: **the test suite writes into the production log.** `UserAgentFileLog` has no
path override in tests, so bursts of dozens of identical lines in `user-agent.log` are test runs,
not field events. This has already caused test output to be read as field data once. Still unfixed.

---

## 10. Other open items

- **Render payment was failing.** Service is healthy as of 2026-08-10 (200 in 374ms) but a healthy
  service does not prove the billing state. If it lapses the whole control plane goes down.
- Yadin's Windows foreground lock is pinned at `2147483647` for reasons still unknown. Ari is not
  doing it — every `user32` entry point was enumerated and `SystemParametersInfo` appears once, as
  the GET. A reboot would say whether it survives logon.
- `/health` now returns the running commit, so "what is Render running?" is one request.

---

## 11. Maintenance list — the test suite's messes

> **ALL DONE 2026-08-12** (a, b, c in the order Yadin set, plus a fourth the gate exposed). Kept in
> full because two of the three entries recorded a cause that turned out to be wrong or incomplete,
> and the corrections are the useful part. Committed as 1.2.142 — code only, no build: the log
> change behaves identically on a machine with no override set, so there is nothing to install.

Separate defects, all the same shape: **the test suite writes into the real machine.** None is a
product bug, all had already cost investigation time or disk. Grouped here on Yadin's instruction
(2026-08-11) so they stop being scattered across three sections.

**a. The CI check does not run the tests.** `validation-gate.yml` runs `scripts/build.ps1` and
`Porta.Agent.ValidationRunner` — there is **no `dotnet test` anywhere**. A green tick means it
compiles, nothing more, and a real regression in any of the five suites reaches `main` green.
Adding a `dotnet test` step that allows the 4 known Core failures **by name** is the highest-value
item here. **DONE 2026-08-12** — `scripts/test-gate.ps1`, wired into `validation-gate.yml`. It parses
the TRX rather than trusting exit codes, and fails on: an unlisted failure; a listed failure that
has started PASSING (delete the entry, per `SECURITY_HANDOFF.md` §4.8's own instruction); a listed
name matching no test that ran (renamed, so the allowance had become an allowance for nothing); and
a suite that produced no results at all.

**It caught a real flake on its first run** — `UserDesktopWidgetWindowTests` and
`GuiHandleLeakStressTests` both drive the static `WidgetSizeLog.PathOverride`, so in parallel one
class's evidence line lands in the other's file. Same shape and same fix as the check-in cards:
`WidgetSizeLogCollection`, `DisableParallelization`. **A fourth mess, found by fixing the first.**

**b. The test suite writes into the production log. FIXED 2026-08-12.** `UserAgentFileLog` now
resolves its path per write and can be overridden by the `PORTA_USERAGENT_LOG` environment variable
or `RedirectTo`; `Porta.UserAgent.Tests` calls it from a `[ModuleInitializer]`, which runs before
xUnit discovers the first test — a fixture is too late, because host classes log while constructing.
An override that cannot be written DROPS the line rather than falling back to the real log. The
original defect *(verified)*: a test run appended dozens of identical lines to the live
`user-agent.log`, and that output was read as field data once during an investigation where the log
was the only instrument.

**c. The test suite leaks a SQLite database per scenario into `%TEMP%`. NEW, 2026-08-11.**
Found because it filled the disk: **5.1 GB in `%TEMP%\porta-step19-scenarios` — 924 `.db`, 842
`-wal`, 842 `-shm`**, oldest 2026-08-07, and the run done that morning added to it. Deleted with
Yadin's go; **deleting is a mop, not a fix — it will refill.**

**FIXED 2026-08-12** — and the second cause recorded below was WRONG. The corrected version:

1. It deleted `_dbPath` and **never the `-wal` / `-shm` sidecars**, so even a fully successful
   dispose left two files behind. That alone explains 1.77 GB. *(verified by reading the code —
   this half was right.)*
2. **The real reason the `.db` files survived is SQLite CONNECTION POOLING, not an undisposed
   store.** `Microsoft.Data.Sqlite` pools by connection string and keeps the underlying file handle
   open after a connection is disposed, so `File.Delete` hit a sharing violation and the `catch`
   swallowed it. *(verified 2026-08-12.)*

> **The cause this file gave until 2026-08-12, struck out because it sent the fix the wrong way:**
> ~~"It never disposes `_innerStore`, so the SQLite connection is still open when `File.Delete`
> runs"~~ — with the fix shape ~~"dispose the store before deleting"~~. **There is no store to
> dispose.** `SqliteBrainStateStore` does not implement `IDisposable`, and it opens a fresh
> connection per call through `DbOpen.OpenConnection`, each inside a `using` (verified by reading
> both files). Anyone following that instruction would have gone looking for a leak that does not
> exist. The entry was marked *(inferred)* and reasoned from the 924-vs-842 gap; the gap has a
> simpler explanation — **the sidecars only exist once a scenario has actually written**, so
> scenarios that never wrote left a `.db` and no `-wal`.

**The fix that shipped:** `SqliteConnection.ClearAllPools()` (runner) / `ClearPool(Connection)`
(database harness) to drop the pooled handles, then delete all three files via the shared
`DeleteDatabaseFiles` helper. Both temp directories are covered — `porta-step19-scenarios` and the
second one, `porta-step19-tests`, at `DeterministicScenarioDatabase.cs:53`. Three regression tests
in `ScenarioHarnessCleanupTests` assert that nothing is left behind, including across a `Restart()`,
which is the case that opens a second pooled handle on the same file.

**Mopped again 2026-08-12** (374 MB had refilled since the 11th, which is what proved the leak was
still live). This time the mop is not the fix — the fix is above it.

> **The disk story continues in §13, and the cause is NOT the test suite.** Mopping `%TEMP%` freed
> real gigabytes twice, but the thing actually filling this disk is `C:\ProgramData\PortaAgent` at
> ~7.7 GB — seven nightly full-database backups plus an unpruned `primitive_buckets`. Read §13
> before proposing another cleanup.

**Disk context, since it is what made this visible:** C: was down to **36 MB free** on 2026-08-11,
which risks the live agent's own SQLite writes. Freed 7.2 GB. Still held, on Yadin's instruction:
`artifacts\release`, 2.2 GB of MSIs for 1.0.14–1.0.35 — **verified** that none of those 22 versions
exists as a GitHub release and no `1.0.x` tag exists beyond `v1.0.0`, so that folder is the only
copy of those builds anywhere and there is nothing to rebuild them from.

---

## 12. Step 9a — projects. BUILT 2026-08-12, committed `861b4c5`, shipped in 1.2.144

**1.2.144 handed over 2026-08-12**, carrying 9a *and* the `primitive_buckets` fix (§13.2) so one
install covers both. `installer\bin\x64\Release\AriAgentInstaller.msi`, ProductVersion read back out
of the MSI, exes stamped `1.2.144+861b4c5`, SHA256
`5AF41C0789F60E02E5443431FE8F6E717CDCA577925F1ECE4996AA228E9ABF78`. No tag, no release, no manifest.

*(1.2.143 was 9a alone. Both are long since installed — see §3 for what is on the machine today,
and read the exes rather than this line.)*

The brief is `Projects_Step9_Brief.md`; the four answers are `Projects_Step9_Answers.md`; **Yadin's
decisions are §10 of the brief and supersede everything above them there.**

### The phase split, as decided

**9a (done)** projects exist locally — picker, chip, Control Center page. **9b (decided, not built)**
the names leave the machine: `projectName` on the digest + narrative payloads, the `BY PROJECT`
block, and the setting defaulting on. **9c** durable server-side task history. **10a** the projects
page from §4 of the brief. Then integrations.

Yadin chose to ship 9a alone and live with it before deciding 9b. **The extra install is the price
of being able to change his mind, and he considers that cheap** — do not "helpfully" fold 9b in.

### The thing that makes 9a tellable from 9b

**No project name leaves the machine in 9a**, and `Step9aNothingLeavesTheMachineTests` asserts it
against the SERIALIZED payloads rather than the record definitions. His test is that he can tell
the two apart by looking at his email. Adding the field for 9b will fail those tests, which is the
point: it is a live-data decision and must be made on purpose, not arrive with a refactor.

The three egress points for task data, all verified untouched: `DailyDigestReporter.BuildTaskPayloads`,
`NarrativeGateway.ProjectTasks`, and `daily_goals.goal_text` (derived from task titles by
`UserCheckInTaskPlanner.JoinTitles`, and uploaded by `DailyStatsReporter`). That third one is the
subtle one — **if project names are ever folded into the goal sentence they leak via `daily_stats`.**

### Schema v26 and the invoice rule

`projects` (id, name, created_at_utc, archived_at_utc, order_index, schema_ver) plus
`tasks.project_id`, `0` = untagged. Not nullable, matching `deferred_until_day`'s `''`-means-no, so
no caller anywhere handles a null.

**`CREATE UNIQUE INDEX idx_projects_name ON projects(name COLLATE NOCASE)` is not housekeeping.**
"Nolan" and "nolan" as two rows halves a client's total — the invoice corruption this feature exists
to prevent, arriving through the back door. Create-or-get, never create-or-fail: a name that already
exists returns the existing project, and an archived name typed again un-archives it rather than
colliding with the index.

### Height — measured before building, not after

Target display: work area 672 DIPs, card ceiling 632 (`672 − 2×20` margin), three-task morning card
with yesterday's block **566.36**.

| State | Cost |
|---|---|
| Chip at rest | **0.00** — the constraint that does not move |
| One picker open | +29.63 |
| Three open | +73 → 673, off the screen |
| 8 chips, unbounded | primary button 12.75 DIPs BELOW the window |

So: one picker at a time, six projects, and the strip is bounded at two rows and scrolls inside
itself past that. Its contribution is a constant regardless of how many projects exist or how long
their names are — **that is what makes "Start the day cannot leave the screen" a property of the
layout rather than a hope.** The card body also scrolls instead of clipping at MaxHeight now; before
this, content past the ceiling silently cut off the primary button and there was no scrollbar.

### Decisions worth not re-litigating

- **Enter in the "+ new" box must not start the day.** Every other box on that card submits on
  Enter. Tested.
- **No project prefix on widget task rows.** The title has 96 DIPs and `Nolan · ` eats 40% of the
  thing the user needs to read. The picker is on the `+ task` compose only.
- **Re-tagging is allowed at any time.** Time moves WHOLE or not at all, so a minute still belongs
  to one task and one project. The digest already sent is a snapshot of that night and is **never
  regenerated**; the projects page is current truth and says so when a day was re-tagged after its
  digest went out. (That last part is 10a's job and is not built.)
- **On a card a project is only a name.** Client, colour, rate belong in Control Center. The morning
  card must not become a form.
- Archive, never delete: a project's tasks keep pointing at it and some of that time has been
  reported.

### Local task retention: 30 → 400 days

Measured from the live database first: **a task row is 135 bytes**, three a day, so a year is
~148 KB. **Nothing depended on the 30-day sweep for correctness** — every read is date-windowed by
its own `WHERE` clause — so this changes disk use and nothing else. Verified by test from both
sides: a 500-day-old closed task is swept, a 90-day-old one survives and still does not appear on
today's card.

---

## 13. THE DISK — this is the cause, and it is bigger than projects

> Investigated 2026-08-12 on Yadin's instruction after 9a. **Everything here is verified** — read
> from the live database, the live filesystem, and git history. Nothing below is inferred.
>
> **STATUS 2026-08-12, after his go on all of it:**
> **13.1 DONE** — the two `.replaced` copies deleted, 1.25 GB reclaimed.
> **13.2 FIXED** — `a3bf395`, shipped in 1.2.144. Both DELETE lists now include the table.
> **13.3 NOT CHANGED** — the two backup constants are a recommendation only; he asked to be told,
> not to have them moved. The numbers to use are at the end of 13.3.

C: had **3.4 GB free** when this started, having hit **36 MB free twice** the week before. After the
deletion and the build's own cleanup it was **7.58 GB**. `C:\ProgramData\PortaAgent` was ~7.7 GB.

| Thing | Size | Now |
|---|---|---|
| `backups\` — 7 nightly full copies | **5.47 GB** | unchanged; shrinks as new backups are taken |
| `core-state.db` (live) | 906 MB | 923 MB — see the note in 13.2 about why it does not shrink |
| two `.replaced` copies from 28 July | 1.28 GB | **deleted** |

### 13.1 The two `.replaced` copies — DELETED 2026-08-12, 1.25 GB reclaimed

Created by **`scripts/restore-backup.ps1:210`**, which deliberately moves the live database aside as
`core-state.db.replaced-<stamp>` (with its `-wal`/`-shm`) before copying a backup into place, as an
undo. There are two, four minutes apart (13:23:42 and 13:27:38 on 2026-07-28) — **the restore was
run twice that afternoon.**

**Nothing reads them.** Verified three ways: the agent opens exactly `core-state.db`
(`DbPaths.GetCoreStateDbPath`, filename hardcoded); `DatabaseBackupWorker` enumerates only
`backups\core-state-*.db`, which these do not match; and the only other directory enumeration in the
whole product is Control Center's local-data reset (`MainWindow.xaml.cs:1387`), which globs
`core-state.db*` and **deletes** them. The undo they exist for expired 16 days ago.

**Deleted 2026-08-12 on Yadin's go**: six files (two `.db` plus their `-wal`/`-shm`), 1.25 GB,
free space 6.80 → 8.05 GB. The live `core-state.db` and the `backups\` folder were untouched —
the filter was `core-state.db.replaced-*`, which matches neither.

**If a restore is ever run again it will create a fresh one, and that is correct.** It is the undo.
Delete it once the restored database has proved itself, not before, and never delete one whose
restore you have not yet lived with.

### 13.2 `primitive_buckets` — an OVERSIGHT, with dates. FIXED `a3bf395`, in 1.2.144

**542 MB, 137,217 rows, 4,141 bytes per row, ~4,036 rows/day ≈ 16.7 MB/day, unbounded.** It holds
2026-07-09 → 2026-08-12, **34 days**, while every pruned table holds 15. Average `primitives_json`
alone is 3,255 bytes per row.

**Oversight, not deliberate — provable from the commit clock:**

- The prune list was written **2026-03-02 13:23:52** (`b3beced`, "step1"). At that commit
  `primitive_buckets` **did not exist** — zero occurrences in the file.
- `primitive_buckets` was created **38 minutes later**, 2026-03-02 14:01:26 (`a7d5370`, "step2").
- It was never added to the list. **`DELETE FROM primitive_buckets` has never existed in any commit
  on any branch** (`git log -S --all` returns nothing).
- Corroborating intent: `GroundTruthLabels_1b_BuildPlan.md:15` names it as the single biggest table
  and says *"this is why the prune exists"*. The intent was explicitly that it be pruned.

**What reads it: nothing.** One `INSERT` (`UpsertPrimitiveBucket`, `SqliteBrainStateStore.cs:6222`)
and **zero `SELECT`s anywhere in the repository.** The table is write-only.

**THE PRIVACY DEFECT — the more serious half, from the same root.** It was missing from the
local-data reset list too. **"Delete my local data" reported success and left 542 MB of the user's
own behavioural data on the machine.** Normalized, z-scored primitives for every 5-second bucket
plus the contributor and "why" JSON behind them — precisely what someone pressing that button means
to be rid of. The product promised deletion and did not deliver it.

**Fixed 2026-08-12 (`a3bf395`, in 1.2.144).** Both lists now include the table. Retention is
**14 days**, matching `DefaultRetentionDays` and every sibling capture table. Nothing reads it, so
shorter would also be defensible; 14 keeps it consistent and needs no new constant. It prunes on
`start_utc`, not `timestamp_utc` — `ix_primitive_buckets_start_utc` already covers that.

**Three tests, each verified to FAIL with either DELETE removed** (checked by reverting, running,
and restoring): the retention boundary, prune-covers-it-not-just-its-siblings, and DeleteLocalData
leaves none behind. They assert against the TABLE rather than through the pipeline — the defect was
never about how rows are written, it was two lists that quietly did not mention them, and a
pipeline test would have missed it just as thoroughly.

> **Do not expect `core-state.db` to shrink after installing 1.2.144.** SQLite frees the pages for
> reuse rather than returning them to the OS, so the file stops GROWING rather than getting smaller.
> The next nightly backup is a `VACUUM INTO` and will be immediately smaller — and the backups are
> where the gigabytes actually were (13.3). Do not go looking for a bug when the file size holds
> steady; that is the expected shape of the fix.

### 13.3 Why it compounds by 8×, which is the real finding

`DatabaseBackupWorker` keeps **`RetentionDays = 7`** nightly VACUUM copies of the whole database. So
**every unpruned megabyte exists eight times over** — once live and seven times in `backups\` — plus
once more in each stale `.replaced`. `primitive_buckets` alone is ~542 MB × 8 ≈ **4.3 GB of this
disk.**

Two things about the backup worker's own safety nets, both verified:

1. **`MaxFolderBytes = 8 GB` is larger than the free space on the drive.** The ceiling can never
   engage before the disk fills. Its sizing comment says *"core-state.db vacuums to ~640 MB on a
   real machine (2026-07-27), so 7 days is ~4.5 GB"* — measured three weeks ago; it is **855 MB per
   backup now**, so the assumption the ceiling was derived from is already stale.
2. **`HasHeadroom` requires `free ≥ 2 × dbBytes`** — currently 1.81 GB. Below that, backups silently
   stop. Fail-safe rather than fail-dangerous, but it means backups stop happening exactly when the
   disk is tight, which is when they matter most.

**Fixing `primitive_buckets` was the highest-value single change on this machine:** it stops the
live database growing, and then shrinks every nightly backup taken afterwards, which is where the
gigabytes actually are. It does not shrink the seven backups already on disk — those age out over
a week.

### 13.4 The two backup constants — RECOMMENDED, deliberately NOT changed

Yadin asked what to set them to, not to set them. **Neither number has been touched.** Both live in
`DatabaseBackupWorker`.

**`MaxFolderBytes`: 8 GB → 5 GB.** The 8 was derived from a database measured at ~640 MB on
2026-07-27; it was 923 MB by 2026-08-12, so the number was already stale when it was found. Once
the 14-day prune settles the database at roughly 600 MB, seven backups is ~4.2 GB — so 5 GB sits
just above normal operation. That keeps it doing its actual job (a runaway guard that must NOT
bind day to day, or it silently prunes below the retention the user asked for) while being low
enough to fire on a drive like this one before the drive itself does. **A ceiling above the free
space on the volume is not a ceiling.**

**The free-space rule: `free ≥ 2 × dbBytes` → `free ≥ dbBytes + 3 GB`.** Today's rule needs 1.85 GB
and is derived from the database size, which is a proxy for "room for one more copy" and says
nothing about what the *agent* needs. The agent's own risk begins when the volume runs low, whatever
the database weighs. 3 GB clears the point where this machine got into trouble — 36 MB free, twice
— with real margin.

**No build needed to try it.** `PORTA_BACKUP_MIN_FREE_BYTES` already overrides that rule
(`ReadConfiguredMinFreeBytes`); set it to `3221225472` and the floor moves today.

**The tension Yadin named, stated honestly, because it does not fully resolve.** Raising the floor
means backups stop *sooner*, and he is right that a tight disk is when a backup matters most. It is
still the correct trade — a skipped backup is recoverable, a full disk corrupting the live database
is not — but it is a trade, not a fix. **The real fix is not needing ~900 MB of free space per
backup.** The `primitive_buckets` prune buys most of that. If it is still tight afterwards, the next
lever is `RetentionDays` 7 → 5 (~3 GB of backups instead of ~4.2), and after that a strategy that
is not seven full copies. Do not raise the floor and call the problem solved.

---

## 14. The hourly app rollup — BUILT 2026-08-12, `e979f2b`, shipped in 1.2.145

**Why it went before the projects page, and before 9b:** the page needs per-app time going back
months; the tables it would have to read are pruned at 14 days and cost ~32 MB/day. Every day the
rollup did not exist was a day of history nobody could get back. Yadin's reasoning, and it was right.

### What it is

One table, `app_usage_rollup`, one row per **UTC hour × task × application**:

```
hour_start_utc  "2026-08-13T10:00:00.0000000+00:00"
task_id         7          (0 = time not on any task — most of a normal day)
app_hash        32 chars   (resolves to a name through the existing label map)
focused_seconds / offtask_seconds / active_seconds
backfilled      0 = measured live, 1 = reconstructed
```

**Measured, not estimated: 595 rows for 14 days of real history, 57 KB — about 4 KB/day, ~1.5 MB a
year.** The capture pipeline is ~32 MB/day, so the rollup costs about 0.01% of what is already
being paid, and never needs pruning for size.

### The two design decisions that matter

**Keyed by TASK, not project.** Re-tagging is allowed at any time and the page is current truth. A
stored `project_id` would freeze last month's rows at whatever the tag was then, and rebuilding
them is only possible while the source buckets survive — 14 days. Resolving `tasks.project_id` at
READ time makes a re-tag instant, total and permanent. There is a test that re-tags a task and
asserts three separate days of history follow it.

**Keyed by UTC hour, converted with the reader's offset** — the same contract `ReadDayTimeline`
uses. Hourly rather than daily on Yadin's call: *"I can ignore detail I have. I can't recover detail
I didn't take."*

### The line that must not be moved

`AccumulateAppUsageRollup` runs in the SAME transaction as `AttributeBucketToActiveTask`, off the
same inputs. **It is the only place the task→app link is ever recorded, and it cannot be
reconstructed afterwards** — `input_signal_windows` has no task column, and `tasks` logs only its
FIRST start (`started_at_utc = CASE WHEN … = '' THEN`), so a task switched away from and resumed
looks continuous. Attributing app time across that gap would invent exactly the number a client's
invoice must never contain. If this write is ever made conditional or moved, that history stops
existing and no later fix recovers it.

### The backfill — one shot, already taken

Runs inside the v27 migration because those days get no second chance. **Verified against a copy of
the live 926 MB database before shipping:** 935 ms, 595 rows, 107 hours across 14 days, 46 apps,
2026-07-30 → 2026-08-12. **Reconstructed total 76.5 hours against 76.5 hours in the source** —
nothing lost, nothing double-counted.

It fills the APP dimension only. Every backfilled row is `task_id = 0` and carries `active_seconds`
alone, flagged `backfilled = 1`. **A zero in the focused/off-task split on those rows means "not
known", never "measured as zero"** — anything that renders them must say so or omit them. The
project dimension genuinely begins the day projects did, which is 2026-08-12.

---

## 15. The backup constants — and a recommendation I had to correct

`MaxFolderBytes` **8 GB → 5 GB**. The 8 was derived from a database measured at ~640 MB on
2026-07-27; it was 923 MB by 2026-08-12, and 8 GB was **larger than the free space on the drive it
was protecting**, so the ceiling could never fire before the disk did.

**The floor is the part worth reading.** It was recommended as a flat `database + 3 GB`, and Yadin
approved it on that recommendation. Implementing it surfaced three existing tests asserting the
opposite, and `b11f345` (2026-07-28) had already removed a flat 2 GB floor for precisely the reason
it was about to be reintroduced:

> *"An invented floor fails in the worst direction — it silently stops backing up a machine that has
> ample room."*

A fixed 3 GB refuses a 60 MB database on a laptop with 400 MB free — forever, and quietly. So the
rule stays **derived**, and only the multiple moved: **1 → 2**, meaning after writing a snapshot the
volume must still hold two more. On the machine that prompted this that is **2.77 GB rather than
1.85 GB** — the intent of the approved number without breaking the small case.

**The lesson, since it has now cost two rounds:** before changing a constant in this codebase, run
`git log -S` on it. Both floors and both ceilings here have a history, and each was set the way it
was for a reason someone wrote down.

**Known and accepted (Yadin):** a stricter rule stops backups sooner, which is the opposite of what
you want on a tight disk. It is the right trade — a skipped backup is recoverable, a full disk
corrupting the live database is not — but it is a trade, not a fix.

---

## 16. OWED: check the disk on or after 2026-08-19

**Yadin asked to be reminded.** One week after the `primitive_buckets` prune has been running —
it reached the machine with 1.2.144 on 2026-08-12, so the check is owed from 2026-08-19:

> **If C: is still tight, drop `DatabaseBackupWorker.RetentionDays` from 7 nightly copies to 5.**

That is ~3 GB of backups instead of ~4.2 GB. It is the next lever after the `primitive_buckets`
prune, and it should not be pulled before there is a week of evidence that the prune was not enough.

**What to measure before deciding**, all on `C:\ProgramData\PortaAgent`:

1. `core-state.db` — should be roughly flat, near 600 MB, not climbing. It will NOT have shrunk from
   923 MB; SQLite frees pages for reuse rather than returning them (§13.2).
2. `backups\` — should be falling as pre-fix snapshots age out of the 7-day window.
3. Free space on C:.
4. `app_usage_rollup` row count — expect ~40/day. If it is far above that, something is fragmenting
   the app identity and that is worth understanding before it fills anything.
