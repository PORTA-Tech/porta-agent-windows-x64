# PORTA Agent (Windows x64)

Windows runtime for PORTA's closed loop on a single machine.

It captures privacy-safe signals, transforms them into normalized primitives, computes local probabilistic state, stabilizes transitions, renders tray UX, and exchanges signed/typed events with the control-plane.

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

- `src/Porta.Agent.Common`: shared models/helpers
- `src/Porta.Agent.Runtime`: interactive runtime + tray host
- `src/Porta.Agent.Service`: Windows service host
- `scripts/`: install/build/smoke scripts
- `docs/`: architecture and step specifications

Dependency direction: `Service -> Runtime -> Common`.

## Quick Start

### Prerequisites

- Windows x64
- .NET SDK 8.0+

### Build

```powershell
dotnet build PORTA.Windows.sln
```

### Run interactive runtime (tray)

```powershell
dotnet run --project src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj
```

### Run service host

```powershell
dotnet run --project src/Porta.Agent.Service/Porta.Agent.Service.csproj
```

### Install service (admin)

```powershell
.\scripts\install-service.ps1 -StartAfterInstall
```

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
- `docs/step14-tray-app-full-ux.md`
- `docs/step15-observability-alerts.md`
- `docs/step16-hardening-rollout.md`

## Boundaries

This repo does not own:

- Central policy governance, fleet decisioning, signed command issuance: `porta-control-plane`
- Operator web UX and fleet dashboards: `porta-operations-console`
- Shared DTO/OpenAPI contracts: `porta-api-contracts`
