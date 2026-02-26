# PORTA Local API Security v1

This file records the mandatory IPC security controls for the localhost API.

## Enforced controls

1. Bind only to loopback:
- Service binds with `UseUrls("http://127.0.0.1:<port>")`.

2. Random per-install token:
- Generated via `RandomNumberGenerator.GetBytes(32)`.
- Stored encrypted with DPAPI (`LocalMachine`) in:
  - `%ProgramData%\PortaAgent\security\local-api.secure.json`

3. Required auth header:
- Header name: `X-PORTA-TOKEN`
- Missing/invalid token returns `401` with `missing_or_invalid_token`.

4. Loopback-only request policy:
- Non-loopback source addresses are rejected with `403` and `loopback_only`.

5. No CORS surface:
- No CORS middleware is registered.

6. Basic anti-spam rate limiting:
- Fixed-window limit `60 req/s` for `/v1/*`.
- Excess requests return `429` with `rate_limited`.

7. Shared local endpoint config (non-secret):
- Service writes active loopback endpoint to:
  - `%ProgramData%\PortaAgent\local-api.endpoint.json`
- This file contains loopback host + negotiated port only (no token).
- Tray and Control Center read port from this shared file instead of hardcoded values.

## Implementation pointers

- `src/Porta.Agent.Service/Program.cs`
- `src/Porta.Agent.LocalApi/LocalApiRuntimeSettingsStore.cs`
- `src/Porta.Agent.LocalApi/LocalApiEndpointMapping.cs`
