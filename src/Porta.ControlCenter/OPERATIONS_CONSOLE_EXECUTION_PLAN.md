# Operations Console — Execution Plan for Claude Code

## Context

This plan covers all work needed to make the Porta Operations Console useful and
data-complete. It is split into two parts:

- **Part A — Backend (porta-control-plane repo):** Fix three data gaps that must
  exist before the console can show anything meaningful.
- **Part B — Frontend (porta-operations-console repo):** Update each section of
  the console to show real, current data about Ari agents.

Do not start Part B until Part A is complete and deployed to Render.

---

## Background: what is already correct

The `MachineSummaryReport` contract (vendored at
`vendor/api-contracts-local/dist/dtos/MachineSummaryReport.d.ts`) already
contains all the new Ari fields from May 2025:

- `attentionSegmentKind` — `focus | context_excursion | off_task_distraction | fragmentation_distraction | away`
- `attentionSegmentReasonCode` — free string: `steady_focus`, `context_excursion`, `outside_focus_context`, `fragmentation_distraction`, `away`
- `focusEpisodeQuality`, `focusEpisodeSeconds`, `focusEpisodeState`
- `distractionEpisodeActive`, `distractionEpisodeSeconds`
- `pressureLevel`, `pressureConfidence`
- `breakPendingKind` — `short_break | long_break | null`
- `calibrationStage` — `cold | provisional | calibrated`
- `breakNeeded`

The fleet endpoint `GET /v1/machines/summary` already returns the full
`MachineSummaryReport[]` joined with identity info. The machine actions endpoint
`GET /v1/machines/:machineId/actions` already returns timestamped action events.
The growth endpoints (`/v1/growth/summary`, `/v1/growth/series`,
`/v1/growth/cohorts`) already exist and have RBAC policies for viewer/operator/admin.

**Nothing in the Windows agent or api-contracts needs to change for this plan.**

---

## Part A — Backend: porta-control-plane

### A1 — Add a digest log store and endpoint

**Problem:** When the daily digest fires (or fails to fire), the control plane
logs to console only. There is no persistence, so the operations console has no
way to show "did the digest run tonight and why not if it didn't."

**Where the digest fires:** `src/api/controllers/agentController.ts`, route
`POST /agent/daily-digest` (around line 404). After `sendEmail()` is called, the
result is only `console.log`/`console.warn` — nothing is persisted.

**What to build:**

**1. New file: `src/store/digestLogStore.ts`**

```typescript
export interface DigestLogEntry {
  machineId: string;
  accountId: string | null;
  dateLocal: string;          // "YYYY-MM-DD"
  trackedSeconds: number;
  fired: boolean;
  failureReason: string | null; // null when fired=true
                                // "under_1h" | "email_not_set" | "send_failed" | "account_not_found"
  sentAtUtc: string;            // ISO timestamp of attempt
}
```

Store entries in the fleet Postgres DB (`getFleetPool()`), falling back to a
`Map<string, DigestLogEntry>` in memory when `getFleetPersistenceMode() === 'memory'`.

Create table DDL (run in `ensureFleetPersistenceReady` or as a separate migration):

```sql
CREATE TABLE IF NOT EXISTS digest_log (
  machine_id      TEXT        NOT NULL,
  account_id      TEXT,
  date_local      TEXT        NOT NULL,
  tracked_seconds INTEGER     NOT NULL DEFAULT 0,
  fired           BOOLEAN     NOT NULL DEFAULT FALSE,
  failure_reason  TEXT,
  sent_at_utc     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  PRIMARY KEY (machine_id, date_local)
);
```

Export two functions:
- `appendDigestLogEntry(entry: DigestLogEntry): Promise<void>` — upsert by `(machine_id, date_local)`
- `listDigestLogEntries(limit?: number): Promise<DigestLogEntry[]>` — ORDER BY sent_at_utc DESC, default limit 100

**2. Update `src/api/controllers/agentController.ts` — `POST /agent/daily-digest`**

After the existing `sendEmail()` call (around line 460), call `appendDigestLogEntry`:

```typescript
import { appendDigestLogEntry } from '../../store/digestLogStore.js';

// ... inside the daily-digest handler, after buildDigestEmail + sendEmail:

await appendDigestLogEntry({
  machineId: payload.machineId,
  accountId: accountId ?? null,
  dateLocal: payload.dateLocal,
  trackedSeconds: payload.trackedSeconds,
  fired: sent,                                // sent is the bool returned by sendEmail
  failureReason: sent ? null : 'send_failed',
  sentAtUtc: new Date().toISOString(),
});
```

Also write a `fired: false` entry for the early-exit cases (under 1h tracked,
email not set, account not found) so operators can see why it didn't fire:

```typescript
// Example: under 1h tracked
await appendDigestLogEntry({
  machineId: payload.machineId,
  accountId: accountId ?? null,
  dateLocal: payload.dateLocal,
  trackedSeconds: payload.trackedSeconds,
  fired: false,
  failureReason: 'under_1h',
  sentAtUtc: new Date().toISOString(),
});
return res.status(200).json({ skipped: true, reason: 'under_1h' });
```

**3. New file: `src/api/controllers/digestController.ts`**

```typescript
import { Router } from 'express';
import { listDigestLogEntries } from '../../store/digestLogStore.js';

export function createDigestController(): Router {
  const router = Router();

  router.get('/log', async (req, res, next) => {
    try {
      const parsedLimit = Number.parseInt(String(req.query.limit ?? '100'), 10);
      const limit = Number.isFinite(parsedLimit) && parsedLimit > 0 ? parsedLimit : 100;
      const entries = await listDigestLogEntries(limit);
      res.json(entries);
    } catch (error) {
      next(error);
    }
  });

  return router;
}
```

**4. Register in `src/app.ts`**

Add import and mount:
```typescript
import { createDigestController } from './api/controllers/digestController.js';
// ...
app.use('/v1/digest', createDigestController());
```

**5. Add RBAC policy in `src/api/middleware/auth.ts`**

In the `routePolicies` array add:
```typescript
{ method: 'GET', path: /^\/v1\/digest\/log$/, roles: roleSet('viewer', 'operator', 'admin') },
```

---

### A2 — Add machine uptime history

**Problem:** `agentPresenceStore` only keeps the last heartbeat per agent in
memory — no historical record. The console plan requires a sparkline showing
online/offline state per machine over the last 7 days.

**What to build:**

**1. New file: `src/store/machineUptimeStore.ts`**

```typescript
export interface UptimeSample {
  machineId: string;
  sampledAtUtc: string;
  agentRunning: boolean;
  userSessionConnected: boolean;
  activityDetected: boolean;
}
```

Store in fleet Postgres:

```sql
CREATE TABLE IF NOT EXISTS machine_uptime_samples (
  machine_id             TEXT        NOT NULL,
  sampled_at_utc         TIMESTAMPTZ NOT NULL,
  agent_running          BOOLEAN     NOT NULL DEFAULT FALSE,
  user_session_connected BOOLEAN     NOT NULL DEFAULT FALSE,
  activity_detected      BOOLEAN     NOT NULL DEFAULT FALSE,
  PRIMARY KEY (machine_id, sampled_at_utc)
);
CREATE INDEX IF NOT EXISTS idx_uptime_machine_time
  ON machine_uptime_samples (machine_id, sampled_at_utc DESC);
```

Export two functions:
- `appendUptimeSample(sample: UptimeSample): Promise<void>`
- `listUptimeSamples(machineId: string, sinceUtc: string): Promise<UptimeSample[]>` — returns rows ordered by sampled_at_utc ASC

**2. Sample on every telemetry write**

In `src/api/controllers/agentController.ts`, the telemetry route already calls
`upsertMachineSummary`. After that call, write an uptime sample:

```typescript
import { appendUptimeSample } from '../../store/machineUptimeStore.js';

// After upsertMachineSummary(normalized):
await appendUptimeSample({
  machineId: normalized.machineId,
  sampledAtUtc: normalized.reportedAtUtc,
  agentRunning: normalized.agentRunning,
  userSessionConnected: normalized.userSessionConnected,
  activityDetected: normalized.activityDetected,
});
```

Do NOT sample more often than once per 5 minutes per machine to avoid table
bloat. Add a simple in-memory `Map<machineId, lastSampleTimeMs>` guard:

```typescript
const lastUptimeSampleMs = new Map<string, number>();
const UPTIME_SAMPLE_INTERVAL_MS = 5 * 60 * 1000; // 5 minutes

const lastMs = lastUptimeSampleMs.get(normalized.machineId) ?? 0;
if (Date.now() - lastMs >= UPTIME_SAMPLE_INTERVAL_MS) {
  lastUptimeSampleMs.set(normalized.machineId, Date.now());
  await appendUptimeSample({ ... });
}
```

**3. Add endpoint to `machineController.ts`**

```typescript
import { listUptimeSamples } from '../../store/machineUptimeStore.js';

// Inside createMachineController():
router.get('/:machineId/uptime', async (req, res, next) => {
  try {
    const machineId = req.params.machineId?.trim();
    if (!machineId) {
      res.status(400).json({ error: 'machineId is required' });
      return;
    }

    const parsedDays = Number.parseInt(String(req.query.days ?? '7'), 10);
    const days = Number.isFinite(parsedDays) && parsedDays > 0 ? Math.min(parsedDays, 30) : 7;
    const since = new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
    const samples = await listUptimeSamples(machineId, since);
    res.json(samples);
  } catch (error) {
    next(error);
  }
});
```

**4. Add RBAC policy in `auth.ts`**

```typescript
{ method: 'GET', path: /^\/v1\/machines\/[^/]+\/uptime$/, roles: roleSet('viewer', 'operator', 'admin') },
```

---

### A3 — Fix growth endpoint connectivity from the console

**Problem:** The growth endpoints exist and have RBAC. The console shows
"Connecting to control plane…" indefinitely. The cause is almost certainly that
the console frontend is either not sending auth headers or sending them
incorrectly.

**Investigation step (do first, before changing anything):**

Open the browser DevTools on `porta-operations-console.onrender.com`, go to the
Growth section, and check the Network tab. Find the failing request to
`/v1/growth/summary`. Check:
1. Is the `x-operator-id` header present?
2. Is the `x-operator-role` header present and set to `viewer`, `operator`, or `admin`?
3. What HTTP status is the server returning? 401 = missing/bad auth. 403 = role not allowed.

The `requireAuthenticatedOperator` middleware accepts either a Bearer session
token (`Authorization: Bearer <token>`) OR both `x-operator-id` and
`x-operator-role` headers. The console must be sending one of these two forms.

**If the console is sending the headers but the route is still 403:** Check that
the growth endpoint RBAC entries in `routePolicies` use the exact path the
console is calling. Current entries:
```
GET /v1/growth/summary
GET /v1/growth/series
GET /v1/growth/cohorts
GET /v1/growth/users/:accountId/journey
```

No backend change should be needed here. Fix is in the console frontend.

---

## Part B — Frontend: porta-operations-console

The operations console is a separate React/Vite frontend repo deployed to
`porta-operations-console.onrender.com`. All API calls go to the control plane
base URL via an env var (likely `VITE_CONTROL_PLANE_URL` or similar). All `/v1/`
routes require either `x-operator-id` + `x-operator-role` headers or a
`Authorization: Bearer` session token. Verify which auth method the console
currently uses before making changes.

---

### B1 — Fix the header tagline

**Location:** main layout or header component.

**Current:** "Behavioral regulation command surface for machine truth, trust, and action state."

**Replace with:** "Fleet status and operational visibility for Ari agents."

Also replace the two status badges if they say anything about "behavioral regulation":
- "Control Plane Connected" — keep as is
- "Internal R&D Edition" — keep as is

---

### B2 — System Health: add uptime sparklines

**Current state:** Number cards (Healthy, Degraded, Disconnected, Revoked) + a
machine detail table. This is fine and should stay.

**What to add:** Below each machine row in the detail table (or as an expandable),
add a small uptime sparkline. Data source: `GET /v1/machines/:machineId/uptime?days=7`
(new endpoint from A2).

Each sparkline shows 7 days of `agentRunning` boolean samples. Render as a simple
horizontal bar: green segments = online, grey = offline. Keep it small — it
belongs in the table row, not as a large chart.

The top-level number cards already work. No change needed to those.

---

### B3 — User Activity: full rebuild

**Current state:** Shows only "Break Ready: 0". This section is nearly empty.

**Replace entirely with three components:**

**Component 1: Per-machine live state grid**

Data source: `GET /v1/machines/summary` (already exists, returns `MachineSummaryReport[]`).

Show one card per machine with:
- Machine name + user name
- Current state label derived from `attentionSegmentKind`:
  - `focus` → **FOCUSED** (green)
  - `context_excursion` → **CONTEXT SWITCH** (amber)
  - `off_task_distraction` → **DISTRACTED** (red)
  - `fragmentation_distraction` → **FRAGMENTED** (red)
  - `away` → **AWAY** (grey)
- How long in current state: `attentionSegmentSeconds` formatted as "14m" or "2h 3m"
- Focus today: `focusEpisodeSeconds` formatted
- Pressure: `pressureLevel` as a percentage (0-1 → 0-100%)
- Break pending: if `breakPendingKind` is not null, show a small amber badge "Break suggested"
- Calibration: if `calibrationStage === 'provisional'`, show "Learning" badge; if `'cold'`, show "Warming up"

Poll this endpoint every 30 seconds.

**Component 2: Fleet summary row**

Above the per-machine grid, show 4 aggregate numbers:
- Active right now (machines where `activityDetected === true`)
- In focus (machines where `attentionSegmentKind === 'focus'`)
- Distracted (machines where `distractionEpisodeActive === true`)
- Break ready (machines where `breakNeeded === true`)

**Component 3: Remove the old single "Break Ready: 0" card entirely.**

---

### B4 — Trust Level → rename to Baseline & Calibration

**Current state:** "CONFIDENCE HIGH: 0 Machines above high-confidence threshold."
This maps to nothing meaningful in the current product.

**Replace entirely:**

Rename the nav item and page title from "Trust Level" to "Baseline & Calibration".

Data source: same `GET /v1/machines/summary`.

Show:
- **Ready** — count of machines where `calibrationStage === 'calibrated'`
- **Learning** — count of machines where `calibrationStage === 'provisional'`
- **Warming up** — count of machines where `calibrationStage === 'cold'`

Below the summary numbers, show a table with one row per machine:
- Machine name
- Calibration stage (with a colored badge: green=calibrated, amber=provisional, grey=cold)
- Trust tier: `trustTier` field (low / medium / high)
- Last reported: `reportedAtUtc` as a relative timestamp ("3 min ago")

---

### B5 — Actions: replace counter with a timeline table

**Current state:** "SUGGESTIONS SHOWN: 0" — a single dead counter.

**Replace with a timeline table:**

Data source: `GET /v1/actions/recent` (already exists, returns recent
`MachineActionEvent[]` across all machines).

Show a table with columns:
- Time (`timestampUtc` as relative: "2 min ago")
- Machine name (join from the event's `machineId` → look up in the machine summary list)
- User name (`userName`)
- Action (`action` field — e.g. "stretch_break_suggestion", "break_suggestion")
- Status (`executionState` field — e.g. "executed", "skipped", "pending")

Default: show last 20 events. Add a "Load more" button.

No charts needed here. The table is sufficient.

---

### B6 — Growth: fix connection + verify data loads

**Current state:** "Connecting to control plane… No data yet." on every metric.

**Fix:** Identify how the console sends auth headers (check the API client
utility file, likely `src/api/client.ts` or similar). Ensure `x-operator-id` and
`x-operator-role` headers (or Bearer token) are being sent on growth endpoint
calls the same way they are sent on machine endpoint calls. If machine endpoints
work but growth doesn't, the auth headers are fine — the issue is something
specific to the growth API call (wrong base URL, missing path prefix, etc.).

Once the connection works, the existing Growth UI structure (trend charts, cohort
retention table, user journey drilldown) is already in place and just needs data.
No structural changes to the Growth section are needed.

---

### B7 — Add new Digest section

**This section requires A1 to be complete and deployed first.**

Add a new nav item "Digest" between "Actions" and "Growth".

Data source: `GET /v1/digest/log` (new endpoint from A1).

Show:
- Page title: "Nightly Digest Log"
- Subtitle: "Ari fires at 23:30 Dubai time when ≥1 hour is tracked."

Table with columns:
- Date (`dateLocal`)
- Machine / User (`machineId` → look up `machineName` + `userName` from machine summary)
- Tracked (`trackedSeconds` formatted as "2h 14m")
- Status: if `fired === true` → green "✓ Sent"; if `fired === false` → red "✗ Skipped"
- Reason (only shown when `fired === false`):
  - `under_1h` → "Under 1 hour tracked"
  - `email_not_set` → "No email configured"
  - `send_failed` → "Send failed"
  - `account_not_found` → "Account not found"
- Time sent (`sentAtUtc` as relative timestamp)

Show last 30 entries by default. This page is the first thing to check every
morning to confirm digests are firing.

---

### B8 — Visual design pass (do last)

Once all sections show real data:

1. Ensure card accent colors match the Ari brand palette:
   - Focus / success green: `#22c55e`
   - Distraction / warning amber: `#f59e0b`
   - Overload / error red: `#ef4444`
   - Away / inactive grey: `#9ca3af`
   - Primary purple (matches website + email): `#5252b0`

2. Each section page should have a consistent header structure:
   - Section label (small caps, `#5050a0`)
   - Section title (large, dark)
   - Last updated timestamp (small, grey) + a Refresh button

3. The machine state cards (B3) should have a left border color matching their
   state (green for focused, amber for context switch, red for distracted, grey
   for away).

4. Keep the existing layout structure. Do not redesign the nav or overall shell.

---

## Order of execution

1. A1 (digest log store + endpoint) — backend, porta-control-plane
2. A2 (uptime history store + endpoint) — backend, porta-control-plane
3. A3 (diagnose growth connection) — investigation, then frontend fix in console
4. Deploy porta-control-plane to Render after A1 + A2 are merged
5. B3 (User Activity rebuild) — frontend, highest impact, data already available
6. B4 (Trust Level rename) — frontend, quick
7. B5 (Actions timeline) — frontend, data already available
8. B1 (header tagline) — frontend, 5 minutes
9. B2 (uptime sparklines) — frontend, requires A2 to be deployed
10. B6 (Growth fix) — frontend, requires A3 investigation first
11. B7 (Digest section) — frontend, requires A1 to be deployed
12. B8 (visual design pass) — frontend, do last

---

## Important constraints

- Do NOT modify `MachineSummaryReport` in the api-contracts or the Windows agent.
  All data needed for the console is already in the existing contract fields.
- Do NOT change any existing fleet endpoints or their response shapes.
  Only add new endpoints (uptime, digest log).
- Do NOT change SQLite in the Windows agent. All new tables are in the control
  plane's Postgres fleet database only.
- The Windows agent telemetry write path must not be slowed down. The uptime
  sample write (A2) must be fire-and-forget — use `void appendUptimeSample(...)`
  not `await` inside the hot path if latency is a concern.
