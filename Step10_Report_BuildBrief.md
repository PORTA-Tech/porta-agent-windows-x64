# Step 10 — the report. BUILD BRIEF

Written 2026-08-13, against `69beaa5` / 1.2.149. This is the brief; **`Step10_Report_Spec.md` is
the authority** and where any disagreement is resolved. Read it first, then this.

The spec is fully answered as of 2026-08-13 — the field list is approved, the four decisions are
made, and §12.0 names the line this feature crosses. **Nothing here reopens any of it.**

---

## 1. The one rule this whole feature is built to satisfy

A client can dispute an invoice. Every number in a report has to survive that, which means the
answer is always *"here is the measurement"* and never *"here is my recollection"*. Everything below
follows from that, and anything that would weaken it is out of scope no matter how small.

**Second rule, from §12.0:** the recipient's email address belongs to someone who never installed
Ari. It is the first third-party data this product has ever held. Minimise it, use it for one thing,
make it deletable on both sides.

---

## 2. The data floor — read before choosing any default date range

**Project-attributed history begins 2026-08-12.** *(verified: `Schema.cs:704`.)* The rollup is
backfilled to 2026-07-30, but every backfilled row is `task_id = 0` with `active_seconds` alone, so
none of it can be attributed to a project or a task, and no later fix recovers it.

- The Reports page must **not offer a range that starts before 2026-08-12** without saying plainly
  that those days have no project hours.
- Days inside the range with no recorded work are **gaps with a real `0.0`**, per §4 — never
  skipped, never omitted from the payload.
- On 2026-08-13 the honest maximum range is **two days**. This is not a bug to work around; it is
  what "every number is measured" costs, and it grows by one day per day.

---

## 3. Four phases. Each one ships. Nothing leaves the machine until C

The order exists so the first three phases can be lived with before a single byte reaches a client.

| Phase | What it is | What leaves the machine |
|---|---|---|
| **A** | Client-facing labels, project client-facing names, optional rate. Schema v28. **BUILT 2026-08-13 — data layer only, no UI, not installed** | **Nothing** |
| **B** | The Reports page: pick a project and a range, draft, exclude lines, preview the real thing | **Nothing** |
| **C** | Connect Gmail, confirm-before-send, send from the machine | **The email, to the client** |
| **D** | The server record, delete-on-both-sides, the setting | **The §12.2 payload** |

**A and B are the whole product with the wire unplugged.** He can draft a real report from real
hours, look at it, and decide it is right before anything is connected. If B looks wrong, C and D
are wasted work.

### Phase A — the data. BUILT 2026-08-13

> **Done:** schema v28, the four store writes, the models, and 14 tests. **Not done, and not part of
> phase A:** any endpoint, any UI, anything a user can see. Nothing is reachable from Control Center
> until phase B, which is deliberate — this phase is the data and the rules that protect it.
>
> **Not built into an MSI.** There is nothing to look at yet, so there is nothing to install. The
> version is bumped to **1.2.150** so the tree no longer claims 1.2.149, which is on his machine.
>
> Gate at the time: Core 780/784 with the four documented failures allowed by name, UserAgent 476,
> Service 132, ControlCenter 80, Runtime 6.

Schema **v28** (v27 was the head — `Migrations.cs:137`, `app_usage_rollup_v27`).

```sql
-- Learned once, never asked again. Local, and the raw title/name NEVER leaves.
ALTER TABLE tasks    ADD COLUMN client_label TEXT NOT NULL DEFAULT '';
ALTER TABLE projects ADD COLUMN client_name  TEXT NOT NULL DEFAULT '';

-- Per §3: the project default decides only what a NEW task starts as.
-- 0 = use the raw title, 1 = ask for a client-facing label.
ALTER TABLE projects ADD COLUMN label_style  INTEGER NOT NULL DEFAULT 0;

-- §7. User-side only. This column must never appear in any payload, in any form.
ALTER TABLE projects ADD COLUMN rate_minor   INTEGER NOT NULL DEFAULT 0;
ALTER TABLE projects ADD COLUMN rate_currency TEXT   NOT NULL DEFAULT '';
```

`''` means "not set" and `0` means "no rate", matching `deferred_until_day` and `project_id` — **no
nullable columns**, so no caller anywhere handles a null. That is the existing convention and 9a
chose it deliberately.

**The rules that are easy to get wrong:**

- A label, once written, **wins forever** — including after the project default changes. Nothing the
  user wrote for a client is ever overwritten by a setting.
- The default applies to **new** tasks only. Never retroactively.
- A project set to "raw title" can still have any single line relabelled.
- **Re-tagging stays instant and total** — labels hang off the task, so a task moving project takes
  its label with it. Do not copy labels onto a project.

### Phase B — the Reports page

Follow the shape already in the repo. Projects did exactly this in 9a:

- `src/Porta.ControlCenter/MainWindow.Reports.cs` — a partial, beside `MainWindow.Projects.cs`
- the page markup in `MainWindow.xaml`
- Control Center reads the agent **through `LocalApiClient`** and never touches the database
  directly *(verified: `MainWindow.Projects.cs:30`)*
- new endpoints beside the project ones at `LocalApiEndpointMapping.cs:359`

Endpoints, all local:

```
GET    /api/reports/draft?projectId=&from=&to=     build a draft from measured hours
POST   /api/reports/label                          set a task's client-facing label
POST   /api/reports/send                           record + send (phase C)
GET    /api/reports                                the local record of what was sent
DELETE /api/reports/{reportId}                     delete, both sides (phase D)
```

The draft is **derived, never stored** until it is sent. A sent report is a snapshot and is never
regenerated — the same rule the digest already follows.

### Phase C — connecting Gmail, and sending

- **The refresh token goes through `Secrets`** (`src/Porta.Agent.Core/Security/Secrets.cs`), the same
  DPAPI path as the device credential. It never reaches the server. §12.4.
- **The send happens from the machine.** The server cannot cause mail to leave the user's address.
- **The consent screen says what it is granting** — permission to send mail as the user — in plain
  words, at the moment of connecting. Not in a policy page.
- **Confirm before send, showing the recipient address in full.** Sending to the wrong address cannot
  be undone. §6.
- **The report never sends by itself.** Locked, and not negotiable for convenience.

### Phase D — the server record, and deletion

- POST the §12.2 payload **as a record of what was sent**, after the send succeeded.
- `DELETE` removes it **on both sides**. §12.6.5.
- **A half-delete must never report success.** If the server half fails, say so and leave the report
  listed as present. This is exactly the `primitive_buckets` failure — "delete my local data"
  reported success and left 542 MB behind (`START_HERE_2026-08-11.md` §13.2). It must not happen
  twice in the same product.
- Deleting a report deletes **the local record too**, and the recipient's address with it.
- The existing "delete my local data" path in Control Center covers sent reports as well.
- The setting from §10 ships in this phase, worded from §12.0.

---

## 4. The payload — approved, and closed

`Step10_Report_Spec.md` §12.2 is the field list. **It is closed**: no field outside it without
going through the standing rule again.

Never in it, and each has a reason in §12.3: the rate or any money · the raw task title · focus
quality, pressure, distraction, app names · `plannedSeconds` or anything remaining · a flag for
excluded lines.

---

## 5. The tests to write, and the failure each one catches

The house style is to assert against the **serialized** shape, and to prove a guard works by
breaking it. Follow it.

| Test | The failure it catches |
|---|---|
| `TheReportPayloadCarriesExactlyTheApprovedFields` | A field arrives on the wire that nobody approved — the standing rule enforced in code rather than in a document |
| `NoReportPayloadFieldMentionsRateOrMoney` | The one thing that turns a report into an invoice |
| `TheReportPayloadCarriesNoRawTaskTitle` | A private note reaching a client because a label was missing and something fell back |
| `AnExcludedLineIsAbsent_NotFlagged` | A record of what the user chose to hide, sitting on a server |
| `ExcludingALineChangesTheTotal` | A total that no longer matches its own lines — the invoice defect |
| `ADayWithNoWorkIsSentAsZero_NotOmitted` | A chart that hides empty days, which a client notices |
| `ASentReportNeverChanges_EvenAfterRetagging` | The snapshot rule |
| `DeletingAReportRemovesItFromBothSides` | The `primitive_buckets` failure, repeated |
| `AFailedServerDeleteIsNotReportedAsSuccess` | The same failure, wearing a success message |
| `ALabelSurvivesTheProjectDefaultChanging` | A setting overwriting words the user wrote for a client |
| `DigestStaysProjectFreeTests` (exists) | A project name drifting onto the nightly email |

Add the report payload type to nothing that the digest guard walks — it is a separate root, on
purpose.

---

## 6. Design

Same values as the cards (`START_HERE_2026-08-11.md` §4d), plus what §4 of the spec settles:
decimal hours (`6.2h`), tabular right-aligned numbers, one accent colour, labels quieter than
values, two weights, no gradients, no shadows. Gaps for days not worked. `in progress` and hours so
far for unfinished work — **never a time remaining**.

`Measured by Ari · useporta.com` is the only Ari branding, at the foot. The user's name is at the
top.

**Render it and look at it.** Yadin judges design from images. The card preview harness shipped in
1.2.148 for exactly this reason (`--preview-checkins --png`); do the equivalent for the report
rather than describing it in prose.

---

## 7. Process gates — non-negotiable

- **Bump all four version fields in all three `.csproj` as part of the commit that changes code.**
  This has now been broken three times, most recently `69beaa5`.
- **Copy the highest `build-<n>.ps1` to the next and change only the version line.**
  `scripts/build-msi.ps1` is a hard-fail stub.
- **Run the gate in a clean process** before building:
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-gate.ps1`
- **Installing is Yadin's.** Hand over the path and the SHA256, then stop.
- **Nothing reaches the fleet without his explicit go.** Pushing to `main` is safe; a tag, a release
  and a manifest are what ship.
- **Read the exes before writing a version number anywhere.**

---

## 8. Out of scope — do not drift into these

Invoicing, invoice numbers, payment terms, currency conversion, tax · money in the client's copy ·
integrations, which wait until three real reports have been sent · project names on the nightly
digest · scheduled or automatic sending · editing hours, ever.

---

## 9. Owed before the first real send

1. **Re-measure the honesty line.** §5 says Ari measures ~64% of wall clock, from one measurement.
   That sentence goes into a client's inbox and must be re-measured on 1.2.148+ data first, or
   reworded so it claims only what is known.
2. **Confirm the control plane stores what §12.1 says it stores.** That column is carried over from
   an older handover and the control plane is a different repository. Check it there.
3. **`ANTHROPIC_API_KEY` on Render** is still unconfirmed — unrelated to the report, but it is the
   reason ARI'S TAKE silently omits from the digest.
