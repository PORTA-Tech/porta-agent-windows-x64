# Local Database Backup — Plan

**Goal:** `core-state.db` currently has no backup and no restore path. One corrupted file, one bad
migration, or one accidental delete loses a user's entire history — and with it their Welford
baseline, which drops them back to "Provisional" until it recalibrates. This adds a nightly local
copy, automatic retention, and a documented way to put a copy back.

**Scope:** the agent repo only. Nothing leaves the user's machine.

---

## Decisions already made (do not re-litigate)

- **Local only.** The backup file stays on the user's PC. No upload, no control plane, no network.
- **`VACUUM INTO`, not `File.Copy`.** The database runs in WAL mode and is written continuously.
  A plain file copy can capture a torn mid-write state. `VACUUM INTO` produces a consistent,
  compacted copy of a live database without pausing the agent.
- **14 days of copies**, then automatic deletion. Data not held cannot leak and cannot be demanded.
- **A size ceiling as well as an age limit.** Age alone does not bound disk use for a heavy user.
- **Server-side backup is NOT part of this phase.** It requires a consent-screen change and a
  Settings switch first — see "Explicitly out of scope" below.

---

## Current state (verified in code, 2026-07-27)

| Piece | Where | What it does today |
|---|---|---|
| Database path | `src/Porta.Agent.Core/Db/DbPaths.cs` | `%ProgramData%\PortaAgent\core-state.db` |
| Store | `src/Porta.Agent.Core/SqliteBrainStateStore.cs` | SQLite, WAL, `synchronous=NORMAL`, autocheckpoint=100 |
| Existing "export" | `SqliteBrainStateStore.ExportBundle` (~line 3353) | Manual support bundle. Uses `File.Copy` on the live DB — **not** a safe backup, and has no schedule or retention |
| Worker registration | `src/Porta.Agent.Service/AgentService.cs` lines 82–105 | `services.AddHostedService<...>()` — 10 workers today |
| Closest pattern to copy | `src/Porta.Agent.Service/AgentUpdateWorker.cs` | `BackgroundService`, tick loop, whole tick wrapped in try/catch so a worker bug can never take down capture |
| Folder permissions | `src/Porta.Agent.Service/SharedDataAclHealer.cs` | Grants `BUILTIN\Users` **Modify** on `%ProgramData%\PortaAgent\`, then re-locks the `security\` subfolder to read-only |

**Consequence of that last row:** anything written directly under `PortaAgent\` is deletable by any
standard user account on the machine. Backups must not inherit that.

---

## What gets built

### 1. `src/Porta.Agent.Service/DatabaseBackupWorker.cs` (new)

A `BackgroundService`, registered alongside the existing ten.

- **Schedule:** once per day at **03:00 local**. Deliberately not 23:30 — that is `DailyDigestReporter`.
  Ticks every 15 minutes and fires when the local day has rolled past 03:00 and today's backup has
  not been taken. A machine that is asleep at 03:00 takes its backup at the next tick after waking,
  so a laptop that is never on at 3am still gets backed up.
- **Output:** `%ProgramData%\PortaAgent\backups\core-state-{yyyyMMdd-HHmmss}.db`
- **Method:** open a read connection to the live DB and execute `VACUUM INTO '<target>'`.
  Write to a `.tmp` name first, then rename into place, so an interrupted run never leaves a
  half-written file that looks like a valid backup.
- **Failure policy:** the entire tick is wrapped in try/catch and logs one line. A backup failure
  must never affect capture, the pipeline, or service start. Same rule as `AgentUpdateWorker`.
- **Skip conditions:** no database file yet; a backup already taken today; less than a
  configurable free-disk margin remaining.

### 2. Retention — inside the same worker

Runs immediately after a successful backup:

- Delete backups older than **14 days**.
- Then, if the folder still exceeds **500 MB**, delete oldest-first until it fits.
- **Never delete the most recent backup**, whatever the limits say. A retention rule that can
  empty the folder is worse than no retention.

### 3. Folder permissions

`backups\` is created with the same treatment `SharedDataAclHealer` gives `security\`: inheritance
broken, `BUILTIN\Users` pinned to read-only, SYSTEM/Administrators keep full control. The service
runs as LocalSystem and is the only writer.

This means a normal user cannot silently delete their own backups, and neither can anything running
as that user.

### 4. `scripts/restore-backup.ps1` (new)

Run as Administrator. Takes a backup filename, or defaults to the newest.

1. Stop the `AriAgent` service.
2. Move the current `core-state.db` aside as `core-state.db.replaced-{timestamp}` — never delete it.
   If the restore turns out to be the wrong choice, the original is still there.
3. Copy the chosen backup into place.
4. Delete the stale `-wal` and `-shm` sidecar files, which belong to the replaced database.
5. Start the service.
6. Print which file was restored and where the replaced one went.

### 5. Tests — `src/Porta.Agent.Service.Tests/DatabaseBackupWorkerTests.cs` (new)

Retention is the part with real logic and real risk, so it is tested against a temp folder:

- Files older than 14 days are deleted; newer ones survive.
- The size ceiling deletes oldest-first until under the limit.
- The newest backup survives both rules even when it alone exceeds the ceiling.
- An empty or missing folder is a no-op, not a crash.
- A `.tmp` file left by an interrupted run is ignored by retention and cleaned up on the next run.

Plus: `AgentServiceCompositionTests` already asserts the worker set — it gets the new worker added.

---

## Acceptance

- Service starts; `backups\` is created with Users pinned to read-only.
- Force a run (temporarily shorten the schedule) → a `.db` file appears and opens cleanly in any
  SQLite viewer.
- `sqlite3 <backup> "PRAGMA integrity_check;"` returns `ok`.
- Restore script: stop, restore, start, and the Control Center shows the restored history.
- `dotnet test` green.
- **Live verification on Yadin's machine before the commit** — WPF/service code cannot be built or
  run in the sandbox.

---

## Explicitly out of scope for this phase

- **Uploading backups to the control plane.** Would need, in this order: a line in the consent
  screen describing what is uploaded, a switch in Settings defaulting to off, and the upload path
  itself. Centralising every user's history also raises the value of a control-plane breach, so it
  should land after per-device auth (Supabase plan, Stage 3), not before.
- **Encrypting `core-state.db` at rest (SQLCipher).** Separate job. Note the interaction: once the
  database is encrypted, the backups are encrypted too, and a DPAPI-machine-bound key means a dead
  machine makes its own backups unreadable. The key-recovery story has to be settled before
  encryption ships, not after.
- **Anything in the control plane repo.**

---

## Standing rules

- One commit: `backup: nightly local VACUUM INTO snapshots with 14-day retention`.
- `git add` new files explicitly before committing.
- Nothing leaves the machine in this phase. No new network calls, no new endpoints.
- A backup failure never blocks or degrades capture.
- Yadin builds, installs, and live-verifies before the commit lands.
