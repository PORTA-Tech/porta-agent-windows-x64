# Compatibility Matrix

| Service | Supported contract version |
|---|---|
| Agent v1 | ^1.0.0 |
| Control-plane v1 | ^1.0.0 |
| Console v1 | ^1.0.0 |
| Tray v1 | ^1.0.0 |

## Step-8 policy/signing update
- Policy contract schema: `RegulationPolicy` moved to `schema_version=2.0.0`.
- Signature verification vectors are enforced in CI via `contracts:policy-signature-test`.

## Step-9 control-plane ingestion update
- New control-plane contracts added at `schema_version=1.0.0`:
  - `ControlPlaneTypedIngestionEnvelope`
  - `ControlPlaneSchemaRegistryEntry`
  - `ControlPlaneIngestionValidationResult`
  - `ControlPlaneTimeSeriesRecord`
  - `ControlPlaneIngestionHealthMetric`
- CI now enforces ingestion compatibility vectors via `contracts:ingestion-validation-test`.
- Compatibility policy defaults:
  - `compatibility_mode=exact_major_backward_minor_patch`
  - major mismatch rejected with `SCHEMA_VERSION_INCOMPATIBLE`
  - producer allow-list, payload-size, and clock-skew limits are hard-fail checks

Compatibility is enforced in CI by schema compatibility checks against base branch.
