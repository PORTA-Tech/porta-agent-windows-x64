# Fleet Rollout Order

This is the canonical rollout order for the identity-aware fleet path.

It exists to keep the implementation sequence stable across:

- `porta-api-contracts`
- `porta-control-plane`
- `porta-agent-windows-x64`
- `porta-operations-console`

The order below is the exact release order. Identity is additive and rollout claims are only valid after the final stage.

## Stage 1: Contract Freeze

Repo:

- `porta-api-contracts`

Scope:

- freeze the identity-aware fleet contracts before downstream repos change

Status:

- done

## Stage 2: Control-Plane Identity Tables

Repo:

- `porta-control-plane`

Scope:

- add persistent identity/profile tables and keep them separate from telemetry truth
- accounts
- organizations
- memberships
- devices
- consent records

Status:

- done

## Stage 3: Control-Plane Identity/Profile Endpoints

Repo:

- `porta-control-plane`

Scope:

- add account/profile/device/org read and management endpoints
- keep profile/device truth out of telemetry tables

Status:

- done

## Stage 4: Device Register/Link Flow

Repos:

- `porta-control-plane`
- `porta-agent-windows-x64`

Scope:

- explicit device register, link, unlink, and revoke flow
- no implicit relink during inventory registration

Status:

- done

## Stage 5: Local Identity Link In Windows App

Repo:

- `porta-agent-windows-x64`

Scope:

- stable local `device_id`
- local link store for `device_id/account_id/org_id/link_status`
- Control Center seam over the local identity link

Status:

- done

## Stage 6: Service Reporter Update

Repo:

- `porta-agent-windows-x64`

Scope:

- service reporter always publishes `device_id`
- publish `account_id` and `org_id` only when linked
- keep name/email out of telemetry reports

Status:

- done

## Stage 7: Joined Admin Endpoints

Repo:

- `porta-control-plane`

Scope:

- joined admin/ops reads over telemetry truth plus account/org display fields
- unlinked devices still return operational rows with null identity display fields

Status:

- done

## Stage 8: Operations Console Identity Display

Repo:

- `porta-operations-console`

Scope:

- augment the existing 4-layer model with joined identity fields
- no new top-level profile layer

Status:

- done

## Stage 9: Control Center Account UI

Repo:

- `porta-agent-windows-x64`

Scope:

- signed-in account surface
- workspace/subscription/device/privacy views
- real account/device relationship display

Status:

- done

## Stage 10: Consent Split

Repos:

- `porta-control-plane`
- `porta-agent-windows-x64`

Scope:

- separate `account/legal` consent from `device/local capture` consent
- signed in does not automatically mean capture-authorized on this machine

Status:

- done

## Stage 11: Migration/Backfill

Repo:

- `porta-control-plane`

Scope:

- existing unlinked machines continue to report
- first new report backfills missing device rows when needed
- legacy fleet pages remain honest for unlinked devices

Status:

- done

## Stage 12: Validation And Rollout

Repos:

- `porta-agent-windows-x64`
- `porta-control-plane`

Scope:

- run rollout validation gates only after Stages 1-11 are complete
- publish validation results to the control plane
- block broad rollout claims unless validation is green

Required checks:

1. new install creates stable `device_id`
2. sign-in links device to account/org
3. service publishes `device_id/account_id/org_id`
4. control plane stores profile + device + telemetry separately
5. joined endpoints show real `name/email/org`
6. revoked device disappears or shows revoked status correctly
7. unlinked device still appears operationally without fake identity

Primary runner:

- `scripts/run-fleet-validation-gates.ps1`

Reference:

- `docs/fleet-validation-gates.md`

Status:

- done in code and automated checks
- live broad-rollout signoff still requires running the real gate flow against a live control plane and real credentials

## Commands

### Verify control-plane endpoints

```powershell
$h = @{ "x-operator-id" = "validation-runner"; "x-operator-role" = "viewer" }

Invoke-RestMethod -Uri "https://porta-control-plane.onrender.com/v1/machines/summary" -Headers $h
Invoke-RestMethod -Uri "https://porta-control-plane.onrender.com/v1/actions/recent" -Headers $h
Invoke-RestMethod -Uri "https://porta-control-plane.onrender.com/v1/rollout/evaluate" -Headers $h
```

### Run fleet validation gates

```powershell
Set-Location C:\Users\hp\porta-agent-windows-x64
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl "https://porta-control-plane.onrender.com" `
  -IdentityUserId "admin" `
  -IdentityPassword "porta-admin"
```

### Optional process-control validation

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-fleet-validation-gates.ps1 `
  -ControlPlaneBaseUrl "https://porta-control-plane.onrender.com" `
  -IdentityUserId "admin" `
  -IdentityPassword "porta-admin" `
  -AllowProcessControl
```

## Non-Negotiable Rules

Do not:

- skip ahead in the order above
- publish fleet truth from `Porta.UserAgent`
- duplicate PII into telemetry persistence
- claim rollout readiness before validation gates are green

Do:

- freeze contracts first
- keep identity/profile/device truth separate from telemetry truth
- keep migration/backfill ahead of rollout claims
- keep validation as the final gate before broad rollout
