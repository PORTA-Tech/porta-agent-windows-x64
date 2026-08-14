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

**ANSWERED 2026-08-13 (Yadin): a client-facing label per task, with a per-project default — and the
project itself gets a client-facing name too, learned the same way. See §12.7.**

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
- **Any single sent report can be deleted, on the server as well as locally.** Added by Yadin,
  2026-08-13. Reports are kept indefinitely, which is only defensible while he can end that for any
  one of them. The rules, including why a half-delete must never report success, are in §12.6.5.

---

## 6. Sending — Resend, in the user's name

**REVISED 2026-08-14 (Yadin). This supersedes the Gmail decision below it.** Sending goes through
**Resend**, which the product already uses for the nightly digest. Not Gmail OAuth.

What the client sees:

| Field | Value |
|---|---|
| From name | **The user's own name** — whoever they are, per user |
| From address | `reports@useporta.com` |
| Reply-to | **The user's own email address** — per user |

So the client reads the user's name first, and hitting reply reaches that user. The domain underneath
is Ari's.

**`reports@`, not `ari@`.** Same domain — `useporta.com`, verified 2026-08-14 from the live digest
in Yadin's inbox. A separate mailbox because mail providers score sending addresses separately: if
the nightly digest is ever marked as spam, client reports must not inherit that reputation, and vice
versa. `ari@useporta.com` stays as it is for the digest.

**Why this replaced Gmail.** Resend cannot send *from* `@gmail.com` — nobody can, without controlling
Google's domain; a service claiming otherwise is spoofing and the mail lands in spam. The only
genuine "from the user's address" route was Gmail OAuth, and its true cost was:

- `gmail.send` is a **sensitive scope**. In testing mode Google expires the refresh token every
  **7 days**, so the user reconnects weekly.
- Only manually-listed test users can connect at all until the app passes **Google verification** —
  consent-screen review, scope justification, a demo video — which takes weeks.
- Ari would hold a token able to send mail as the user. The most valuable secret the product has
  ever held.

Resend costs none of that, works today, and needs **nothing from the user** — no console, no consent
screen, no reconnect. Verified 2026-08-14; see the sources in the Cowork thread.

**The trade Yadin accepted:** the address is not literally the user's. If a client ever remarks on
the domain, that is the signal the Gmail work is worth doing — and by then it will be known to be.

**One consequence to watch as it grows:** every user's reports ride Yadin's domain reputation. At 18
installs this is nothing. If one user's reports were ever marked as spam it would affect everyone's.

### 6a. Where the send happens — and why this merges C and D

**The control plane sends it, not the machine.** The Resend key lives on the server and must never
reach a user's machine, so the agent cannot call Resend directly.

That collapses the phase split: the report payload has to reach the server before it can be sent, so
**the server record (phase D) and the send (phase C) are the same trip.** Build them together.

The local-only encrypted Gmail token decision (§12.6.2) is **moot** and can be struck — there is no
token.

**Address book.** Ari remembers client addresses so they aren't retyped. Editable at any time. The
risk is not storing a wrong address, it is *sending* to one — which cannot be undone. So:
**a confirmation step before sending that shows the recipient address in full.**

### 6b. The confirm screen — DECIDED 2026-08-14

**All three addresses on one screen, each editable in place, remembered after the first time, never
asked again.** Yadin: the confirm step exists to show the recipient in full, so it is already the
right place for the other two — the same learned-once pattern as the client-facing project name
(§12.7).

| Shown | Comes from | Edited |
|---|---|---|
| **From name** | the account's `displayName` | in place; the edit updates the account profile |
| **From address** | the constant `reports@useporta.com` | not editable — it is the product's |
| **Reply-to** | the account's `primaryEmail` | in place; the edit updates the account profile |
| **To** | the client picked from the address book | in place |

**An edit writes to the account profile, not to a local override.** That is what keeps §12.2 free of
sender fields: there is one copy of the user's identity, the server's, and the confirm screen is a
view onto it. A local override would recreate exactly the two-copies-that-disagree problem removing
`senderName` was meant to solve.

**No email-verification flow.** Yadin, 2026-08-14: it is a feature of its own and would block the
first send by weeks. The address is shown plainly instead — *"if it's wrong I'll see it"* — which is
the same principle already accepted for the recipient's address, and the recipient's is the one that
cannot be undone.

**One hard block, not a warning: a `@porta.internal` address can never be Reply-to.** The identity
service assigns `displaced-<accountId>@porta.internal` when two accounts collide on an email
(`identityController.ts:79`), so a profile can hold a synthetic address that no one reads. A client
replying to it is a **guaranteed silent failure** — the reply goes nowhere and neither party learns
that it did. That is not a judgement for the user to make on a confirm screen. **Refuse the send and
say why.**

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

**The setting must say what it is doing in the words of §12.0** — that the recipient is a third
party who never installed Ari, what is held about them, for how long, and that any single report can
be deleted on both sides. §12.0 is the fuller statement and it is the one to write from.

---

## 11. Still open, to answer when building

1. ~~Does a project carry a default naming style, or is every client-facing label per task?~~
   **ANSWERED 2026-08-13** — per task, with a per-project default. Written up in §3.
2. What exactly goes to the server, field by field, with real example values — the standing rule.
   **DRAFTED 2026-08-13, in §12. Partly settled.**
   **ANSWERED IN FULL 2026-08-13.** §12.2 and §12.3 approved as drafted; §12.4, §12.6 and §12.7
   decided; §12.0 names the line the feature crosses. The standing rule is satisfied — **the field
   list is the approved one, and no field outside it may be added without going through this
   again.** The build brief is `Step10_Report_BuildBrief.md`.
3. What the Reports page looks like. Yadin has also noted that some existing Control Center pages
   should be removed; that is a separate conversation when Control Center is worked on.

---

## 12. What leaves the machine — field by field. DRAFT, NOT APPROVED

Written 2026-08-13 against the code as it stands at `69beaa5` / 1.2.149. This is the standing rule
being honoured: **name every live-data write before making it, and wait for the go.**

**§12.2 and §12.3 APPROVED by Yadin, 2026-08-13**, as drafted. Two things he noted rather than
changed, recorded so they are not quietly reopened later:

- **No raw task titles in the payload — only the labels he wrote.** The private note stays on the
  machine; what the client is shown is the only thing stored.
- **No flag for excluded lines.** In his words: *"a record of what I chose not to show a client is
  itself a thing sitting on a server."* An excluded line is absent, not marked absent.

### 12.0 The line this feature crosses — third-party data

**Named here at Yadin's instruction, 2026-08-13, because it should be written down rather than
understood.**

Everything Ari has ever held is the user's own data, about themselves. They installed it, they can
see it, they can delete it, and every argument about privacy in this product so far has been about
someone's relationship with their own machine.

**The recipient's email address is not that.** It belongs to a person who never installed Ari, never
agreed to anything, was never asked, and has no way to know the record exists. Today that person is
Yadin's client. The moment Ari has a second user it is *their* client — other companies' contacts and
work records, held on Yadin's server.

That is the line. Not the volume of data, and not its sensitivity — the fact that for the first time
the subject of the data is not the person who chose to run the software.

What follows from it, and none of it is optional:

- **Minimise.** Exactly one address per sent report, because you cannot send without it. The address
  book stays on the machine (§12.4). There is never a server-side list of a user's clients.
- **It is used for one thing.** Sending that report. Never analytics, never enrichment, never
  suggestions, never anything aggregated across users. It does not become a product input.
- **It is deletable, on both sides, per report** (§12.6.5) — the only way a third party's data can
  be held indefinitely and still be defensible.
- **The setting in §10 says this plainly**, in the words above, at the moment it is switched on. Not
  in a policy document nobody opens.
- **The answer to "what do you hold about me?" has to be short and true**: one email address and one
  work report, sent to you by the person you hired, deletable on request.

If a future change would put a third party's data anywhere else — a second field, another table, an
analytics event, a backup with a different retention — **that is a new decision of the same size as
this one**, and it gets named the same way before it is built.

---

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
> **Written 2026-08-13 on Yadin's instruction** — the recommendation was accepted and his "delete
> it" was narrowed by him: `DigestStaysProjectFreeTests`. It walks every payload type reachable
> from `DailyDigestReportPayload` and `NarrativeRequestPayload` and fails if any of them gains a
> field mentioning a project, plus a test asserting the walk actually reaches the nested payloads —
> a walk that quietly reached nothing would pass forever and guard nothing. The report payload is a
> different root and is deliberately not constrained.
>
> **Verified by breaking it:** adding `projectName` to `DigestTaskPayload` fails two of the four
> tests; removing it again passes all four.

### 12.2 The report, when the user taps Send — APPROVED 2026-08-14

One POST, at the moment of sending, never on a schedule.

**REVISED 2026-08-14 for the server-side send, and approved as revised.** Two fields left and one was
renamed; nothing was added.

> **Every value below is a placeholder.** No name, address or domain belonging to any particular user
> appears anywhere in the product — enforced by `NoPersonalIdentifiersShipTests`, which also scans
> comments and sample payloads for email-shaped literals, precisely because *"an address in a comment
> is an address someone pastes into code later"* (Yadin, 2026-08-14). The founder's own address was
> in this block until that date; it is a placeholder now for the same reason.

```json
{
  "reportId":              "7f3c1b90-2a44-4f0e-9d61-0c2b5a8e13d7",
  "accountId":             "acct_9f21c8",
  "deviceId":              "dev_4c7a1e",
  "machineId":             "mach_1b93f0",
  "requestedAtUtc":        "2026-08-20T11:42:07.0000000+00:00",
  "timezoneOffsetMinutes": 240,

  "projectName":           "Nolan Studio",
  "rangeStartLocal":       "2026-08-12",
  "rangeEndLocal":         "2026-08-19",

  "recipientEmail":        "accounts@example-client.test",
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

**What the server adds, and why it is not in the payload.** The From name is the account's
`displayName`, the Reply-to is its `primaryEmail`, and the From address is the constant
`reports@useporta.com`. All three are resolved server-side at send time from the account profile the
control plane already holds and already uses to address the nightly digest.

`senderName` and `senderEmail` were in the draft and are **gone**: two copies of an identity can
disagree, and a client asserting its own sender identity is worse than a server reading its own
record. One source, no drift.

**`sentAtUtc` became `requestedAtUtc`** for the same reason. The agent no longer knows when the mail
went — it knows when it asked. The server records when Resend accepted, and a dispute is answered by
the timestamp of a real delivery.

Field by field, and what each one actually discloses:

| Field | Example | Why it must leave | What it reveals |
|---|---|---|---|
| `reportId` | `7f3c1b90-…` | ties the stored snapshot to the local record | nothing |
| `accountId` / `deviceId` / `machineId` | `acct_9f21c8` | already sent with every payload | nothing new |
| `requestedAtUtc` | `2026-08-20T11:42:07Z` | the server's own `sentAtUtc` is what answers a dispute; this is when the machine asked | when the user was working |
| `projectName` | `Nolan Studio` | it is the report's headline | **a client's name — new.** The client-facing name per §12.7, never the local one |
| `rangeStartLocal` / `rangeEndLocal` | `2026-08-12` | the period being billed | the shape of an engagement |
| ~~`senderName` / `senderEmail`~~ | — | **REMOVED 2026-08-14.** The server resolves both from the account profile | — |
| `recipientEmail` / `recipientName` | `accounts@example-client.test` | you cannot send without it | **a third party who agreed to nothing — new** |
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

### 12.4 The address book, and the Gmail permission — DECIDED 2026-08-13

These are the two pieces that are not report data, and Yadin decided them separately from §2's
"the server holds everything".

**The address book — LOCAL ONLY.** It is a list of other people's names and email addresses. Only
the one address on the report being sent ever leaves. Keeping the book local means a server breach
cannot spill a client list. Storing it would buy convenience across machines, which is worth nothing
at one user. **Unchanged by the Resend decision.**

> ### REVERSED 2026-08-14 — the send moved to the server
>
> Everything struck through below was true while Gmail OAuth was the plan. Resend's API key lives on
> the server and must never reach a user's machine, so the control plane sends (§6a). **This is the
> guarantee that decision cost, written down rather than quietly dropped, so the next person sees
> what was given up and not only what was chosen.**
>
> ~~**The Gmail refresh token — LOCAL ONLY, encrypted like the device credential.** A token that can
> send mail as the user is the highest-value secret this product has ever held. Server-side, one
> breach sends mail as *every* user; the blast radius is not "data leaked" but "mail sent from your
> address". Locally it goes through the same DPAPI path as the device credential (`Secrets`).~~
>
> ~~Yadin, 2026-08-13, on the one cost: *"only sending while my machine is on is not a cost, I have
> to tap Send anyway."*~~
>
> ~~So **the send happens from the machine**, and the server receives §12.2 as a **record of what was
> sent**, never as an instruction to send it. **Nothing server-side can cause an email to leave the
> user's address.**~~
>
> **What is lost.** That last sentence was a structural guarantee: no server bug, no server breach,
> and no compromised admin could put a document in a client's inbox, because the server had no means
> to send one. It does now. That is a bigger cost than the `reports@useporta.com` domain in the From
> line, and it is the real price of dropping Gmail — not the address.
>
> **What replaces it**, since a guarantee cannot be replaced by an intention. Three bounds, approved
> 2026-08-14, and each is a property to be tested rather than a rule to be remembered:
>
> 1. **The server never originates a send.** It sends only on an authenticated request from the
>    user's own device carrying that report. No schedule, no queue that retries by itself, no admin
>    path, no endpoint that takes a `reportId` and sends it again.
> 2. **`reportId` is the idempotency key, and the same id never sends twice.** A network retry after
>    a send that actually succeeded would otherwise put a second copy in a client's inbox, and there
>    is no recalling it.
> 3. **The send is recorded before it is attempted.** A crash mid-send leaves evidence rather than a
>    silent gap, which is the only way "did it go?" is answerable afterwards.
>
> The Gmail route remains the honest version of "from the user's address" and is worth doing when a
> client ever remarks on the domain (§6). At that point this guarantee comes back.

### 12.5 Two things deliberately NOT proposed

- **No project name on the nightly digest payload.** That was 9b's `BY PROJECT` block. The report is
  what pays; the digest is an email he sends himself. Adding it is a separate live-data write and
  should be asked for separately, not folded in here.
- **No project list upload.** A project that has never been in a sent report never leaves the
  machine at all. Names arrive at the server only as a consequence of sending a report about them.

### 12.6 The four decisions — ANSWERED 2026-08-13

All four recommendations taken, plus one requirement Yadin added.

1. **The recipient's address is stored on the server.** It is the only field belonging to someone
   who never installed anything, and storing it is what makes *"who did I send this to?"* answerable.
   Yadin: *"a dispute is exactly when I need to know who I sent it to."*
2. ~~**The Gmail token is local**, encrypted like the device credential. §12.4.~~
   **STRUCK 2026-08-14** — sending moved to Resend on the server, so there is no token to hold,
   nowhere to hold it, and nothing to encrypt. What the reversal cost is written into §12.4.
3. **The address book is local.** §12.4.
4. **A sent report is kept indefinitely**, and the setting in §10 **says so plainly** rather than
   leaving it to be discovered.

**5. NEW REQUIREMENT — a specific sent report must be deletable, and that must delete it
server-side too, not only locally.** Yadin, 2026-08-13.

This is the counterweight to keeping reports forever: indefinite retention is defensible only while
the user can end it for any single report. What it means for the build:

- **`reportId` is what makes deletion possible.** It is already in §12.2; it is now load-bearing.
- **Deletion is one action with two halves, and it is not done until both have happened.** A local
  delete that leaves the server copy is exactly the failure `primitive_buckets` already made once —
  "delete my local data" reported success and left 542 MB behind (`START_HERE_2026-08-11.md` §13.2).
  **Do not repeat it.** If the server half fails, say so and leave the report listed as still
  present; never report success on a half-delete.
- **It deletes the recipient's address with it**, which is the point — that field is a third party's.
- **It is a real delete, not a flag.** A `deleted = 1` row still holds the client's name, the
  labels, and the address.
- **The local record of what was sent (§5) goes too.** Keeping the evidence locally while the user
  asked for the report to be gone is the same broken promise in the other direction. If that trade
  is ever considered, it is a decision for Yadin, not a default.
- The existing "delete my local data" path must cover sent reports as well, on both sides.

### 12.7 The project's own name — CLOSED 2026-08-13

**The gap:** §3 gives every *task* a client-facing label because raw titles are private notes — but
§4 puts the **project name** in the header, large, and a project name is typed on a morning card in
the same private hurry. `Nolan` is fine. `nolan - chase invoice` is not, and nothing in the spec
stopped it reaching the client. Yadin: *"the header is the biggest text a client reads."*

**Decided: a project gets a client-facing name too, learned once, the same mechanism as a task.**
Asked for the first time the project appears in a report, remembered forever, never asked again,
editable in Control Center. The project keeps its own name locally, exactly as a task does.

`projectName` in §12.2 is therefore **the client-facing name**, never the local one — the local one
does not leave the machine at all.
