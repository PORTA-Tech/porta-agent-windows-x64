# Ari — Windows Agent (porta-agent-windows-x64)

**Ari** is a Windows productivity-tracking agent. It runs quietly in the background, captures keyboard/mouse signals (no content, only metadata), classifies focus/pressure/overload states, and surfaces them through a floating desktop widget and a WPF Control Center app.

---

## What Ari Does

1. **Captures input signals** every second — keystrokes count, mouse movement, scroll velocity, app focus switches, idle time. Never raw text, screen content, clipboard, or file names.
2. **Analyzes in 5-second buckets** — normalizes signals against a personal baseline (Welford online stats), scores focus strength, overload likelihood, distraction pressure.
3. **Tracks work sessions** — detects focus episodes (≥15 s sustained focus), passive focus (reading/thinking without input), context switches, breaks. Resets only on genuine 3+ min idle or 5+ min app context shift.
4. **Shows real-time state** — floating widget shows Work today / Last focus session / Pressure. CC shows full history, pressure detail, volatile hours, daily timeline.
5. **Suggests breaks** after configurable thresholds (default: 30-min short break, 90-min long break) once the baseline has enough data (calibration phase = "Provisional"; ready = "Ready").

---

## 3 Processes

| Process | Binary | Runs as |
|---|---|---|
| **ari.exe** | `Porta.Agent.Service` | Windows Service (LocalSystem) |
| **Porta.UserAgent.exe** | `Porta.UserAgent` | User logon autostart |
| **Porta.ControlCenter.exe** | `Porta.ControlCenter` | Launched on demand by user |

### ari.exe (service)

- Hosts the local HTTP API on `127.0.0.1:17887–17950` (port chosen at install, stored in `C:\ProgramData\PortaAgent\local-api.endpoint.json`).
- Runs the full signal pipeline: input ingestion → bucket finalization → state inference → snapshot update.
- Persists everything to SQLite at `C:\ProgramData\PortaAgent\core-state.db` (WAL mode, FULL synchronous).
- Auth token stored (DPAPI, LocalMachine scope) at `C:\ProgramData\PortaAgent\security\api_token.bin`. Runtime settings at `C:\ProgramData\PortaAgent\security\local-api.runtime.json`.

### Porta.UserAgent.exe

- Captures keyboard/mouse hooks in the user desktop session (can't run as service).
- Posts input signals to `ari.exe` via the local API every 5 seconds.
- Drives the floating desktop widget — polls the local API every 4 seconds.
- Shows break toasts and overload notifications.

### Porta.ControlCenter.exe

- WPF dashboard, reads all data from the local API.
- Shows today's focus timeline, pressure history, diagnostics, settings.
- Single-instance; double-click tray icon to open.

---

## Solution Layout

```
src/
  Porta.Agent.Service/     # Service host + local API + pipeline orchestration
  Porta.Agent.Core/        # State machine, SQLite store, DB migrations
  Porta.Agent.LocalApi/    # HTTP endpoint mapping, response contracts, client
  Porta.UserAgent/         # Widget window, input capture, session host
  Porta.ControlCenter/     # WPF dashboard
  Porta.Agent.Common/      # Shared models/helpers
  Porta.Agent.Runtime/     # Input capture runtime (keyboard/mouse hooks)
installer/                 # WiX MSI project
scripts/
  build-msi.ps1            # ALWAYS use this to build a release MSI
```

---

## Building a Release MSI

**Always use `scripts/build-msi.ps1`** — never `dotnet publish` directly, because the MSI reads from `installer/payload/` which only this script populates.

```powershell
# Bump Version in all 3 .csproj files first, then:
cd C:\Users\hp\porta-agent-windows-x64
powershell -ExecutionPolicy Bypass -File scripts/build-msi.ps1
# Output: installer/bin/x64/Release/AriAgentInstaller.msi
# Copy to: artifacts/publish/AriAgentInstaller-<version>.msi
```

Version must be bumped in:
- `src/Porta.Agent.Service/Porta.Agent.Service.csproj`
- `src/Porta.UserAgent/Porta.UserAgent.csproj`
- `src/Porta.ControlCenter/Porta.ControlCenter.csproj`

---

## DB Schema

SQLite at `C:\ProgramData\PortaAgent\core-state.db`. Migration version tracked in `diagnostics` table (`schema_version` key). Current version: **19**.

Key tables:
- `state_buckets` — one row per 5-second bucket, contains `state_flags_json` with focus/pressure/continuity scores
- `input_buckets` — raw signal aggregates per bucket
- `primitive_buckets` — normalized, z-scored primitives per bucket
- `events` — break suggestions, interventions, pauses, resumes
- `consent` — consent grant history
- `runtime_state` — live service diagnostics
- `brain_snapshots` — **legacy, emptied by V19 migration, no longer written**

**WAL file** (`core-state.db-wal`) can grow large. The service uses `wal_autocheckpoint=1000` (SQLite default) for passive checkpointing. **Never run `VACUUM` or `wal_checkpoint(TRUNCATE)` from a background thread** — both require exclusive DB access and exhaust the .NET thread pool on large databases, hanging the HTTP server.

---

## Widget Behavior (what to expect)

| State | When | How long |
|---|---|---|
| Work: `—` | No timeline data yet | < 60s after first bucket |
| Focus: `—` | No focus episode yet | Until ≥15s sustained input |
| Pressure: `—` | No snapshot yet | < 60s after first bucket |
| "Starting Ari locally." | API unreachable | Until service is up |
| "Tracking limited" banner | `freshness_state != fresh` | Clears once state refreshes |
| "Learning your pattern: Provisional" | Baseline < threshold | First ~30-60 min |
| "Learning your pattern: Ready" | Baseline established | Permanent once reached |

**The widget is not a real-time display.** Work today updates on bucket cadence (~60s). Focus and Pressure update on state refresh cycle (~5-10s when healthy). If the API is hung (e.g. VACUUM blocking thread pool), the widget shows last-cached timeline for Work but `—` for Focus/Pressure.

---

## Common Issues

### Widget shows "Starting Ari locally." for a long time
**Cause:** The local API is not responding (timeout > 3s). The widget HTTP client has a 3-second timeout.

**Diagnose:**
```powershell
$tc = [System.Net.Sockets.TcpClient]::new()
$tc.Connect("127.0.0.1", (Get-Content C:\ProgramData\PortaAgent\local-api.endpoint.json | ConvertFrom-Json).Port)
# If this hangs: service is not listening
# If connected but no HTTP response: thread pool is exhausted (VACUUM / blocking DB op)
```

**Fix:** Install the latest MSI from `artifacts/publish/`.

### Computer is slow after install
**Cause:** A background VACUUM or `wal_checkpoint(TRUNCATE)` is running. These operations lock the entire SQLite DB exclusively and hammer disk I/O on multi-GB databases.

**Fix:** Never run VACUUM or TRUNCATE checkpoint from background tasks. Use only `PRAGMA wal_checkpoint(PASSIVE)` if needed.

### "Tracking limited" banner always showing
**Cause:** `freshness_state != "fresh"` — the service hasn't completed a full state refresh cycle recently. Clears automatically after a clean service cycle (~10-30s).

### Service crashes on startup (loop)
**Cause:** Usually a migration failure. Check if VACUUM was added to a migration step — SQLite forbids VACUUM inside a `BEGIN IMMEDIATE` transaction.

### History page empty in CC
**Cause:** `_retention` was null when timeline was fetched; 90-day fallback wasn't applied. Fixed in current version.

---

## Version History (recent)

| Version | Key change |
|---|---|
| 1.2.4 | Replace blocking VACUUM/TRUNCATE with safe PASSIVE checkpoint |
| 1.2.3 | Remove VACUUM from V19 migration (was crashing service in loop) |
| 1.2.2 | V19 migration: stop writing brain_snapshots, schedule cleanup |
| 1.2.1 | PublishSingleFile=false (was causing Defender scan hang on first run) |
| 1.2.0 | Major version bump (1.1.x MSIs existed, causing WiX downgrade rejection) |

---

## Connected Repos

| Repo | Role |
|---|---|
| `porta-control-plane` | Backend: policy governance, fleet management, signed intervention commands |
| `porta-api-contracts` | Shared DTO/OpenAPI contracts between service and control plane |
| `porta-operations-console` | Operator web dashboard for fleet oversight |

The Windows agent connects to the control plane via `PORTA_BACKEND_URL` for policy sync and fleet telemetry. All local analysis runs offline-first — the agent is fully functional without a backend connection.

---

## Security & Privacy

- No raw text, screen pixels, clipboard, file contents, or URLs ever captured or stored
- No signals emitted before consent is active (`consent` table)
- Auth token encrypted with DPAPI (LocalMachine scope) — readable by any local process, not remotely
- Local API bound to `127.0.0.1` loopback only
- `ProtectionScope.LocalMachine` means the token is accessible to both the service (SYSTEM) and user processes (Porta.UserAgent, Porta.ControlCenter)
