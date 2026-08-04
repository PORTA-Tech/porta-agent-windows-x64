# PORTA Security Work — Handoff

**Written:** 2026-07-28, end of session
**For:** the next session (Claude Code, fresh chat)
**Repos:** `porta-agent-windows-x64`, `porta-control-plane`, `porta-operations-console`

---

## How to use this document

Read the whole thing before touching code. Section 5 is the working rules — they come from
mistakes actually made, and repeating them wastes the user's time. Section 4 is the work queue.

Anything below stated as fact was measured or read in source. Where something is inferred rather
than verified, it says so. Keep that convention when you update this document.

---

## 0. URGENT — nothing outstanding

The default-credential hole is **closed, deployed, and verified live on 2026-07-28.** Details in
section 2. There is no open emergency.

**Agent 1.2.126 was RELEASED to the fleet on 2026-08-04.** It carries the 4.6a refresh-token work,
the `GetMeAsync` fix and the SQLite CVE upgrade, and it is the build with the shared payload
removed. The matching control-plane change was deployed and verified live *before* the agent
shipped — that order was deliberate and matters (§4.6a).

| | |
|---|---|
| version | **1.2.126** |
| SHA-256 | `71F0649EFDFEBFB772D1D06BB696D6B984C4D2BA37BCB87F1733BFB574007D75` |
| MSI | 3 files, 178,003,968 bytes |
| agent commit | `c274516` |
| control plane | `f84f339`, deployed |

**The one thing NOT yet exercised end to end — do this before trusting the refresh path:**

Sign out of the Control Center and sign back in with "Stay signed in on this PC" ticked, then
confirm `%LOCALAPPDATA%\PortaAgent\control-center.remembered-signin.v2.json` exists and the `v1`
file is gone. The install was verified on a session already valid to 3 Sept, so the *stored*
session kept working — but that does not prove a refresh token was ever issued, stored or
redeemed. Rotation, reuse-detection and the v1 password purge are covered by unit tests and by
live probes against production, **not** by a real client completing the round trip. Until someone
does that, treat "the agent stores a refresh token instead of a password" as tested-in-parts.

Everything else from the 2026-08-04 stretch is done: 4.1, 4.2, 4.6a, 4.7, 4.9. Open work starts
at 4.4.

---

## 1. Verified state

Verified by reading git on 2026-07-28 (second session), not copied forward from the previous
version of this table. An earlier version of this section listed only the two pushed agent commits
and did not record that three further commits existed on an unmerged, unpushed branch. Check
`git status -sb` and `git branch -r --contains HEAD` before writing this table again.

| Repo | Commit | What | State |
|---|---|---|---|
| porta-control-plane | `5294219` | test: make facade test independent of base-user state | on `main`, pushed |
| porta-control-plane | `4449fa4` | auth: default logins fail closed in production | on `main`, pushed |
| porta-control-plane | `ed111d4` | narrative: reject unknown accounts before spending a model call | on `main`, pushed |
| porta-agent-windows-x64 | `a727609` | restore-backup: survive missing processes, always restart service | on `main`, pushed |
| porta-agent-windows-x64 | `07107b8` | backup: nightly local VACUUM INTO snapshots, 7-day retention | on `main`, pushed |
| porta-agent-windows-x64 | `2b90cfb` | restore-backup: keep the WAL, verify the database, roll back on failure | merged to `main` and pushed 2026-07-28 |
| porta-agent-windows-x64 | `b11f345` | backup: prune on every tick, size the free-space rule from the database | merged to `main` and pushed 2026-07-28 |
| porta-agent-windows-x64 | `fb45ae8` | docs: correct the handoff — dotnet is available, elevation is not | merged to `main` and pushed 2026-07-28 |

The last three sat on branch `backup-restore-hardening`, contained in no remote branch, until they
were merged and pushed on 2026-07-28. `porta-control-plane` local `main` and `origin/main` were
identical at `5294219` throughout.

**Test status, measured:** control plane `npm run test` → 197 passing, CI run 30361986697 green on
`5294219`. Agent `dotnet test src\Porta.Agent.Service.Tests` → **92 passing, 0 failed** (measured
2026-07-28 on `backup-restore-hardening` immediately before the merge). The previous figure of 69
was measured 2026-07-27, before the branch added tests — the count rising is expected, not a
discrepancy.

**Live-verified on the user's machine:** a backup ran and produced a 638.83 MB snapshot; `icacls`
shows `BUILTIN\Users:(OI)(CI)(RX)` on the backups folder; the restore script was run end-to-end
and the service came back Running.

**Production auth state, verified 2026-07-28 12:58:38Z** by probing the live login route with
deliberately wrong passwords:

| user_id | before deploy | after deploy |
|---|---|---|
| `admin` | INVALID_CREDENTIALS (existed) | USER_NOT_FOUND |
| `operator` | INVALID_CREDENTIALS | USER_NOT_FOUND |
| `auditor` | INVALID_CREDENTIALS | USER_NOT_FOUND |
| `developer` | INVALID_CREDENTIALS | USER_NOT_FOUND |
| `insideporta2@gmail.com` | — | INVALID_CREDENTIALS (exists) |

---

## 2. What was done on 2026-07-28

### The default-credential hole — closed

`stableUsers()` in `src/core/authSessionStore.ts` hardcoded four accounts with fallback passwords
(`admin`/`porta-admin` and three others). `AUTH_USERS_JSON` was unset on Render, so
`loadBaseUsers()` returned that list, and `authenticateUser` fell through to `findLocalUser` when
no Postgres row matched — making `admin`/`porta-admin` a live admin login on production.

Fixed in `4449fa4`: `loadBaseUsers()` now returns `[]` unless **both** `NODE_ENV !== 'production'`
and an explicit `PORTA_ENABLE_DEFAULT_LOGINS` flag are true. An explicit `AUTH_USERS_JSON` still
works in production; the compiled-in defaults never ride along. Deployed and verified (table above).

**Interim mitigation, now redundant.** Four `AUTH_DEFAULT_*_PASSWORD` variables were set on Render
as a stopgap before the fix deployed. They no longer do anything. The user may delete them; there
is no urgency and no risk either way.

### Database changes made to production

- `insideporta2@gmail.com` (`account_id c8bf679d-1778-4751-a162-c661f23cdd48`) promoted from
  `developer` to `admin` in `auth_users`. This was done because there were **zero** admin rows in
  the table — every account was `developer`.

### Forensics — answered

One `auth_sessions` row exists for `user_id: "admin"`, issued 2026-05-27, expired 2026-06-26,
`revoked_at_utc` null. **The user has confirmed this was him.** No other default-account sessions
exist. No incident.

The row is expired and cannot be used. Revoking it is cosmetic — do not spend the user's time on
it unless he asks.

### Two facts established that closed open questions

- **The operations console does not use user sessions.** `client.ts:144-146` sends
  `x-operator-id`, `x-operator-role`, `x-operator-key: VITE_OPERATOR_KEY` on every request, hitting
  the shared-key branch of `requireAuthenticatedOperator`. It never touches `auth_users`, base
  users, sessions, or rbac. No auth change to those stores can lock the user out of the console.
- **rbac assignments are ephemeral.** `.data/rbac-assignments.json` is a relative in-container path
  with no mounted disk; `ensureLoaded` returns zero assignments when absent (`rbacStore.ts:63`), and
  a deploy restarts the container. So `resolveAssignedRoleForUser` falls back to `auth_users.role`
  after any deploy. *Caveat: Render's disk config was not read; this assumes no persistent disk is
  attached, which is Render's default.*

### New finding — hardcoded Ed25519 signing key (NOT fixed)

`process.env.CONTROL_PLANE_SIGNING_PRIVATE_KEY_PEM || defaultPrivateKeyPem` in
`policyGovernanceStore.ts:88` and `policyExecutionEngine.ts:191`. That env var is not set on Render
and `signer-private.pem` is gitignored, so **production signs with the key compiled into source.**

Measured: it is a real, valid Ed25519 key and the hardcoded public half matches the private half.
Not a placeholder.

Blast radius, traced: agents do **not** verify it — agent-side signing is HMAC self-signing of its
own telemetry (`Layer1AgentHost.cs:111`). The key covers the control plane's own audit-ledger and
signed-command integrity. So the exposure is **audit non-repudiation, not forged commands executing
on machines.**

Repo visibility checked, not inferred: `api.github.com/repos/PORTA-Tech/porta-control-plane` returns
404 unauthenticated while `microsoft/vscode` returns 200 on the same call — the repo is private.

Scope check: every `KEY`/`SECRET`/`PASSWORD`/`TOKEN`/`PEM` env read was grepped. This is the only
other instance of the pattern.

Queued as 4.3 below. It cannot ship alone — see that section.

---

## 3. Deliberately NOT done (do not silently re-add)

- **Per-IP rate limiting.** Built, then deleted at the user's explicit instruction. IP is not
  identity, the numbers had to be guessed, and a wrong guess silently breaks real agents. Do not
  reintroduce without asking. The proper fix is per-device auth.
- **The Supabase Auth migration** (Stages 0, 1, 2, 4, 5). Deferred by agreement. The existing
  custom auth — scrypt hashes, opaque 32-byte session tokens with TTL, RBAC route policies — is
  sound. **Stage 3 is the exception**, see 4.2.
- **Moving the in-memory narrative daily cap into Postgres.** Not worth it once the account check
  landed. Reconsider only if it becomes load-bearing.
- **Revoking the expired `admin` session row.** Judged cosmetic; see section 2.

---

## 4. What is left, in order

### 4.1 2FA on the Render account — DONE, in force since 2026-07-31

**2FA is enabled on both Render and the Google account behind it** (confirmed by the user
2026-08-04, in force since 2026-07-31). **This no longer gates anything — in particular the agent
MSI is NOT blocked on it.** Earlier sections of this document deliberately held the MSI back
pending this; that block is lifted. Do not reintroduce it, and do not remind the user again.

Why it mattered, kept because the underlying exposure has not changed: `requireSignature` still
defaults to **false** in the auto-update path (no code-signing certificate yet). The fleet's
protection is the SHA-256 in the Render environment variables. Anyone who can edit those can point
the manifest at any MSI, which installs as SYSTEM on every machine. Render is also the only place
the operator key and database URL live. It remains the highest-value credential in the project —
2FA raised the bar to reach it, it did not remove the exposure. A code-signing certificate is what
would actually close this.

### 4.2 Stage 3 — per-device auth — the main remaining work

Specified in `porta-control-plane/Supabase_Auth_Migration_Plan.md`, Stage 3. **It does not depend on
Supabase** — device secrets are random bytes hashed into the existing Postgres. Follow the plan as
written, with one change: where it says device registration requires a Supabase JWT, keep the current
session auth instead.

**Keep the plan's log-mode rollout exactly as specified.** It exists so a deploy cannot lock the
fleet out.

**Decisions already taken — do not re-open:**

- **Bootstrap: option B.** `POST /api/device/register` stays open, treated as inventory only. A
  separate authenticated endpoint issues the device secret. Reason: registration has two callers —
  `ControlPlaneReporter.cs:183` (the service, as LocalSystem at boot, before anyone signs in, no
  credential) and `MainWindow.Account.cs:164` (Control Center, already sends a bearer token).
  Requiring auth on `/register` breaks boot-time registration on every machine.
- **`GET /presence` is closed entirely, not device-auth'd.** It returns the whole fleet map and
  nothing in the agent repo calls it. Operator-only.
- ~~**`link-account` / `unlink-account` auth lands first, as its own commit.**~~ **DONE 2026-07-28** —
  see "What landed for link/unlink" below.
- **`heartbeat` and `telemetry` stay open.** They carry no user data and are memory-only. Accepted
  residual risk: an unauthenticated caller can pollute the in-memory presence map.

**Approved route classification:**

| Route | Disposition |
|---|---|
| `POST /heartbeat` | open (bootstrap) |
| `POST /telemetry` | open (bootstrap) |
| `POST /categories/resolve` | device auth |
| `POST /machine-summary` | **two-tier — see below** |
| `POST /action-events` | device auth |
| `POST /daily-stats` | device auth |
| `POST /narrative` | device auth |
| `POST /daily-digest` | device auth |
| `GET /user-context` | device auth + account match |
| `GET /presence` | operator only |
| `POST /device/register` | open (inventory only, no secret) |
| `POST /device/link-account` | operator session + account match — **DONE** |
| `POST /device/unlink-account` | operator session + account match — **DONE** |

**`machine-summary` is two-tier, and this is deliberate (decided 2026-07-28):**

Without a valid device credential the report is accepted as **anonymous inventory only**: the
caller-supplied `accountId` and `orgId` are discarded, `reconcileDeviceProfileFromTelemetry` runs
with nulls, and no install event, no `account_active_day` and no account activation are written.
Uptime samples still land, because they are machine-scoped presence. With a valid credential the
full reconcile runs. A credential proves exactly one device, so it is also checked against the
payload's `deviceId` — one device's secret cannot write another device's row.

This route is **not** subject to `PORTA_DEVICE_AUTH_MODE`; the tiering is permanent.

**Why this is a stronger rule than `heartbeat` and `telemetry`, which are simply open:** those two
are memory-only — they populate the in-memory presence map and nothing else, so the worst an
unauthenticated caller achieves is noise that dies with the process. `machine-summary` **persists**:
it writes `devices`, `machine_summaries`, `growth_events` and `account_active_days`. An
unauthenticated write here would outlive the process and would attach a real account to a row
nobody proved they own. Hence: visible, but anonymous.

Reason it is not simply closed: an unlinked device can never hold a credential (issuance requires a
linked device), and `machine-summary` is the device-inventory feed. Closing it would make a machine
that installed and was never signed into invisible in the console — which is exactly the machine you
need to see during a rollout.

**The enforce gate is a query, not an adoption count.** A device can be fully updated and still hold
no credential, so counting agent versions proves nothing. Before flipping
`PORTA_DEVICE_AUTH_MODE=enforce`, this must return zero:

Column names verified against the `CREATE TABLE devices` and the `ALTER TABLE` in
`src/store/fleetPersistence.ts`. Note the inconsistency: the table's timestamps are `last_seen_at`,
`first_seen_at` (no suffix) but the column added for this is `device_secret_created_at_utc` (with
suffix). Getting `last_seen_at_utc` wrong is the easy mistake here.

```sql
-- Must return 0 before flipping PORTA_DEVICE_AUTH_MODE=enforce.
SELECT count(*) AS blocking_devices
FROM devices
WHERE current_account_id IS NOT NULL
  AND device_secret_hash IS NULL
  AND status <> 'revoked'
  AND revoked_at IS NULL
  AND last_seen_at > now() - interval '7 days';
```

```sql
-- The same rows, listed, for when the count is not 0 and you need to know which machines.
SELECT device_id,
       machine_name,
       agent_version,
       current_account_id,
       last_seen_at
FROM devices
WHERE current_account_id IS NOT NULL
  AND device_secret_hash IS NULL
  AND status <> 'revoked'
  AND revoked_at IS NULL
  AND last_seen_at > now() - interval '7 days'
ORDER BY last_seen_at DESC;
```

Why each clause is there: `current_account_id IS NOT NULL` because unlinked devices can never hold
a credential and are exempt by design; `device_secret_hash IS NULL` is the actual question;
`status <> 'revoked'` and `revoked_at IS NULL` because a revoked device must not block the flip;
the 7-day window because a laptop that has been off for a month should not either — widen it if
you have seasonal users.

The counter `agent_machine_summary_anonymous_total` (audit metrics) tracks how much of the fleet is
still reporting anonymously, and `agent_device_account_mismatch_total` records every request that
enforce mode *would* have refused. Both should approach zero before the flip.

**Stage 3 progress as it stood on 2026-07-28 — HISTORICAL, superseded.** Stage 3 is complete and
enforce is live (see the 4.2 completion note below), and agent 1.2.126 shipped 2026-08-04. Kept
because the reasoning is still useful; do not read the status claims here as current.

Built and tested:

- `devices.device_secret_hash` + `device_secret_created_at_utc`; `deviceCredentialStore` stores
  sha256 only and compares in constant time.
- `POST /api/device/issue-credential` — account session, device must be linked to that account,
  re-issue rotates.
- `requireDeviceAuth` / `attachDeviceIdentity` middleware + `PORTA_DEVICE_AUTH_MODE` (default `log`).
- `machine-summary` two-tier (above). **This is the only route the middleware is wired to so far.**
- Agent: DPAPI credential storage in `C:\ProgramData\PORTA\secrets\device_credential.bin` (note:
  that is the real secrets path — the plan document's `PortaAgent\security\` is stale);
  `POST /v1/identity/device-credential` hand-off, which refuses a credential for a different
  deviceId; `/identity/unlink` deletes the credential; `ControlPlaneClient` attaches both headers
  when present; throttled `fleet_unauthorized` log line distinguishing missing from rejected.
- Control Center obtains the credential automatically on link **and** on every account refresh when
  the service reports none. The refresh path exists because the Control Center has no autostart
  entry (`Product.wxs` gives it shortcuts only; only `Porta.UserAgent` autostarts), so anything
  needing a deliberate unlink/relink would never reach most of the fleet.

- `requireDeviceAuth` now guards `categories/resolve`, `action-events`, `daily-stats`, `narrative`,
  `daily-digest` and `user-context`, in **log mode** — uncredentialed calls are counted and let
  through, so the deploy changed nothing for the live fleet.
- `GET /api/agent/user-context` has the device↔account match. It took an `accountId` off the query
  string with no credential and returned that person's first name, weak hour, average streak and
  strong-window bounds. Counted in log mode, 403 in enforce.

**Still open:**

- **Nothing in the fleet holds a credential yet**, so until the agent ships, every device is on the
  anonymous path. Consequence, analytics-only and verified: `machine_installed`,
  `account_active_day` and `account_activated` are not written during the gap. Existing
  device→account links are **not** erased. `account_active_day` and `maybeActivateAccount` feed
  only `growthReadService` → `/v1/growth` (operator-only console dashboards); `maybeActivateAccount`
  does not mutate `accounts.status`, and neither the digest nor the narrative builder reads active
  days. No user experiences any of it.
- ~~Agent MSI not built. **Blocked deliberately** on Render 2FA (§4.1)~~ — **DONE. Agent 1.2.126
  released 2026-08-04.** The 2FA block was lifted (in force since 2026-07-31, §4.1) and the MSI
  was cut and shipped. Every device therefore now runs an agent that requests and stores a
  refresh token.
- The enforce flip itself (Render env var), gated on the query above.

`deviceId` is `Guid.NewGuid()` in `LocalIdentityLinkStore.cs`, persisted to
`C:\ProgramData\PortaAgent\identity-link.v1.json` — a directory where `SharedDataAclHealer` grants
`BUILTIN\Users` Modify. So any standard user can read it and delete it to mint a new one. Treat
`deviceId` as an identifier, never a secret; the credential is the only proof.

**What landed for link/unlink (2026-07-28):**

A new `requireAccountSession` in `src/api/middleware/auth.ts` guards both routes. It is session-only
by design and deliberately does **not** fall back to the `x-operator-key` header branch that
`requireAuthenticatedOperator` uses: that key is a shared secret baked into the operations-console
bundle at build time and carries no account, so it cannot satisfy an account-ownership check.
Nothing in the console calls these routes, so closing the header path costs nothing. There is a test
asserting the operator key is rejected.

Both middlewares now set `req.operatorAccountId = session.account_id`, which defuses trap 1 below
for the rest of Stage 3.

Measured, not assumed: control plane `npm run test` → **204 passing** (was 197; 7 tests added).
Agent `dotnet test src\Porta.Agent.Service.Tests` → **92 passing**, full solution builds clean.

Two things found while doing it that the earlier survey missed:

1. `unlinkDeviceIdentity` skips its ownership check entirely when `accountId` is null
   (`identityProfileService.ts:641` — `if (request.accountId && ...)`), and `accountId` is optional
   on the wire. So omitting it was a way to unlink **any** device by guessing a device id. The
   controller now substitutes the session's account before calling the service. There is a
   regression test for exactly this.
2. The **service** client `Porta.Agent.Service/Fleet/ControlPlaneClient.cs` also has
   `LinkDeviceAccountAsync` / `UnlinkDeviceAccountAsync` (lines ~213, ~224) posting with no bearer
   token. They are **dead code — grepped, zero callers anywhere in the repo** — so nothing broke.
   They were left in place rather than deleted to keep this commit to one concern. If anyone wires
   them up they will now get a 401; give them an access token at that point. The handoff's earlier
   claim that `ControlPlaneFleetClient.cs` was the only unauthenticated caller was incomplete.

**Highest-value single item:** `GET /api/agent/user-context` (`agentController.ts` ~line 988) takes
an `accountId` from the query string with no credential and returns `firstName`, `weakHour`,
`avgStreakMin`, the strong-window bounds and `daysOfData`.

**Two traps — each costs a debugging session if missed:**

1. `requireAuthenticatedOperator` (`src/api/middleware/auth.ts`) sets
   `req.operatorId = session.user_id` — **not** `account_id`. Writing the account-match against
   `operatorId` compares a user_id to an accountId and every legitimate link fails. **Defused
   2026-07-28:** both middlewares now also set `req.operatorAccountId`. Use that field; `operatorId`
   still means user_id.
2. Header attachment is **two clients, not one**. `ControlPlaneClient.cs` handles most routes
   including `categories/resolve` (line 272), but `Porta.Agent.Runtime/Transport/PortaClient.cs`
   posts `heartbeat` and `telemetry` through a separate `HttpClient`.

`porta-operations-console` was grepped including `node_modules`: it only calls `/v1/admin/...`,
already behind operator auth and RBAC. The three `/api/*` hits were dead declarations in a generated
OpenAPI types file. **Stage 3 cannot break it.**

### 4.3 Rotate the hardcoded signing key -- DONE 2026-07-30

Rotated and verified. A new Ed25519 pair was generated, both
`CONTROL_PLANE_SIGNING_PRIVATE_KEY_PEM` and `CONTROL_PLANE_SIGNING_PUBLIC_KEY_PEM` are set on
Render, and the compiled-in fallbacks are gone from `policyGovernanceStore.ts` and
`policyExecutionEngine.ts` (commit `6fdee29`). A missing key now throws instead of silently
signing under a key in the repo.

**Verified against the live service, not the code:** production was made to sign a fresh
intervention command via `POST /v1/policy/evaluate`; the signature verifies under the new public
key and fails under the old one, with the payload hash recomputed independently to confirm the
signature covers it. Server-side replay reports `verified`, so both halves are in effect.
Re-checked after the fallback-removal deploy: still signing, still new key.

**Two traps this hit, worth keeping:**

1. The first attempt set the Render variables using the *filenames* (`signer-private.pem`) as the
   variable names. Nothing failed -- the code fell back to the compiled-in key and kept signing
   happily. Only signing something and verifying it client-side caught it. A green deploy proves
   nothing here.
2. A malformed key throws; an absent one falls back silently. So "signing still works" is not
   evidence the rotation landed -- you must check *which* key signed.

**Consequence:** audit traces signed by the old key no longer verify. The trace format carries
`key_id` but only one key is supported, so historical verification resets at the rotation. Scope
multi-key verification separately if those traces matter.

### 4.3a (superseded) Original notes on the hardcoded signing key

Details in section 2. Not urgent — private repo, agents do not verify it — but real.

**It cannot ship as a code change alone.** Failing closed means production stops signing until the
env var is set, and the current key must be treated as compromised and rotated. Required order:

1. Generate a new keypair.
2. Set **both** `CONTROL_PLANE_SIGNING_PRIVATE_KEY_PEM` and `CONTROL_PLANE_SIGNING_PUBLIC_KEY_PEM`
   on Render **as a matched pair.** Setting only the private half leaves the verifier falling back
   to the compiled-in public key, and everything the new key signs fails verification.
3. Then deploy the code change that removes the fallback.

Get the user's go-ahead before starting. Do not set Render variables yourself — you cannot, and
should not ask for their values.

### 4.4 Encryption at rest for `core-state.db`

No SQLCipher, no `PRAGMA key` today. Cheap now (a handful of databases exist), expensive later.

**Settle the key-recovery question before writing any code.** If the key is DPAPI machine-bound, a
dead machine makes its own backups unreadable — both jobs done, everything still lost. Simplest
honest option: accept that backups are machine-local and protect against corruption, not hardware
death.

### 4.5 Off-machine backup

What the user originally asked for. A copy on the same disk does not survive a dead drive, a stolen
laptop, or ransomware (which runs as admin and can take ownership of any local folder — the
read-only ACL only stops non-admin users).

**Required, in this order:** a line in the existing consent screen describing what is uploaded, a
Settings switch defaulting to off, then the upload itself. Uploading a user's full behavioural
history under a consent that only mentions local tracking is the specific thing that draws
regulatory penalties — the storing is not the problem, the not-telling is. The user has UAE users
(digest runs on Dubai time).

Do it **after 4.2**: centralising every user's history raises the value of a control-plane breach,
and the control plane still has unauthenticated endpoints until Stage 3 lands.

### 4.6 Never reviewed at all

These are unexamined, not known-good. Say so plainly if the user asks whether they are safe.

- The widget, Control Center, and UserAgent processes
- Local API (127.0.0.1) input validation
- ~~Dependency vulnerabilities in either repo~~ — **scanned 2026-07-30, see 4.7**
- Log redaction — there is a 71 MB `_debug-user-agent.log` in the agent repo root
- `porta-operations-console` beyond its auth mechanism (now known: build-time operator key)

### 4.2 Stage 3 — COMPLETE, enforce live 2026-08-04

`PORTA_DEVICE_AUTH_MODE=enforce` is deployed. Verified against production, not from code:

| probe | result |
|---|---|
| `GET /api/agent/user-context` uncredentialed | **401** `DEVICE_CREDENTIAL_REQUIRED` |
| `categories/resolve`, `daily-stats`, `narrative`, `daily-digest`, `action-events` uncredentialed | **401** each |
| real device credential + its own account | **200**, returns the profile |
| real device credential + a different account | **403** `DEVICE_ACCOUNT_MISMATCH` |
| tampered secret | **401** |
| `machine-summary` uncredentialed | **400** on a bad body — still open, two-tier, correctly not mode-governed |

The 403 is the hole that mattered: `user-context` used to hand a person's weak hour, streak length
and strong-window bounds to anyone who knew an accountId. It is now closed both ways — no
credential, and wrong credential.

**The self-heal was proven end to end on AYMENE**, which had burned its issuance on 1.2.123. After
`device_secret_hash` was cleared it re-bootstrapped at 07:19:17Z and was reporting credentialed by
07:21:09Z — under two minutes, no reboot, nobody at the machine. Issuance log shows exactly two
entries, both for that device, and none since. That is the path every dormant machine will take.

Use `scripts/clear-device-credential.mjs <deviceId>` (porta-control-plane, needs `DATABASE_URL`) to
recover any device whose plaintext is lost. Issue-once means there is no other way back.

**Still true and worth knowing:** the credential is readable by any authenticated local user —
`C:\ProgramData\PORTA\secrets` grants `Authenticated Users` Read, and DPAPI LocalMachine with a
source-constant entropy means read is enough. Device auth closes the *remote* hole; it does not
defend against someone with local access to that machine. Do not claim otherwise.

### 4.6a Remembered sign-in stored the PASSWORD — DONE and RELEASED 2026-08-04 (1.2.126)

Shipped to the fleet in agent **1.2.126**. The control plane was deployed and verified live first;
the agent followed.

| repo | commit | what |
|---|---|---|
| porta-control-plane | `f84f339` | refresh tokens on `auth_sessions`, `POST /auth/refresh` — deployed |
| porta-agent-windows-x64 | `d043c94` | v2 store holds a refresh token; v1 password file deleted; GetMeAsync fix |
| porta-agent-windows-x64 | `777d8ce` | SQLitePCLRaw 2.1.12 (CVE-2025-6965) |
| porta-agent-windows-x64 | `c274516` | v1.2.126, shared payload removed — **the released build** |

**Not yet proven end to end: a real client completing the refresh round trip.** See §0. Unit tests
cover rotation and reuse-detection, production probes cover the endpoint, and the install was
verified — but on a session that had not expired, so nothing has yet demonstrated a refresh token
being issued, stored in `control-center.remembered-signin.v2.json` and redeemed by an actual
Control Center. Sign out and back in with "Stay signed in" ticked to close that gap.

**Deploy order was deliberate and must be preserved if this is ever redone: server first.**
The agent's refresh path 404s harmlessly against an old control plane, but shipping the agent
first would mean a fleet holding tokens nothing can redeem.

**Verified against production 2026-08-04 13:08Z, not from code:**

| probe | result |
|---|---|
| `POST /auth/refresh` no body | **400** `VALIDATION_ERROR` |
| `POST /auth/refresh` bogus token | **401** `REFRESH_TOKEN_INVALID` |
| `GET /auth/me` bogus bearer | **401** `SESSION_EXPIRED` |
| `GET /health` | **200** |

The two bogus-token probes are the migration proof, and this is the trick worth keeping: both
run a `SELECT` whose column list now includes `session_type` and `refresh_family_id`. A missing
column would raise `column does not exist` and surface as a 500. A clean 401 means the SELECT
executed against the real table — so the `ALTER` applied. A green deploy alone proves nothing
(see 4.3 trap 2 for the same lesson). The user confirmed his Control Center stayed signed in
across the deploy, which is the live confirmation that pre-existing rows still authenticate.

**How it works:** `session_type` discriminates access from refresh on the existing
`auth_sessions` table (`DEFAULT 'access'`, so every pre-existing row is untouched and still
authenticates). `refresh_family_id` ties an access token to the refresh token that minted it
and each rotation to the one before. `/auth/refresh` rotates on every use; presenting a
rotated-away token revokes the **entire family**, because the stolen copy and the real client
cannot both redeem and there is no way to tell which is which. Refresh tokens are opt-in at
`/auth/login` via `issue_refresh_token` — only "stay signed in" asks for one.

`readAuthSession` and `refreshReauth` reject `session_type !== 'access'`, so a refresh token
can never authenticate a route. Logout revokes the whole family. The identity is re-derived
from the user store on every redemption, so a role change or deleted account lands at the next
refresh instead of riding the chain for 90 days.

**Two hazards that are not obvious:**

1. **Rolling the control plane back past `f84f339` is a privilege escalation. This is LIVE as of
   1.2.126.** The rollback removes the `session_type !== 'access'` guard, and any refresh token
   then authenticates as a bearer token for its full 90 days. This was harmless while no released
   agent requested one; 1.2.126 shipped 2026-08-04 and agents now do. **Do not roll the control
   plane back past `f84f339` without first revoking every refresh row** — an earlier version of
   this note said the hazard was theoretical, and that is no longer true.
2. **The SQLite fix needs a CLEAN rebuild.** An incremental build left the old 3.41.2
   `e_sqlite3.dll` in `bin/` while `dotnet list package` already reported 2.1.12, and
   `install.ps1` copies from payload without building. **Verify by extracting the version string
   from the binary, never from a package version.** Note the *target* changed in 1.2.126: there is
   no loose `e_sqlite3.dll` in the payload any more (§4.9), so the check is against the SQLite
   embedded in the three payload exes. `build-1296.ps1` does this and fails closed.

**Released:** agent 1.2.126, 2026-08-04, SHA-256
`71F0649EFDFEBFB772D1D06BB696D6B984C4D2BA37BCB87F1733BFB574007D75`.

### 4.6a (original writeup) Remembered sign-in stores the PASSWORD, not a token

`ControlPlaneRememberedSignInStore` persists the user's **password** to
`%LOCALAPPDATA%\PortaAgent\control-center.remembered-signin.v1.json`, DPAPI `CurrentUser`-scoped
(`UpdateRememberedSignIn(userId, password)` at `MainWindow.Account.cs:1344`, `PasswordEntropy`).

This is a bigger problem than the credential-acquisition work it was found next to. A stored
password is reusable everywhere that account is valid, cannot be scoped, cannot be revoked without
a password change, and survives every session expiry. A stolen profile yields the account itself,
not a 30-day session.

Compounding it: the **session** token at `control-center.account-session.v1.json` is *not* reliably
present — on the dev machine, signed in and linked, that file does not exist while the
remembered-signin file from the same sign-in moment does. So the durable on-disk secret is the
password, and the ephemeral one is the token. That is exactly backwards.

**What it would take to replace with a refresh token:**

1. Control plane: issue a refresh token at `/auth/login` alongside the access token. The
   `auth_sessions` table already has the right shape (`token_hash`, `expires_at_utc`,
   `revoked_at_utc`) — add a type discriminator and a longer TTL rather than a new table.
2. Control plane: `POST /auth/refresh` exchanging a refresh token for a new access token, rotating
   the refresh token on each use so a stolen one is single-use and its reuse is detectable.
3. Agent: `ControlPlaneRememberedSignInStore` stores the refresh token instead of the password.
   Keep DPAPI `CurrentUser`; the scope is right, the contents are not.
4. Migration: delete any stored password on upgrade. Users re-authenticate once. Do not attempt to
   silently convert a stored password into a token by logging in on the user's behalf.
5. Fix the session-clearing bug first (below) or the refresh token inherits the same problem.

Interacts with the Supabase migration: Stage 5 deletes the password path entirely, so if that is
still happening, this may fold into it rather than landing separately. Decide before building.

### 4.6b Automatic credential acquisition — decided 2026-07-31, not yet built

Requiring a user to open the Control Center is a design flaw, not a rollout step. Agreed approach:

**Superseded in part 2026-08-04:** the `GetMeAsync` bullet below is **DONE** (`d043c94`) — it
shipped with 4.6a because the refresh token would otherwise inherit the same bug. A 401 now
redeems the refresh token before signing anyone out. The A/B credential-acquisition work is
still unbuilt.

- **B (the mechanism):** the control plane issues a device credential without a session, for a
  device that is already linked and has none, one shot. This is the only option that works for a
  dormant machine that comes back weeks later with nobody present. It narrows the bootstrap
  decision in 4.2 — that was a deliberate re-decision, not an oversight.
- **A (the fast path):** `Porta.UserAgent` autostarts via an HKLM Run key (`Product.wxs:121`) and
  runs **as the user**, so unlike the service it can decrypt the `CurrentUser`-scoped session. It
  already uses `LocalApiClient`. It can call `issue-credential` and hand off through the existing
  `/v1/identity/device-credential` endpoint.
- **Plus:** fix `GetMeAsync` clearing the stored session on *any* failure including a transient
  network blip (`MainWindow.Account.cs:104`). Same bug class as the device calls fixed in `144e174`
  — an error collapsed to `null` and treated as an authoritative "expired".

**`install_secret` CANNOT serve as the `localProofToken`. Checked, do not revisit.**

Two independent reasons:

1. **The control plane has never seen it.** Grepped both repos: the install secret is never
   transmitted and the control-plane source contains no reference to it. A proof token the verifier
   cannot check is not a proof token — sending it would just be shipping another bearer secret.
2. **Sending it would break the privacy model.** It is the HMAC key that pseudonymises identifiers
   *before they are uploaded*: `FocusIdentityNormalizer.HashIdentifier` and
   `BucketPrivacyContract.HashIdentifier` both key HMAC-SHA256 on it, and the result travels as
   `focusAppIdentity` on the machine-summary payload (`FleetContracts.cs:73`,
   `MachineSummaryMapper.cs:125`). App identifiers are low-entropy, so anyone holding that key can
   brute-force the pseudonyms back to real app names — including retroactively, over data already
   uploaded. Giving the server this key de-anonymises the corpus.

If a `localProofToken` is wanted, it needs a secret the control plane can verify and that protects
nothing else. The field is already threaded through all three device payloads and the agent always
sends `null`; the control plane parses and discards it (`deviceIdentityController.ts:50/68`).

### 4.7 Dependency vulnerabilities — scanned 2026-07-30

**Control plane: nothing production-reachable.** `npm audit` reports 14, but only three survive
`npm ls --omit=dev`, all in express's tree, and none apply: `body-parser` needs an *invalid* `limit`
(we pass a valid `'1mb'` at `app.ts:88`), the `qs` advisory is in `qs.stringify` which we never call
(express uses `parse`), and the `path-to-regexp` 0.1.12 ReDoS needs two params in one segment while
every route separates them with `/` — probed empirically with a 60k-char input, ~0.1 ms, no
backtracking. Everything else, including the one "critical", is vitest/vite tooling that never runs
outside a dev machine.

**Agent: one real finding, not reachable today.**

`SQLitePCLRaw.lib.e_sqlite3` **2.1.6** — CVE-2025-6965 / GHSA-2m69-gcr7-jv3q, High (CVSS 7.2).
SQLite below 3.50.2; memory corruption when aggregate terms exceed available columns. Present in
every shipping project (Core, LocalApi, Runtime, Service, UserAgent, ControlCenter, VideoCapture)
via `Microsoft.Data.Sqlite` 8.0.10, which pins it transitively.

**Why it is not reachable:** the bug needs attacker-controlled SQL. All seven interpolated
`CommandText` sites use compile-time constants (`tableName`, `indexName`, `column`, `definition` in
`Db/Schema.cs`; `TaskColumns` / `DailyGoalColumns` in `SqliteBrainStateStore.cs`) with every value
passed as a `$` parameter. **This stops being true the moment anyone builds a query from user
input** — that is the change to catch in review.

**DONE 2026-08-04 (`777d8ce`), RELEASED in agent 1.2.126.** Pinned `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12
directly in the three projects with a direct `Microsoft.Data.Sqlite` reference; that overrides
the transitive 2.1.6 without moving off EF 8. Measured rather than inferred, because the version
strings mislead: `lib.e_sqlite3` **2.1.11 ships SQLite 3.49.1** (still vulnerable) and **2.1.12
ships 3.53.3** (fixed). Confirmed in the build output, not just the package graph. See the
clean-rebuild trap in 4.6a — this is the one that will bite.

**Original decision, kept for context: fold the upgrade into the next release rather than cutting
one for it.** Not urgent, but do not let it drift indefinitely.

One correction for whoever picks this up: the GitHub advisory page says no patched NuGet version
exists. That is stale. SQLitePCLRaw renumbered to track SQLite itself and `lib.e_sqlite3` now
publishes 3.50.3 and 3.53.3, both at or past the fix. Upgrade paths were deliberately not compared
here — do that when the release is being planned, and validate by building, not by reading version
numbers.

The two other .NET hits (`System.Net.Http` 4.3.0, `System.Text.RegularExpressions` 4.3.0) appear
only in `*.Tests` projects and are the stale-reference kind that net8.0 resolves to the runtime's
own implementations. Ignore them.

---

## 4.8 KNOWN-FAILING TESTS — `Porta.Agent.Core.Tests`, as of 2026-08-04

**The agent test suite is RED on `main` and this is expected. Do not treat a green Core.Tests run
as the baseline, and do not assume a red one means you broke something.**

Measured 2026-08-04 on `b312bf6`: `dotnet test src\Porta.Agent.Core.Tests` → **711 passed, 4
failed**. The four:

| test | symptom |
|---|---|
| `OverloadBrightnessScenarioTests.Scenario_RestartMidBrightnessRamp_ReconcilesContinuesAndRestoresWithoutDuplicateReducedEvent` | `Expected: 65, Actual: 70` |
| `DeepWorkSuppressionScenarioTests.Scenario_RestartDuringNotificationSuppression_ReconcilesWithoutDuplicateAndRestoresOnExit` | `Expected: 1, Actual: 0` |
| `Step20FinalAcceptanceGatesTests.Gate3_RestartNeverLeavesNotificationsSuppressedPermanently` | gate report assert |
| `Step20FinalAcceptanceGatesTests.Gate4_RestartNeverLeavesBrightnessDimPermanently` | gate report assert |

All four are restart-reconciliation scenarios — brightness ramp and notification suppression
surviving a restart. **Confirmed pre-existing, not inherited from the 4.6a/SQLite work:** the
changes were reverted and these same four still failed. They are unrelated to auth, sessions and
SQLite.

**Decision (user, 2026-08-04): these do NOT block the release.** They are to be fixed separately.

The other four suites are green and *are* a real gate — a failure there is yours:
ControlCenter.Tests 80, Service.Tests 104, UserAgent.Tests 247, Runtime.Tests 6.

When these are fixed, delete this section rather than editing the numbers — a stale allowance is
worse than none, because it silently excuses the next four failures too.

---

## 4.9 The installer shipped 619 fossil files — removed 2026-08-04, released in 1.2.126

**The MSI went from 622 files to 3. If you are wondering why it shrank by ~70 MB, this is why.**

`installer\payload\SharedFiles` was harvested wholesale into every MSI
(`HarvestDirectory` → `SharedPayloadComponentGroup` → `ComponentGroupRef` in `Product.wxs`). That
mechanism had been **dead since single-file publish was adopted**. The build step that fed it is:

```powershell
Get-ChildItem publish\service -Exclude "ari.exe","*.pdb" | Copy-Item -Dest SharedFiles -Force
```

`ari.exe`, `Porta.UserAgent.exe` and `Porta.ControlCenter.exe` are published
`--self-contained /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true`, so
publish emits **only the exe plus pdbs**. That copy has produced nothing for a long time. The ~617
files sitting in `SharedFiles` were fossils from an older non-single-file era, surviving purely
because `Copy-Item -Force` overwrites what it copies but never deletes what publish stopped
emitting. File dates in there ranged from Aug 2023 to Dec 2025 — accreted across several eras.

Among them: a `Porta.Agent.Runtime.exe` (framework-dependent, which the 400+ loose DLLs existed to
serve) referenced **nowhere** in any `.cs`, `.wxs` or `.ps1`; and an `e_sqlite3.dll` at SQLite
**3.41.2**, i.e. the CVE-2025-6965 version, installed to `C:\Program Files\Ari` on every machine in
the fleet.

**How this was found, because the method generalises:** a clean rebuild wiped `SharedFiles`, and
the CVE gate then failed complaining the DLL was missing. The gate's own assumption was wrong — it
was checking a loose file that only existed as a fossil — but the failure exposed the fossil. The
shipping MSI's contents were then read directly out of its `File` table via the
`WindowsInstaller.Installer` COM API, which is how "622 files" is a measurement and not an estimate.

**Verified before removing anything.** 1.2.125 was built with an empty payload and installed on a
real machine: service Running, database ready, local API responding, widget tracking, Control
Center signed in with a valid session. Only then was the structure changed. Do not skip this if
something similar comes up — nothing in source *proved* those files were unused, and the blast
radius was the whole fleet.

**Removed in 1.2.126 (`c274516`):** the `ComponentGroupRef` from `Product.wxs`; the
`HarvestDirectory`, `BindPath`, `SharedPayloadSourceDir` property, its `DefineConstants` entry and
the `EnsurePayloadHarvestDirectories` target from the wixproj; and the `WixToolset.Heat` package
reference, which existed only to serve the harvest.

**Three traps this produced, all worth keeping:**

1. **An empty `SharedFiles` will not link.** `heat` emits no ComponentGroup for an empty directory,
   so the unconditional `ComponentGroupRef` fails with `WIX0094`. 1.2.125 worked around it with a
   marker file *deliberately*, so the install test would not also be testing an installer
   restructure. If you ever repopulate the payload, that is the error you will hit first.
2. **The build scripts reported the MSI hash from the wrong directory.** Every script up to and
   including `build-1295.ps1` did
   `Get-ChildItem installer\bin\Release\*.msi | Sort LastWriteTime -Descending | Select -First 1`,
   but WiX writes to `installer\bin\x64\Release`. It only ever produced the right answer because
   the two paths happen to be hardlinked. Break that link — a clean, a copy, a WiX upgrade — and
   the script prints the SHA-256 of a **stale MSI**. While `requireSignature` is false (§4.1) that
   hash is the fleet's only integrity check on auto-update, so publishing a wrong one is a
   fleet-wide update failure. `build-1296.ps1` uses the exact output path, refuses to report a hash
   from an MSI older than the build, and reads `ProductVersion` back out of the MSI, throwing on
   mismatch.
3. **Nothing now ships loose DLLs, so `PublishSingleFile` is load-bearing.** Turn it off and the
   MSI silently omits hundreds of assemblies; the failure appears at runtime, nowhere near the
   cause. `build-1296.ps1` throws if `publish\service` emits any loose file. Do not delete that
   guard.

**Backup:** the 1.2.124 payload set (625 files) is at `C:\Users\hp\porta-payload-backup-1.2.124`.
It is redundant now that the empty payload is proven, and can be deleted. Note that the 1.2.124
MSI itself was overwritten in `bin\Release` by a later build — to reconstruct it, build from commit
`787e68a`.

---

## 5. Working rules — read before writing code

These come from real mistakes. The user caught them; the user should not have had to.

1. **Never state a number without measuring it first.** A rate limit of 120/min was invented, then
   found to fit ~6 agents once the real reporting interval was read. A 500 MB backup ceiling was
   invented when a real snapshot is 638 MB. Both numbers were sitting in the codebase, waiting to
   be looked up.
2. **Never assert anything about code you have not read.** `signer-private.pem` was called "in git
   history" based on a phrase in a planning document. It is gitignored and `git log --all` shows it
   was never committed.
3. **A question is not an instruction.** "What if we have more than 33 devices?" was answered by
   editing code, and the user had to stop it mid-edit. Answer the question; ask before changing
   anything.
4. **Do not quote the timelines in planning docs as commitments.** They are the user's own estimates.
5. **Be brief.** Short paragraphs, plain words, no jargon unless explained. Long structured answers
   are actively counterproductive here.
6. **Verify before claiming done.** `dotnet` IS available; `npm run test` works. "It compiles" and
   "the tests pass" are claims you are expected to have checked yourself.
7. **Exhaust what you can determine yourself before asking the user to do anything manual.** On
   2026-07-28 the user was walked through staging a production database URL twice to answer a
   lockout question that reading `porta-operations-console/client.ts` answered in one grep. Before
   asking the user to run a query, paste a credential, or click through a dashboard: check whether
   the answer is in code you can already read.
8. **When the user says they are confused or angry, stop and re-explain — do not push forward.**
   Re-state where things are in plain language, then give exactly one next action. Do not produce
   another block of instructions to paste.
9. **State clearly when something is inferred rather than measured.** "Likely" and "verified" are
   different words and the user relies on the difference.

---

## 6. Codebase facts that cost time to discover

**Test runner — this repo's source of truth is `npm run test`, not `npx vitest run`.** The npm
script uses `--pool=threads --poolOptions.threads.singleThread`, so every test file shares one
`process.env`. Suites that toggle env vars pollute each other under it. Validating with
`npx vitest run` gives false greens *and* false flakes: the rollout/slo/rbac tests that look flaky
under parallel `npx vitest run` all pass under `npm run test`. Measured 2026-07-28: `npm run test`
→ 197 passing, clean.

**Building and installing the agent (order matters):**

```powershell
powershell -ExecutionPolicy Bypass -File "build-1296.ps1"      # builds + fills installer\payload
powershell -ExecutionPolicy Bypass -File "scripts\install.ps1" # ONLY copies from payload
```

`install.ps1` does not build. Running it alone reinstalls stale binaries — check the dates it
prints. A new version means a new `build-<n>.ps1` plus a version bump in the csproj files.

**Start from `build-1296.ps1`, not an older one.** Copying an older script forward reintroduces
three things that were fixed in it — see 4.9. In particular every script before it reports the MSI
hash from the wrong directory.

**The MSI is written to `installer\bin\x64\Release\`, NOT `installer\bin\Release\`.** The two are
usually hardlinked, which is the only reason the old glob ever worked. Never take the hash from
`bin\Release` — see 4.9.

**Touching `core-state.db` from a script** requires all three, in order — `Stop-Service` alone is
not enough because `ari.exe` holds the file open and SCM recovery will relaunch it mid-operation:

1. `sc.exe failure AriAgent reset= 0 actions= none`
2. `sc.exe stop AriAgent`
3. Kill `ari`, `Porta.UserAgent`, `Porta.ControlCenter`

Then restore `reset= 60 actions= restart/15000/restart/15000/restart/15000` and start again. Do the
restore in a `finally` block — a failed operation that also leaves the agent dead is worse than a
failed operation.

**PowerShell:** with `$ErrorActionPreference = 'Stop'`, `taskkill` writing "process not found" to
stderr becomes a *terminating* error. Use `Get-Process | Stop-Process` instead.

**Sessions and identity:**

- `auth_sessions` rows are **never pruned** in Postgres mode — `cleanupExpiredSessions` iterates the
  in-memory map only. Expiry is enforced logically at read time. Good for forensics.
- Session TTL defaults to 30 days (`AUTH_SESSION_TTL_MINUTES` = 43200).
- `readAuthSession` returns the stored session record even when the identity no longer resolves, so
  a session survives changes to the user table independently. Auth changes do not evict live tokens;
  revocation is a separate step.
- Effective role runs through `resolveAssignedRoleForUser` (`rbacStore.ts:144`), so a fleet-scoped
  rbac assignment overrides `auth_users.role` — but that store is ephemeral (section 2).

**Verified schema** (from the `CREATE TABLE` statements in `fleetPersistence.ts`):

- `auth_sessions`: `session_id, token_hash, account_id, user_id, role, issued_at_utc,
  expires_at_utc, revoked_at_utc, updated_at_utc`
- `auth_users`: `account_id, user_id, role, status, source, password_hash, password_algo,
  last_login_at, updated_at`
- `accounts`: `primary_email, last_login_at, created_at`

**Code structure (agent):**

- `DbPaths` is `internal` to `Porta.Agent.Core`. Use `CoreStateDbStartup.InitializeAndMigrate()`.
- `AssemblyInfo.cs` has `InternalsVisibleTo("Porta.Agent.Service.Tests")`.
- Workers are registered in `AgentService.ConfigureServices` (~line 82–106).
- `SharedDataAclHealer` grants `BUILTIN\Users` **Modify** on `C:\ProgramData\PortaAgent\` with
  inheritance, then re-locks only `security\`. Anything else written there — including
  `core-state.db` — is deletable by any standard user. New folders must protect themselves.
- **`Porta.Agent.Service/Fleet/ControlPlaneClient.cs` has two dead tokenless callers.**
  `LinkDeviceAccountAsync` and `UnlinkDeviceAccountAsync` (~lines 213 and 224) post to
  `api/device/link-account` / `unlink-account` through `PostForResultAsync`, which attaches **no
  Authorization header**. Grepped 2026-07-28: **zero callers anywhere in either repo** — they are
  dead code, which is the only reason closing those routes did not break the service. **Both routes
  now require an account session.** If you wire either method up, pass an access token and send it
  as `Bearer`, or you will get a silent 401 — `PostForResultAsync` collapses every non-2xx to
  `null`, so it will look like the control plane is unreachable. The service runs as LocalSystem at
  boot with no signed-in user, so it has no token to send: if the service genuinely needs to link a
  device, that is a design question (device auth, Stage 3), not a header to add.

**What can and cannot be verified from a session:**

- **`dotnet` IS available** (8.0.417). Build and test your own work before reporting it.
- **PowerShell scripts can be parsed and exercised.** Parse-check with
  `[System.Management.Automation.Language.Parser]::ParseFile`; run end-to-end by pointing
  `$env:ProgramData` at a temp tree.
- **`.ps1` files must stay pure ASCII.** No BOM, so PowerShell 5.1 reads them in the ANSI codepage
  and a stray em dash silently truncates a string literal. `.cs` files are fine.
- **Elevation is NOT available.** Anything needing Administrator has to be run by the user.
- **Environment variables set in the user's interactive terminal do not reach tool subprocesses.**
  Do not design a workflow around them without checking first.
- `git` write operations from a sandbox can strand `.git/index.lock`, blocking the user's own
  commits. Fix: `Remove-Item <repo>\.git\index.lock`.
- Both repos have CRLF working copies with no `.gitattributes`. Always use
  `git diff --ignore-cr-at-eol`.

**Already correct — do not "fix" these:**

- `POST /api/agent/daily-digest` is already guarded: returns `account_not_found` when the profile is
  null and checks `wasDigestSentForAccount` before `buildDigestEmail`, both before any model call.
- Control-plane security headers are strong: HSTS with preload, CSP `default-src 'none'`, `nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, allowlisted CORS.
- Account IDs are UUIDs (`$1::uuid` casts throughout), so they are not enumerable.
- The Anthropic API key has a **$5 hard spend cap** in the Anthropic console.
- **`AgentUpdateWorker` — read and verified 2026-07-27.** It rejects a manifest with no `Sha256`,
  requires an `https` URL, verifies the hash three times (cached-file reuse, post-download, and
  immediately before launch), opens the MSI and checks its internal `ProductVersion` against the
  manifest, and caps downloads at 500 MB. Its only real exposure is whoever controls the Render
  environment variables — which is why 4.1 is first.
