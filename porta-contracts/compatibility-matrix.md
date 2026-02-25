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

Compatibility is enforced in CI by schema compatibility checks against base branch.
