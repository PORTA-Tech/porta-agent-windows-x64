# Signing Requirements

## Signed objects
- Regulation policies
- Intervention commands

## Algorithm
- Ed25519 detached signatures (default)

## Signature coverage
Signature input must include canonicalized payload bytes covering:
- full payload
- `version` or `schema_version`
- timestamp (`created_at` or `issued_at`)

## Key management
- Key IDs are tracked externally by control-plane config.
- Rotation supports overlap (N and N+1 keys valid during transition).
- Revoked keys fail verification immediately.

## Verification contract
- Agent and control-plane must verify signature before execution.
- Invalid signature results in rejection and audit entry.