# Brief — the weekly digest, agent half

Read first: `COWORK_START_HERE.md`, `Chat8_Decisions_2026-08-25.md`, `Weekly_Digest_Brief.md`.

The server half is **built and live** — control plane `e170c1d`, deployed 25 August 2026.
This brief is the other half. It is the agent repo, so it means a version bump and an MSI.

---

## Why this is needed at all

The weekly digest cannot be assembled on the server. Four of its eight sections — the
planned/finished resume, what finished, what carried, and the project split — are built from
task and project data, and **none of that exists on the control plane**. `daily_stats` holds
per-day metrics only; the daily digest's task array is rendered into the email and then thrown
away; there is no project store anywhere in that repo.

So the weekly is agent-driven end to end: the agent gathers the week, posts it, and the server
renders and sends it. Until this ships, **no weekly goes out**. That is deliberate — an email
missing half its sections would be worse than none.

---

## What the agent has to do

Once a week, on **Monday at 07:00 local**, POST the week that ended the previous night to:

```
POST /api/agent/weekly-digest
```

Same device auth as `/daily-digest`. **Sunday's daily still sends as normal** — the two are
different emails on different days and neither replaces the other.

The server refuses a week whose dates are malformed, a week that runs backwards, and a week
that has not finished yet. It dedups one weekly per **account** per week, keyed on the week's
end date, in its own log — so a service restart, a second POST, or a second machine on the
same account cannot produce a second email. Keep a local guard too, but do not rely on it:
the in-memory guard is exactly what failed for the daily.

---

## The payload

```jsonc
{
  "accountId": "…", "deviceId": "…", "machineId": "…",
  "weekStartLocal": "2026-08-17",      // Monday,  YYYY-MM-DD, required
  "weekEndLocal":   "2026-08-23",      // Sunday,  YYYY-MM-DD, required — identifies the week
  "timezoneOffsetMinutes": 240,
  "reportedAtUtc": "2026-08-24T03:00:00.000Z",

  "plannedCount": 18,                  // see "the one rule" below
  "finishedCount": 11,

  "trackedSeconds": 148320,            // the week's totals
  "focusSeconds":   114000,
  "awaySeconds":     34320,

  "days": [                            // exactly seven, Monday first
    { "dateLocal": "2026-08-17", "label": "Mon", "focusSeconds": 22320, "worked": true },
    // …
    { "dateLocal": "2026-08-23", "label": "Sun", "focusSeconds": 0,     "worked": false }
  ],

  "finished": [
    { "title": "Rewrite onboarding email", "project": "Northbridge", "seconds": 5100 }
  ],
  "carried": [
    { "title": "Landing page copy", "project": "Northbridge", "seconds": 7800, "daysOpen": 3 }
  ],
  "projects": [
    { "name": "PORTA", "seconds": 52560 }
  ],

  "ariTake": "…",                      // optional, see below
  "missingFields": []                  // any of: tracked | focus | away | bestDay
}
```

Notes on the fields that are easy to get wrong:

- **`worked` is not `focusSeconds > 0`.** A day with tracked time but no focused stretch is a
  worked day that draws a very short bar. A day the machine was off is an empty track. The
  strip's whole job is that you can tell those apart, so the agent must send them apart.
- **`label`** is the three letters the email prints. The server never guesses a locale.
- **`daysOpen`** is the point of the "what carried" block — it is the only number that
  separates *stuck* from *slow*. Count from when the task was opened, not from Monday.
- **`project`** empty means untagged. Untagged work still appears in `finished`/`carried`
  with no project suffix, but it must **not** appear in `projects` — the project split is a
  ranking of where the week went, not an accounting of every minute, and it says so on the
  page. Do not send an "Untagged" or "Other" bucket.
- **`missingFields`** is how you say a sensor had no data. The email prints "no data" rather
  than a confident zero. Use it; do not send zeros for things you did not measure.
- **`ariTake`** is optional. Drafted from result lines and statuses only, **never** from app
  usage — the same locked rule the daily and the report already hold. Omit it and the section
  is dropped; omit it on a week with no outcomes at all and the email says it cannot see how
  the week's work ended rather than guessing.
- **Best day is derived server-side** from `days`. Do not send it.

---

## The one rule that matters most

`plannedCount` and `finishedCount` must be scored through **the same rule as the evening card
and the daily digest** — `UserCheckInTaskBoard.SummarizeDay`, where only state `done` counts as
finished. That is why the server takes them as numbers instead of counting `finished.length`:
if the weekly and the daily could disagree about what "finished" means, the whole thing is
untrustworthy, and that is the bug this section exists to avoid.

`finished` may legitimately be shorter than `finishedCount` if you cap the list — the resume
is the score, the list is the evidence.

---

## Where the data probably lives

Pointers, not a design — the query is yours to write:

- Tasks already carry project, status, result line, colour slot and measured seconds in
  `SqliteBrainStateStore`. The client-report path (`AgentReportDraft` / `AgentReportLine`,
  built per project over a date range) is the closest existing analogue and is worth reading
  before writing anything new.
- Per-task-per-day time only began recording on **17 August 2026**. Weeks before that have
  day totals but no per-task split. If a week predates it, send what you have and use
  `missingFields` — do not backfill and do not infer.
- The weekly is per **account**, across all projects. The report draft is per project. That is
  the main shape difference.

---

## Prove it

- Build the whole solution and run all five test projects.
- Post a real week to the live endpoint from a dev build and read the email on a phone. The
  server renders it; the thing to check is whether the numbers are true, not whether it lays
  out — that is already verified at 390px and 600px with the stylesheet stripped.
- Check the strip specifically: a day the machine was off must be an empty track, and a week
  with fewer than three worked days must come through as a sentence naming every day.

---

## Not in scope

Do not build a server-side fallback. Do not change the daily. Do not move the Monday 07:00
decision — it is Yadin's, made 25 August 2026, and the agent owns that clock because only the
agent knows the user's local Monday.

**Still open, unrelated to this build:** the digest renders dark, and Gmail on Yadin's phone
inverts it to light. He flagged it on the weekly mockup himself. It affects the daily too, and
it is a decision rather than a bug.
