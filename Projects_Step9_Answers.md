# Projects — answers to the four questions

Written 2026-08-12 in reply to `Projects_Step9_Brief.md`. **No code has been written.** Every claim
below is marked `verified` (I measured or read it) or `inferred` (I reasoned to it).

One reminder, once, as the brief asks: the projects page in §4 is essentially the step 10 report on
a screen, and step 10 was gated on a week of real use. The picker, the chip and the digest block are
not — they add information without changing the loop, which is why starting them now is fine.

---

## Short version

- **The picker fits.** A chosen project chip costs **zero** height. One open picker costs **29.6
  DIPs**. *(verified — measured on your display.)*
- **It stops fitting at seven chips.** At 8 chips the card hits its ceiling and **"Start the day"
  is cut off the bottom** — the card has no scrollbar. So the picker list must be capped, and only
  one picker may be open at a time. *(verified.)*
- **One new field leaves the machine:** `projectName` per task. Nothing else. The digest's BY
  PROJECT totals are summed from the tasks already in the payload, so the block can never disagree
  with the task list above it.
- **The projects page cannot be built on what exists.** Closed tasks are deleted locally after 30
  days and the server stores no tasks at all, so "how many days does this project span" has no
  answer today. That is a separate phase and the brief does not cost it. *(verified.)*
- **The real invoice risk is not time splitting** — that is already impossible. It is **re-tagging a
  task after its time was measured and already emailed.** You need to decide that one.

---

## 1. The phased plan

The brief is four pieces of work, not one. Split honestly:

### Step 9a — projects exist, nothing leaves the machine

Schema, the picker on all three cards and the widget's `+ task`, the chip at rest, `untagged` as a
permanent silent state, and a plain list of projects in Control Center so a typo can be renamed.

Nothing new is sent anywhere. You can use it for a few days and find out whether you actually tag
things before any of it becomes a privacy decision. **This is step 9 proper.**

### Step 9b — the names leave the machine

`projectName` on the digest payload and the narrative payload, the `BY PROJECT` block in the email,
Ari's take able to say the name, and the setting with its default. This is the part §5 of the brief
is about, and it is the part that stops being about your clients the moment there is a second user.

Separating 9a and 9b costs one extra build and buys you the ability to change your mind about 9b
after seeing 9a work.

### Step 9c — the durable record (prerequisite for both the page and the report)

Today nothing server-side remembers a task. Until that changes, a projects page can show at most the
last 30 days and a "days it spans" number that is quietly wrong. See question 4, item 1.

This creates a permanent server-side record of things you typed. `START_HERE_2026-08-11.md` §4a
already flags that as its own decision — do not fold it into 9a or 9b.

### Step 10a — the projects page

The Control Center screen in §4 of the brief. Needs 9c. Same data as the step 10 report, which is
the point.

### Later — connecting to other software

Not now. See question 4, item 7: nothing above makes it harder.

---

## 2. Every new field, named, with real values

### Local database — schema v26, two additive migrations

`v26 projects_v26` — a new table:

| Column | Type | Example |
|---|---|---|
| `id` | INTEGER PK AUTOINCREMENT | `3` |
| `name` | TEXT NOT NULL | `"Nolan"` |
| `created_at_utc` | TEXT NOT NULL | `"2026-08-13T07:41:22.1030000+00:00"` |
| `archived_at_utc` | TEXT NOT NULL DEFAULT `''` | `""` |
| `order_index` | INTEGER NOT NULL DEFAULT 0 | `0` |
| `schema_ver` | INTEGER NOT NULL DEFAULT 1 | `1` |

Plus `CREATE UNIQUE INDEX idx_projects_name ON projects(name COLLATE NOCASE)`. **This index is not
housekeeping.** Typing "Nolan" into `+ new` twice must not create two projects, or a client's total
silently splits in half — which is the invoice corruption the brief is worried about, arriving by a
different door.

`v26b tasks_project_v26` — one column on the existing table:

| Column | Type | Example |
|---|---|---|
| `tasks.project_id` | INTEGER NOT NULL DEFAULT 0 | `3` — and `0` means untagged |

`0` rather than `NULL`, matching `deferred_until_day`'s `''`-means-no: no nullable handling anywhere,
and every existing row is already correct without being touched.

### Local API (`PortaApiContracts.cs`) — what the cards and the widget read

Added to `LocalApiTask`:

```json
{ "project_id": 3, "project_name": "Nolan" }
```

The name is denormalised onto the task deliberately — the widget repaints every 4s and must not need
a second call to render a row.

Added to `LocalApiTaskCreateRequest`:

```json
{ "project_id": 3 }
```

New action on `LocalApiTaskActRequest`: `"retag"`, carrying `project_id`. New endpoints
`GET /projects` and `POST /projects`:

```json
{ "projects": [ { "id": 3, "name": "Nolan", "archived": false } ] }
```

### Digest payload (agent → control plane) — ONE new field

Added to `DigestTaskPayload` / the TypeScript `DigestTaskEntry`:

```json
"tasks": [
  { "title": "Pricing page", "projectName": "Nolan",
    "plannedSeconds": 7200, "actualSeconds": 4020, "offtaskSeconds": 480,
    "state": "done", "deferredUntilDay": "" },
  { "title": "Invoice chase", "projectName": "",
    "plannedSeconds": 1800, "actualSeconds": 900, "offtaskSeconds": 0,
    "state": "pending", "deferredUntilDay": "2026-08-13" }
]
```

`""` = untagged, and also what is sent when the setting is off.

**There is deliberately no `byProject` aggregate field.** The email sums the block from the tasks it
is already rendering, so the block and the list cannot disagree. That is the same lesson as
`summarizeDay`: two implementations of one number is how the card and the email ended up
contradicting each other last week.

Control-plane validation to match, in `agentController.ts:120` (`parseTasks`): accept `projectName`,
trim, `.slice(0, 40)`, default `''` — same shape as the existing `deferredUntilDay` guard.

### Narrative payload (agent → control plane → Anthropic)

Added to `NarrativeTaskPayload`, same validation in `narrativeService.ts`:

```json
{ "title": "Pricing page", "projectName": "Nolan", "state": "done",
  "plannedSeconds": 7200, "actualSeconds": 4020 }
```

Rendered into the fact sheet as:

```
- "Pricing page" [Nolan] — finished; planned 2h, actually worked 1h 7m
```

### The email — where the block goes

Between the timeline and the tasks, i.e. between `buildTimelineHtml(p)` and `buildTasksHtml(tasks)`
at `digestEmailBuilder.ts:1369`. Rendered only when at least one task carries a name; `Untagged`
appears only alongside tagged work; one project and nothing else omits the block, exactly as §3 of
the brief specifies.

### The setting

Stored in the local `runtime_state` key/value table under `projects.share_names.v1`:

```json
{ "enabled": true, "updatedUtc": "2026-08-13T07:41:22Z" }
```

**Not the `settings` table** — *(verified)* `PruneIfNeeded` runs `DELETE FROM settings WHERE
timestamp_utc < cutoff`, so a preference stored there evaporates after the retention window.
`runtime_state` is not pruned and is the table the signup-nudge state already uses.

The default lives in one constant, `ShareProjectNamesDefault = true`, with the date and the reason
in a comment beside it — so choosing a different default for other users before a fleet release is a
one-line change, which is what §5 of the brief asks for.

When it is off, `projectName` is sent as `""` everywhere. The email then sees an all-untagged day and
omits the block by the rule that already exists. Nothing else changes and nothing breaks.

### Step 9c, named now so it is not invented later

A new server table `daily_task_outcomes`: `account_id`, `date_local`, `title`, `project_name`,
`planned_seconds`, `actual_seconds`, `state`, `deferred_until_day`. *(verified)* `daily_stats` has 32
columns and none of them is tasks, so this is genuinely new ground and a decision of its own.

---

## 3. The height cost — measured, not estimated

Measured on your display through the real card: work area **672** DIPs, card width **380**, so the
card's own ceiling is **632** (`MaxHeight = work.Height − 2 × 20`). Baseline is a three-task morning
card with yesterday's three rows above it. *(All rows verified by measurement, except the marked one.)*

| State | Card height | Room left |
|---|---|---|
| Today — three tasks, yesterday, no picker | **566.4** | 65.6 |
| A chosen project chip sitting on the duration row | **566.4** | 65.6 — **zero cost, as required** |
| One picker open, one row of chips (up to ~5 incl. `+ new`) | **602.0** | 30.0 |
| One picker open, two rows (6–7 chips) | **631.6** | 0.4 |
| One picker open, three rows (8+ chips) | **clamped at 632** | **over — see below** |
| `+ new` text box open instead of the chip row | **606.7** | 25.3 |

**So: yes, the budget breaks — at seven or more chips in the open picker.** And it breaks badly. The
card has **no scroll view** *(verified — the only `ScrollViewer` in the file is inside the text-box
template)*, so going over does not scroll, it clips: at 8 chips the bottom of **"Start the day" sits
12.75 DIPs below the bottom of the window.** The button you must press to start your day is the first
thing lost.

Two things follow, and both need deciding before anyone builds:

1. **Only one picker may be open at a time.** Three open at once would be 566.4 + 3 × 35.6 = **673**
   against a 632 ceiling. *(inferred — arithmetic on the measured single-picker cost.)* Opening one
   must close the others.
2. **The open list must be capped and must wrap.** Recommend the 5 most recently used projects plus
   `+ new`, with anything beyond that reachable from Control Center. A wrapping panel is required
   regardless: the row used today is a horizontal stack, which **clips rather than wraps**
   *(verified)*.

Width, since it constrains the same row: the duration row has **347** DIPs, `1h/2h/3h` use **116.5**,
leaving **230.5** for the project chip. Measured chip widths: `+ project` **65.5**, `Nolan` **50.2**,
`Northgate Property Trust` **141.8** — all fit. Names past roughly **38 characters** overflow, and
because the row is a stack they would be silently cut rather than ellipsised, so the chip needs a
max width and a "…".

---

## 4. What in the brief fights something already built

**1. The projects page has no data to stand on. (verified — the biggest gap in the brief.)**
Locally, closed tasks are deleted after 30 days (`TaskDoneRetentionDays = 30`, cleaned up as a
piggyback on every task read). Server-side there is nothing at all: `daily_stats` has no tasks
column, and the digest's task list is rendered into the email and thrown away. So "total time and
the number of days it spans" is capped at 30 days on the machine and is zero days on the server. The
page needs step 9c first, and 9c is a real decision about a permanent record, not a schema chore.

**2. Time splitting is already impossible. Re-tagging is the actual risk. (verified.)** Only one task
can be active, and time is credited solely to the active one; starting another returns the previous
one to pending with its minutes intact. A minute therefore belongs to one task and one task to one
project, everywhere, and adding projects does not change that. What *is* new: change a task's project
after the time was measured and every already-measured minute moves with it — including minutes
already sent in last night's email. Your call, and it must be a call:
  - **(a)** the project can always be changed, and every report means "as tagged right now";
  - **(b)** once a task has been active, its project is fixed.

  I recommend **(a)** — correcting a mis-tag is exactly the kind of thing you will want on a Friday
  afternoon — with the email treated as a snapshot of that night. But it should be chosen, not
  discovered later from a client's invoice.

**3. The widget has no room for the prefix. (verified.)** The widget is 200 DIPs wide and the task
title is 9px with a **96 DIP** hard cap and character ellipsis — roughly 20 characters. `Nolan · `
eats about 40% of that; a longer name leaves nothing. §3 of the brief says "a quiet muted prefix",
but at this size the prefix would push the task name out of view. Three options, your pick:
  - show the prefix only when a single task is on the widget;
  - a 3–4 character abbreviation stored per project;
  - no prefix at all, project on hover only.

  I recommend the third — the widget's job is the countdown, and the project is never in doubt while
  the task is running.

**4. Enter on the morning card starts the day. (verified.)** Every morning box responds to Enter by
submitting. The `+ new` project box sits inside that same row, so unless it swallows Enter, typing a
project name and pressing Enter will start the day with a half-created project. Easily solved, but
it is precisely the class of bug that cost a week over the caret, so it is named here rather than
found later.

**5. The green rule: no conflict. (verified.)** Chips are already neutral — `#2E353E` when selected,
transparent with a hairline when not — and a test fails if a chip goes green. A dashed muted
`+ project` chip is new styling but not a new meaning for the colour.

**6. Duplicate detection needs a rule. (inferred.)** Re-confirming an open task today starts the
existing task instead of creating a second one, and it matches on title. "Same title, different
project" now has to mean something. Recommend: keep matching on title, and if the user picks a
different project on a matching row, re-tag the existing task rather than create a twin.

**7. Nothing here makes the integrations harder later. (inferred, low risk.)** A project is an id, a
name and tasks pointing at it. Linking one to an outside system later is two more columns on
`projects` (`external_source`, `external_id`), not a redesign. The single thing that *would* make it
harder is inventing per-project time that is not the sum of its tasks — so don't; keep every project
number derived from tasks, which is also what keeps the invoice honest.

---

## What I need from you before anything is built

1. **9a first, or 9a and 9b together?** (Recommend separately.)
2. **Re-tagging** — option (a) or (b) in item 2 above.
3. **The widget prefix** — which of the three options in item 3.
4. **The picker cap** — 5 recent projects plus `+ new` on the card, rest in Control Center?
