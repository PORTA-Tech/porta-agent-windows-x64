# PORTA Agent (Windows x64)

Windows runtime for PORTA's closed loop on a single machine.

The repo is in the middle of a boundary split:

- `Porta.Agent.Service` is the machine service host and local deterministic engine host.
- `Porta.UserAgent` is the user-session process boundary for tray UX and session-bound capture.
- `Porta.ControlCenter` is the API-driven WPF control surface.

Step `0A` defines that project ownership. Later steps move the actual capture path and ingest flow.

## What This Repository Owns

- Windows signal capture (strict whitelist only)
- Consent gating and privacy-safe local aggregation
- Layer 2 normalization (rolling mean/std, z-score, EWMA)
- Layer 3 deterministic inference (`agent.primitive -> agent.state`)
- Layer 4 stabilization (hysteresis, dwell, cooldown, flap suppression)
- Tray UX (status, interventions, pause/resume, explainability)
- Local telemetry, correlation IDs, observability/alerts
- Local hardening and rollout smoke harnesses

## Implemented Scope (Steps)

- Step 3 integration: contracts/build/types/tests compatibility with CI naming alignment
- Step 4: Layer 1 signal acquisition + consent + tray visibility baseline
- Step 5: Layer 2 deterministic normalization
- Step 6: Layer 3 deterministic inference and explainability fields
- Step 7: Layer 4 state stabilization logic
- Step 14: Tray full UX baseline
- Step 15: Observability + alert wiring (correlation and end-to-end tracing)
- Step 16: hardening/performance/chaos smoke harnesses

## Solution Layout

- `src/Porta.Agent.Service`: Windows machine service host, local API host, deterministic engine composition
- `src/Porta.UserAgent`: user-session process boundary for tray UX and session-bound capture
- `src/Porta.ControlCenter`: WPF renderer of local API truth
- `src/Porta.Agent.Core`: deterministic kernel implementation and persistence coordination
- `src/Porta.Agent.LocalApi`: localhost contracts, client, endpoint mapping, runtime settings
- `src/Porta.Agent.Common`: shared models/helpers
- `src/Porta.Agent.Runtime`: shared runtime helpers and legacy runtime components under reduction
- `scripts/`: install/build/smoke scripts
- `docs/`: architecture and boundary specifications

Current dependency direction is centered around the service host and shared libraries, with `ControlCenter` consuming only `Porta.Agent.LocalApi`.

## Quick Start

### Prerequisites

- Windows x64
- .NET SDK 8.0+

### Build

```powershell
dotnet build PORTA.Windows.sln
```

### Run user-session agent

```powershell
dotnet run --project src/Porta.UserAgent/Porta.UserAgent.csproj
```

### Run service host

```powershell
dotnet run --project src/Porta.Agent.Service/Porta.Agent.Service.csproj
```

### Run control center

```powershell
dotnet run --project src/Porta.ControlCenter/Porta.ControlCenter.csproj
```

### Install service (admin)

```powershell
.\scripts\install-service.ps1 -StartAfterInstall
```

The MSI now packages `Porta.UserAgent.exe` separately from the machine service and installs a user-logon startup entry for it, so session-bound capture starts in the desktop session instead of inside the Windows service.

The runtime contract for background behavior, capture health, user notifications, and Windows update handling is locked in `docs/runtime-contract.md`.

### Uninstall service

```powershell
.\scripts\uninstall-service.ps1
```

## Key Environment Variables

- `PORTA_BACKEND_URL`: control-plane base URL (`https` only)
- `PORTA_CONTROL_PLANE_WS_URL`: realtime stream endpoint
- `PORTA_TLS_CERT_SHA256_PIN`: optional certificate pin
- `PORTA_CONSOLE_URL`: dashboard URL opened from tray
- `PORTA_CONSENT_STATE_PATH`: consent storage path override
- `PORTA_EXPECTED_POLICY_VERSION`: incoming policy version guard
- `PORTA_LAYER1_SIGNAL_POLICY`: expected whitelist policy (`whitelist_v1`)
- `PORTA_SIGNAL_SCHEMA_VERSION`: expected signal schema (`1.0.0`)

## Security And Privacy Guarantees

- Never captures raw text, clipboard content, screen pixels, file contents, or packet payloads
- No signal emission before consent is active
- Fail-closed behavior on consent/config/state errors
- TLS transport with optional cert pinning
- Encrypted local logs (`AES-256-GCM`) with integrity (`HMAC-SHA256`)
- Intervention version/signature checks before apply

## Validation Scripts

- `scripts/privacy-guard.ps1`
- `scripts/phase6-soak.ps1`
- `scripts/phase72-combined-soak.ps1`
- `scripts/step16-performance-smoke.ps1`
- `scripts/step16-chaos-smoke.ps1`

## Docs

- `docs/architecture.md`
- `docs/runtime-contract.md`
- `docs/project-boundaries.md`
- `docs/step14-tray-app-full-ux.md`
- `docs/step15-observability-alerts.md`
- `docs/step16-hardening-rollout.md`

## Boundaries

This repo does not own:

- Central policy governance, fleet decisioning, signed command issuance: `porta-control-plane`
- Operator web UX and fleet dashboards: `porta-operations-console`
- Shared DTO/OpenAPI contracts: `porta-api-contracts`
