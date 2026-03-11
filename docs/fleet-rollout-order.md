# Fleet Rollout Order

This is the canonical rollout order for the fleet reporting + operations console path.

It exists to keep the implementation sequence stable across:

- `porta-api-contracts`
- `porta-control-plane`
- `porta-agent-windows-x64`
- `porta-operations-console`

The order below reflects the current live architecture and the work already completed.

## Stage 1: Contract Freeze

Define the canonical fleet reporting payloads first in `porta-api-contracts`.

Required payloads:

- `MachineSummaryReport`
- `MachineActionEvent`

Why first:

- every other repo depends on the same field names and enums
- control-plane storage and UI hooks should not guess response shapes

Status:

- done

## Stage 2: Control-Plane Persistence

Add persistent fleet stores in `porta-control-plane`.

Required stores:

- `machineSummaryStore`
- `machineActionStore`

Rules:

- production uses Postgres when `PORTA_CONTROL_PLANE_DATABASE_URL` or `DATABASE_URL` is configured
- in-memory fallback is allowed only for local/dev scenarios

Why second:

- the agent should not publish into a temporary endpoint contract with nowhere durable to land

Status:

- done

## Stage 3: Agent Summary/Action Publisher

Add the upstream reporter only in `Porta.Agent.Service`.

Rules:

- do not move fleet reporting into `Porta.UserAgent`
- use local truth already available from:
  - status
  - snapshot
  - recent events
  - runtime state
- publish machine summary every 5-10 seconds
- publish action events with a persisted last-sent cursor

Why third:

- the service is the authoritative machine-level reporter
- `UserAgent` remains capture/execution only

Status:

- done

## Stage 4: Control-Plane Ingestion and Read Endpoints

Add the ingestion API and the operator read API in `porta-control-plane`.

Required ingestion endpoints:

- `POST /api/agent/machine-summary`
- `POST /api/agent/action-events`

Required read endpoints:

- `GET /v1/machines/summary`
- `GET /v1/machines/:machineId`
- `GET /v1/machines/:machineId/actions`
- `GET /v1/actions/recent`

Compatibility:

- `/v1/nodes/snapshot` may remain as a compatibility alias, but it must be backed from fleet summary truth, not legacy presence-only state

Status:

- done

## Stage 5: Operations Console Hook Refactor

Make the active console pages consume the new fleet endpoints directly.

Required mapping:

- Layers 1-3 from `GET /v1/machines/summary`
- Layer 4 from `GET /v1/actions/recent`

Rules:

- do not use audit traces, websocket fragments, or inferred fake rows as the primary source of truth
- UI may simplify backend truth, but it must not invent it

Status:

- done

## Stage 6: Validation and Rollout

Run the end-to-end validation gates after all of the above exist.

Required checks:

1. machine appears in `machines/summary`
2. degraded capture is reflected when `Porta.UserAgent` stops
3. activity and last-activity fields move with real input
4. fleet summary matches local Control Center truth closely
5. action lifecycle appears centrally
6. persistence survives restart when control plane uses Postgres

Primary runner:

- `scripts/run-fleet-validation-gates.ps1`

Reference:

- `docs/fleet-validation-gates.md`

Status:

- done

## Commands

### Verify control-plane endpoints

```powershell
$h = @{ "x-operator-id" = "validation-runner"; "x-operator-role" = "viewer" }

Invoke-RestMethod -Uri "https://porta-control-plane.onrender.com/v1/machines/summary" -Headers $h
Invoke-RestMethod -Uri "https://porta-control-plane.onrender.com/v1/actions/recent" -Headers $h
```

### Run fleet validation gates

```powershell
Set-Location C:\Users\hp\porta-agent-windows-x64
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 -ControlPlaneBaseUrl "https://porta-control-plane.onrender.com"
```

### Optional process-control validation

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 -ControlPlaneBaseUrl "https://porta-control-plane.onrender.com" -AllowProcessControl
```

## Current Commit Landmarks

These are the main commits that established the fleet path:

- `porta-api-contracts`
  - `df2645e` `Add fleet reporting contracts`
- `porta-control-plane`
  - `7410e45` `Persist fleet state and expose machine endpoints`
  - `6ae63fa` `Track vendored fleet contract DTOs`
  - `d0b9d7f` `Add fleet validation health metadata`
- `porta-agent-windows-x64`
  - `73424d7` `Publish fleet reports from agent service`
  - `5a65803` `Add fleet validation gates runner`
  - `ae509b6` `Fix fleet validation wrapper param parsing`
- `porta-operations-console`
  - `648c907` `Read operations layers from machine summary and actions`
  - `7d187e8` `Align operations pages to four-layer UI mapping`
  - `c523d3f` `Unwrap machine and action collection responses`

## Non-Negotiable Rules

Do not:

- publish fleet truth from `Porta.UserAgent`
- let the console invent rows from traces
- rely on in-memory control-plane stores for production
- change the rollout order

Do:

- keep contracts frozen first
- keep storage ahead of publishing
- keep publishing ahead of UI refactors
- keep validation ahead of rollout claims
