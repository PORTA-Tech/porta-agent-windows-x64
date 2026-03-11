# Fleet Validation Gates

This validates the real fleet reporting path:

`Porta.Agent.Service` -> `porta-control-plane` -> `porta-operations-console`

## Preconditions

- The local Windows agent is running.
- `Porta.UserAgent` is running in the active user session.
- The control plane is reachable.
- The control plane accepts operator headers.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl http://localhost:8787 `
  -OperatorId validation-runner `
  -OperatorRole viewer `
  -AllowProcessControl
```

## What it validates

1. The machine appears in `GET /v1/machines/summary` within 10 seconds.
2. If `-AllowProcessControl` is used:
   - stopping `Porta.UserAgent` degrades capture truth
   - restarting `Porta.UserAgent` restores session truth
3. Manual typing/mouse activity updates:
   - `activity_detected`
   - `last_activity_utc`
   - keyboard/mouse flags
4. Control plane summary truth stays close to the local Control Center truth:
   - capture context
   - keyboard/mouse flags
   - calibration stage
   - trust tier
   - deep work / overload active
   - overload likelihood
   - task continuity seconds
   - model confidence
5. Action lifecycle reaches the control plane:
   - `break_suggestion` -> `applied`
   - `deep_work_notifications_suppressed` -> `verified`

## Persistence gate

If the control plane reports `fleet_reporting.persistence_mode = postgres`, the validation runner writes a checkpoint file and prints a follow-up command.

After restarting the control plane, rerun:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl http://localhost:8787 `
  -OperatorId validation-runner `
  -OperatorRole viewer `
  -VerifyPersistenceCheckpoint "C:\path\to\checkpoint.json"
```

That verifies the machine summary and action rows still exist after the restart.
