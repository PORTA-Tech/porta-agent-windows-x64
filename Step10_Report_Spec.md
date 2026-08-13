# Step 10 — The report

Written 2026-08-13, revised the same day with Yadin's answers. **This is a specification, not a
build brief.** Nothing here is to be built yet.

Read `START_HERE_2026-08-11.md` for project state and `Projects_Step9_Brief.md` for what projects
already do.

---

## 1. Why this exists

Ari today asks the user questions and hands back facts about themselves. Effort in, information out,
which nobody pays for. The report is the inversion: the user does their work, and Ari writes the
thing **someone else** is waiting for, which the user approves with one tap.

Everything from steps 3 to 9 exists to make this possible: real tasks, measured hours, done or not,
grouped by project.

---

## 2. The decisions, settled

Yadin, 2026-08-13. These are answers, not proposals.

| Question | Answer |
|---|---|
| Who receives it | A **client paying for hours** — so every number must survive being disputed |
| How often | **On demand.** A button, not a schedule |
| How it arrives | **Email, sent genuinely as the user** — not as Ari. See §6 |
| Client-facing names | **Learned once per project**, not edited per report |
| Time remaining on unfinished work | **Not shown.** See §4 |
| Money | **Never in the client's copy.** Rate per project, visible only to the user in Control Center. See §7 |
| Where the user drafts it | A **new Reports page in Control Center** |
| Server storage | **Yes — the server holds everything.** Yadin's decision, 2026-08-13 |
| Integrations | **Later.** After three real reports have been sent. See §9 |

**The phase labels 9b and 9c are retired.** They existed to defer one decision — whether client
names leave the machine — until Yadin had lived with projects. Choosing "Ari emails a report to a
client" made that decision. What leaves the machine is now one decision, made once, named field by
field, as part of building the report.

---

## 3. Client-facing names, learned once

Task titles are not fit for a client to read. The real ones on this machine are:

- `finish my writting part "all the way in"`
- `continue to improve the system`
- `working on appolo leads`

Private notes with typos. A task title can also name a different client, an internal problem, or the
reason a day went badly. None of that belongs in a paying client's inbox.

So, per Yadin: **Ari learns a client-facing label once, the way projects themselves are learned.**
The first time a task appears in a report, the user gives it a client-facing name. It is remembered
against that task forever, and never asked again.

Rules:

- The hours are **never** editable. Only the label.
- Any line can be excluded from a report. Excluding removes its hours from the total, and the total
  reflects that.
- Renaming affects reports only. The task keeps its own name locally, forever.

**ANSWERED 2026-08-13 (Yadin): a client-facing label per task, with a per-project default.**

The project carries one setting — *use client-facing labels* or *use the raw title* — and a label on
a task overrides it. Purely per task means re-labelling every new task forever; purely per project
means one line that needs different wording can never have it.

What that settles for the build:

- **The project default decides what a new task starts as**, and nothing else. It is never applied
  retroactively to a task that already has a label.
- **A task label, once given, wins forever** — including if the project default changes afterwards.
  Nothing the user has explicitly written for a client is ever overwritten by a setting.
- **A project set to "raw title" still lets any single line be relabelled.** The setting removes the
  asking, never the ability.
- On a project set to "client-facing labels", a task with no label yet is **asked for once**, at
  draft time, in the Reports page. It is never asked again, and it is never asked on a card — the
  morning card must not become a form (`Projects_Step9_Brief.md` §10).

---

## 4. What the report contains

The approved shape, from the mockup Yadin reviewed on 2026-08-13.

1. **Header** — `WORK REPORT`, the project name large, then date range and the user's name.
2. **Three figures** across the top: hours, days worked, completed (`4 of 5`).
3. **Hours by day** — a bar per day across the range.
4. **The work** — one row per task: a check if finished, the client-facing label, a bar proportional
   to the largest line, and hours.
5. **The honesty line** — see §5.
6. **`Measured by Ari · useporta.com`** as the only Ari branding, at the foot. The user's name is at
   the top, where it belongs. The client can follow the link to Ari's site.

**Deliberate choices in the design:**

- **Hours as decimals — `6.2h`, not `6h 12m`.** That is the language of timesheets and invoices,
  which is what the client is checking against.
- **Days not worked appear as gaps, not skipped.** A chart that hides empty days is the kind of thing
  a client notices, and it costs trust permanently.
- **Unfinished work shows `in progress` and the hours so far — never a time remaining.** The estimate
  was set by the user in a morning card for themselves, not promised to the client. A remaining
  figure in a client's inbox becomes a commitment that was never made.

**Not in the client's copy, deliberately:** focus quality, pressure, distraction, app breakdown,
how the day felt. Those belong to the user. The projects page shows them because it is for the user.
A client is owed an account of work delivered, not a record of someone's inner life.

**Design bar:** the report is the moment the product pays off. It should look like something a
professional sends a client — clear, confident, expressive, with the graphs carrying real meaning.
Same values as the cards: tabular right-aligned numbers, one accent colour, labels quieter than
values, two weights, no gradients or shadows.

---

## 5. Defensibility — the rules that make it survivable

A client can dispute an invoice. Every rule below exists so the answer is "here is the measurement"
rather than "here is my recollection".

- **Every number is measured. Nothing is estimated, ever.** Already the locked decision, and the
  whole product.
- **The report says what the number means.** Ari measures present-and-working time, which in the one
  live measurement was ~64% of wall clock — i.e. it *under*-counts against a stopwatch. The client
  is told plainly: *"Time is recorded automatically while the work was actively in progress. Breaks
  and time away are not included."*
- **Untagged time is excluded and, if significant, declared.** Work not assigned to this project is
  not in the total.
- **Unrecognised app time never appears.** Already the rule for the digest.
- **The report never sends by itself.** Locked. One wrong number in a client's inbox costs the
  relationship permanently; the tap costs two seconds.
- **A sent report is a snapshot** — stored as sent, never regenerated. Re-tagging afterwards changes
  the next report, never a delivered one. Same rule as the digest.
- **A local record of exactly what was sent**, including the labels, the figures, the recipient and
  the timestamp. Yadin, 2026-08-13: *"there should be."* Without it a challenge cannot be answered.

---

## 6. Sending — as the user, not as Ari

**Yadin's decision: the report sends genuinely from the user's own address**, not from
`ari@useporta.com`. A document adjacent to an invoice arriving from a domain the client has never
heard of is a trust problem before a word is read.

This means connecting the user's email account (Gmail OAuth for this machine). Consequences to be
named plainly in the UI at the moment of connecting:

- **Ari would hold permission to send mail from the user's account.** That is a real permission and
  the user must grant it deliberately.
- Replies then reach the user naturally, because the mail genuinely is from them. **The reply-routing
  question disappears** — Yadin was right about this and the earlier suggestion of "from Ari with
  reply-to" is dropped.

**Address book.** Ari remembers client addresses so they aren't retyped. Editable at any time. The
risk is not storing a wrong address, it is *sending* to one — which cannot be undone. So:
**a confirmation step before sending that shows the recipient address in full.**

---

## 7. Money — the user's side only

**Never in the client's copy.** A document carrying a money total starts being treated as an invoice,
and invoices carry tax numbers, payment terms and country-specific requirements. That is a different
product and established tools already do it.

**What is in scope:** an optional rate per project, stored locally, **shown only to the user in
Control Center**. Ari does the arithmetic — `18.4 hours × rate` — and the user carries that figure
into whatever they already invoice with. The client receives defensible hours; the user gets the
multiplication done.

**Explicitly out of scope:** invoicing, invoice numbers, payment terms, before-or-after payment,
currency conversion, tax or VAT handling.

---

## 8. What it needs that does not exist yet

| Needed | State |
|---|---|
| Tasks belong to projects | **Done** (9a, first shipped 1.2.143; on midday and evening rows in 1.2.149) |
| Measured hours per task | **Done** |
| Per-app history | **Done, but read the row below before planning a date range** |

> ### Project history genuinely starts 2026-08-12. Do not build a report expecting more.
>
> The rollup is per-app per **hour**, not per day, and it was backfilled to **2026-07-30**. That
> backfill is the APP dimension only: every reconstructed row carries `task_id = 0` and
> `active_seconds` alone, flagged `backfilled = 1`, with no focused/off-task split *(verified in
> `Schema.cs:704`)*.
>
> **So none of the history before 2026-08-12 can be attributed to a project or a task, and no later
> fix can recover it.** That is deliberate, not a gap to be closed: `input_signal_windows` has no
> task column, and a task switched away from and resumed looks continuous, so attributing that time
> would invent exactly the number a client's invoice must never contain.
>
> Consequences for the report:
>
> - A range starting before 2026-08-12 has **no project hours** for those days. It must show them
>   as days with nothing recorded, the same way §4 requires days not worked to appear as gaps.
> - The first report worth sending a client covers **2026-08-12 onwards**.
> - The days a report can honestly cover grow by one per day from that date. Nothing else does.

| Client-facing labels, learned once per task | Not built |
| Project and report data on the server | Not built — **now wanted**, per §2 |
| A Reports page in Control Center | Not built |
| Connecting the user's email account | Not built |
| Address book plus a confirm-before-send | Not built |
| A local record of every report sent | Not built |
| Optional rate per project, user-side only | Not built |

---

## 9. Integrations — later, and here is the test

Integrations would add *proof* to a client report: the actual documents, commits and files touched.
That is genuinely compelling for a client.

**The reason to wait is not difficulty.** It is that nobody knows yet whether a client wants it.
**Send three real reports first.** If a client asks *"what did you actually produce?"*, that is the
signal — and it also names which tool to integrate first, instead of guessing.

---

## 10. The setting, and why it matters more now

Carried forward from `Projects_Step9_Brief.md` §10.5, and it grows with this feature.

Today, this routes **Yadin's** clients' names and work descriptions through **Yadin's** server and
mail provider. His data, his call.

The moment Ari reaches anyone else, it routes **users' clients'** names and work descriptions through
the same infrastructure. That is other companies' client lists and work records, held by Yadin.

That is a responsibility to take on deliberately, not to discover afterwards. So: **a setting,
default on for Yadin, with its default for everyone else chosen on purpose before release.**

---

## 11. Still open, to answer when building

1. ~~Does a project carry a default naming style, or is every client-facing label per task?~~
   **ANSWERED 2026-08-13** — per task, with a per-project default. Written up in §3.
2. What exactly goes to the server, field by field, with real example values — the standing rule.
   **DRAFTED 2026-08-13, in §12. Not approved.** Nothing in it may be built until Yadin has gone
   through it and said yes, and §12.6 lists the four things he actually has to decide.
3. What the Reports page looks like. Yadin has also noted that some existing Control Center pages
   should be removed; that is a separate conversation when Control Center is worked on.

---

## 12. What leaves the machine — field by field. DRAFT, NOT APPROVED

Written 2026-08-13 against the code as it stands at `69beaa5` / 1.2.149. This is the standing rule
being honoured: **name every live-data write before making it, and wait for the go.**

Read §12.6 first if you only read one part. It is the four decisions; everything above it is the
detail behind them.

### 12.1 The baseline — what leaves today, so the change is visible

The **payload** column is *verified* — read out of `src/Porta.Agent.Service/Fleet/FleetContracts.cs`,
`DailyDigestReporter.cs` and `DailyStatsReporter.cs` on 2026-08-13. The **stored server-side** column
is *carried over* from `START_HERE_2026-08-11.md` §4a: the control plane is a different repository
and none of it was re-checked here. **Confirm it there before relying on it.**

| Already leaving | Where | Stored server-side? |
|---|---|---|
| Raw task titles, planned/actual seconds, state, `deferredUntilDay` | nightly digest payload, `tasks[]` | **No** — rendered into the email, then discarded |
| Raw task titles, again | narrative payload, `tasks[]` | **No** |
| The goal sentence, derived from task titles | `daily_stats`, as `goal` | **Yes** — this is the one durable server-side record of typed text today |
| Per-hour focused/active/away, per-app labels and seconds | digest + `daily_stats` | Partly |
| **No project name, ever** | — | was asserted by `Step9aNothingLeavesTheMachineTests`, **deleted 2026-08-13** — see below |

Two things follow that matter more than the table:

- **Raw titles already transit the server; they are just not kept.** So the new thing in step 10 is
  not "titles leave" — it is **durable server-side storage**, and a **third party's email address**.
- **The 2026-08-13 decision retires the no-project-names rule**, and the test asserting it was
  deleted the same day on Yadin's instruction, in its own commit.

> **What that deletion cost, stated plainly.** The test guarded the **digest and narrative**
> payloads — not the report, which is new and which those assertions never touched. So deleting it
> unblocked nothing: the report payload in §12.2 could have been added without failing a single one
> of them. What it did remove is the guard that stops a project name drifting onto the nightly
> digest by refactor — and §12.5 recommends the digest stay project-free.
>
> If that recommendation is accepted, the right end state is a **narrower test guarding the digest
> and narrative payloads only**, and it is one small file to write. Not written, because it was not
> asked for.

### 12.2 The report, when the user taps Send

One POST, at the moment of sending, never on a schedule. Real values, from this machine — the
project name and the client are illustrative, everything else is the true shape.

```json
{
  "reportId":              "7f3c1b90-2a44-4f0e-9d61-0c2b5a8e13d7",
  "accountId":             "acct_9f21c8",
  "deviceId":              "dev_4c7a1e",
  "machineId":             "mach_1b93f0",
  "sentAtUtc":             "2026-08-20T11:42:07.0000000+00:00",
  "timezoneOffsetMinutes": 240,

  "projectName":           "Nolan",
  "rangeStartLocal":       "2026-08-12",
  "rangeEndLocal":         "2026-08-19",

  "senderName":            "Yadin Sela",
  "senderEmail":           "insideporta2@gmail.com",
  "recipientEmail":        "accounts@nolan-studio.com",
  "recipientName":         "Nolan Studio",

  "totalHours":            18.4,
  "daysWorked":            5,
  "tasksCompleted":        4,
  "tasksTotal":            5,

  "lines": [
    { "lineId": "a1", "clientLabel": "Chapter three — first draft", "finished": true,  "hours": 6.2 },
    { "lineId": "a2", "clientLabel": "Interview transcripts",       "finished": true,  "hours": 4.9 },
    { "lineId": "a3", "clientLabel": "Structural edit, part one",   "finished": false, "hours": 3.1 }
  ],

  "daily": [
    { "dateLocal": "2026-08-12", "hours": 3.7 },
    { "dateLocal": "2026-08-13", "hours": 5.2 },
    { "dateLocal": "2026-08-14", "hours": 0.0 },
    { "dateLocal": "2026-08-15", "hours": 4.6 }
  ]
}
```

Field by field, and what each one actually discloses:

| Field | Example | Why it must leave | What it reveals |
|---|---|---|---|
| `reportId` | `7f3c1b90-…` | ties the stored snapshot to the local record | nothing |
| `accountId` / `deviceId` / `machineId` | `acct_9f21c8` | already sent with every payload | nothing new |
| `sentAtUtc` | `2026-08-20T11:42:07Z` | a dispute is answered with a timestamp | when he was working |
| `projectName` | `Nolan` | it is the report's headline | **a client's name — new** |
| `rangeStartLocal` / `rangeEndLocal` | `2026-08-12` | the period being billed | the shape of an engagement |
| `senderName` / `senderEmail` | `Yadin Sela` | the report is *from* the user | his own identity |
| `recipientEmail` / `recipientName` | `accounts@nolan-studio.com` | you cannot send without it | **a third party who agreed to nothing — new** |
| `totalHours` … `tasksTotal` | `18.4`, `5`, `4`, `5` | the three figures at the top | hours billed |
| `lines[].clientLabel` | `Chapter three — first draft` | the body of the report | **what he was hired to do — new** |
| `lines[].finished` / `hours` | `true`, `6.2` | the check and the number | delivery against hours |
| `daily[].dateLocal` / `hours` | `2026-08-14`, `0.0` | the bar chart, gaps included | which days he did not work |

**`daily[].hours` of `0.0` is sent, not omitted.** §4 requires days not worked to appear as gaps,
and a gap the payload does not carry cannot be drawn.

### 12.3 What must NEVER appear in this payload

Each of these is a rule with a reason, not a preference:

- **The rate, and any money.** Not as `rate`, not as `amount`, not as a total. §7. A document with a
  money figure becomes an invoice, and this is not one.
- **The raw task title.** `finish my writting part "all the way in"` has no business being stored on
  a server for a feature that renders `clientLabel`. The label is what the report is made of; the
  title stays local, forever, per §3.
- **Focus quality, pressure, distraction, app names, per-app seconds.** §4. None of it is in a
  client's copy, so none of it belongs in a client report's payload.
- **`plannedSeconds` or anything remaining.** §4 — an estimate the user set for themselves becomes a
  promise the moment it reaches a client's inbox.
- **Excluded lines.** A line the user excluded is *gone*, not sent with a flag. A flag is a record of
  what he chose not to show a client, sitting on a server.

### 12.4 The address book, and the Gmail permission

These are the two pieces that are not report data, and they should be decided separately from §2's
"the server holds everything".

**The address book — recommend LOCAL ONLY.** It is a list of other people's names and email
addresses. The send is triggered by the user at the machine, so the server never needs the book, only
the one address on the report being sent. Keeping it local means a server breach cannot spill a
client list. Storing it buys convenience across machines, which is worth nothing at one user.

**The Gmail refresh token — recommend LOCAL ONLY, encrypted, and this is the sharpest one.** A token
that can send mail as the user is the highest-value secret this product has ever held. Server-side,
one breach sends mail as every user; the blast radius is not "data leaked" but "mail sent from your
address". Locally it can go through the same DPAPI path as the device credential (`Secrets`), and it
costs one real thing: **the report can only be sent while the machine is on** — which is already
true, because the user must tap Send.

If the token is local, the send happens from the machine, and the server receives §12.2 as a
**record of what was sent**, not as an instruction to send it.

### 12.5 Two things deliberately NOT proposed

- **No project name on the nightly digest payload.** That was 9b's `BY PROJECT` block. The report is
  what pays; the digest is an email he sends himself. Adding it is a separate live-data write and
  should be asked for separately, not folded in here.
- **No project list upload.** A project that has never been in a sent report never leaves the
  machine at all. Names arrive at the server only as a consequence of sending a report about them.

### 12.6 The four things Yadin has to decide

1. **The recipient's address on the server — yes or no?** It is the only field belonging to someone
   who never installed anything. Storing it is what makes "who was this sent to?" answerable in a
   dispute. Not storing it means the local record answers that instead, and the server holds an
   anonymous report. *Recommendation: store it — a dispute is exactly when it is needed.*
2. **Gmail token local or server?** §12.4. *Recommendation: local.*
3. **Address book local or server?** §12.4. *Recommendation: local.*
4. **How long does the server keep a sent report?** Forever is defensible — it is the evidence.
   *Recommendation: keep indefinitely, and say so in the setting from §10, rather than quietly.*

### 12.7 A gap in the spec, found while writing this

**The project's own name goes to the client and has no client-facing label.** §3 gives every *task*
a label because raw titles are private notes — but §4 puts the **project name** in the header, large,
and a project name is typed on a morning card in the same private hurry. `Nolan` is fine. `nolan -
chase invoice` is not, and nothing in the spec currently stops it reaching the client.

Two ways to close it, both cheap: give a project an optional client-facing name the same way a task
gets one, or let the report header be edited once per project and remember it. **Recommend the
second** — it is the same "learned once, never asked again" mechanism already agreed in §3, and it
adds no new concept.
