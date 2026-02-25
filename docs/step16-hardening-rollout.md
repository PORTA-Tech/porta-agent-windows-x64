# Step 16: Hardening + Rollout (Agent)

## Security Hardening Applied
- TLS-only transport enforced in runtime (`https` scheme only).
- Optional certificate pinning in `PortaClient`:
  - env: `PORTA_TLS_CERT_SHA256_PIN`
  - format: SHA-256 hex digest of backend certificate.
- Local telemetry cache remains encrypted (`AES-256-GCM`) and signed (`HMAC-SHA256`).
- Consent gate remains fail-closed before capture/emission.

## Performance Smoke Harness
- Script: `scripts/step16-performance-smoke.ps1`
- Output: `docs/step16-agent-performance-smoke.csv`
- Captures process CPU time and working set each second.
- Usage:
  - `pwsh ./scripts/step16-performance-smoke.ps1 -DurationSeconds 120`

## Chaos Smoke Harness
- Script: `scripts/step16-chaos-smoke.ps1`
- Output: `docs/step16-agent-chaos-smoke.log`
- Simulates crash/restart cycles with forced process termination.
- Usage:
  - `pwsh ./scripts/step16-chaos-smoke.ps1 -Cycles 3`

## Canary Rollout Parameters
- Stage 1: 5% of fleet
- Stage 2: 25%
- Stage 3: 50%
- Stage 4: 100%

Gate criteria per stage:
- Agent CPU average `<5%`, peak `<10%`
- Memory `<150MB`
- capture->inference `<100ms`
- policy eval `<50ms`
- intervention dispatch `<200ms`
- tray update `<1s`

Rollback criteria:
- critical alert sustained > 5 minutes
- intervention failures > 3 in 5 minutes
- correlation trace incomplete ratio > 1%
