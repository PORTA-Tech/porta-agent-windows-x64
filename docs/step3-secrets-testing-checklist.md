# Step 3 Secrets Testing Checklist

## Scope
- `install_secret.bin` and `api_token.bin` under `%ProgramData%\PORTA\secrets\`
- DPAPI scope: `LocalMachine`
- Status contract: `localApiAuthReady` and `localApiAuthError` on `/v1/status`

## 1. Fresh Install
- Stop service/app.
- Delete `%ProgramData%\PORTA\secrets\install_secret.bin` if it exists.
- Delete `%ProgramData%\PORTA\secrets\api_token.bin` if it exists.
- Start service/app.
- Verify both files are created.
- Verify both files decrypt to exactly 32 bytes through `Secrets.GetInstallSecret()` and `Secrets.GetApiToken()`.
- Verify `/v1/status` returns `localApiAuthReady=true` and `localApiAuthError=null`.

## 2. Restart Persistence
- Record current values:
  - `install_secret` bytes from `Secrets.GetInstallSecret()`
  - `api_token` bytes from `Secrets.GetApiToken()`
- Restart service/app.
- Read both values again.
- Verify values are identical (no regeneration/rotation).

## 3. Corrupt install_secret.bin (fail closed)
- Stop service/app.
- Corrupt `%ProgramData%\PORTA\secrets\install_secret.bin` (overwrite with random non-DPAPI bytes).
- Start service/app.
- Verify startup fails closed.
- Verify logs include install-secret corruption/read failure code (`INSTALL_SECRET_READ_FAILED` or `INSTALL_SECRET_INVALID_LENGTH`).

## 4. Corrupt api_token.bin (degraded mode)
- Restore valid `install_secret.bin` first.
- Stop service/app.
- Corrupt `%ProgramData%\PORTA\secrets\api_token.bin`.
- Start service/app.
- Verify service still starts.
- Verify `/v1/status` returns:
  - `localApiAuthReady=false`
  - `localApiAuthError="TOKEN_READ_FAILED"`

## 5. Recovery from token corruption
- Stop service/app.
- Replace `%ProgramData%\PORTA\secrets\api_token.bin` with a valid DPAPI blob (or delete to allow regeneration).
- Start service/app.
- Verify `/v1/status` returns:
  - `localApiAuthReady=true`
  - `localApiAuthError=null`
