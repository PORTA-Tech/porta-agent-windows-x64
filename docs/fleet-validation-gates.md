# Fleet Validation Gates

This validates the real fleet reporting path:

`Porta.Agent.Service` -> `porta-control-plane` -> `porta-operations-console`

## Preconditions

- The local Windows agent is running.
- `Porta.UserAgent` is running in the active user session.
- The control plane is reachable.
- The control plane accepts operator headers.
- A real account user can sign in to the control plane.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl http://localhost:8787 `
  -OperatorId validation-runner `
  -OperatorRole operator `
  -IdentityUserId admin `
  -IdentityPassword porta-admin `
  -AllowProcessControl
```

## What it validates

1. The machine appears in `GET /v1/machines/summary` within 10 seconds.
2. Identity rollout gates:
   - local `device_id` exists and is stable
   - sign-in links the current device to account/org
   - the service publishes `device_id` / `account_id` / `org_id`
   - control-plane account/device/telemetry views stay separate
   - joined machine reads return real display name / email / org
   - a revoked synthetic device shows revoked state centrally
   - the current device can be temporarily unlinked locally and still appears operationally without fake identity
   - the resulting report is published to `POST /v1/rollout/validation-gates`
   - `GET /v1/rollout/evaluate` shows `VALIDATION_GATES=PASS`
3. If `-AllowProcessControl` is used:
   - stopping `Porta.UserAgent` degrades capture truth
   - restarting `Porta.UserAgent` restores session truth
4. Manual typing/mouse activity updates:
   - `activity_detected`
   - `last_activity_utc`
   - keyboard/mouse flags
5. Control plane summary truth stays close to the local Control Center truth:
   - capture context
   - keyboard/mouse flags
   - calibration stage
   - trust tier
   - deep work / overload active
   - pressure (compat from overload likelihood)
   - focus episode duration (compat from task continuity seconds)
   - model confidence
6. Action lifecycle reaches the control plane:
   - long break suggestion (legacy transport action `break_suggestion`) -> `applied`
   - `deep_work_notifications_suppressed` -> `verified`

Migration note:

- `break_suggestion` remains a legacy transport action ID in current validation gates.
- Product-facing reporting should treat it as the compatibility path for the canonical break overlay flow, not as the long-term public action name.

## Identity safety

- If the current device is already locally linked to a different account/org, the runner stops unless `-AllowIdentityRelink` is provided.
- `-IdentityOrgId` can pin the workspace instead of using the first active org from `/v1/me`.
- `-SkipIdentityRolloutGates` leaves the legacy fleet gates available, but broad rollout should not proceed without a published validation report.

## Persistence gate

If the control plane reports `fleet_reporting.persistence_mode = postgres`, the validation runner writes a checkpoint file and prints a follow-up command.

After restarting the control plane, rerun:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl http://localhost:8787 `
  -OperatorId validation-runner `
  -OperatorRole operator `
  -IdentityUserId admin `
  -IdentityPassword porta-admin `
  -VerifyPersistenceCheckpoint "C:\path\to\checkpoint.json"
```

That verifies the machine summary and action rows still exist after the restart.
