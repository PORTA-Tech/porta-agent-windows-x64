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
section 2. There is no open emergency. Start at 4.1.

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

### 4.1 Turn on 2FA for the Render account — the user's job, minutes

`requireSignature` defaults to **false** in the auto-update path (no code-signing certificate yet).
The fleet's protection is the SHA-256 in the Render environment variables. Anyone who can edit those
can point the manifest at any MSI, which installs as SYSTEM on every machine. Render is also the
only place the operator key and database URL live. It is the highest-value credential in the project.

This is not a code task. Remind the user once; do not nag.

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

```sql
SELECT device_id, machine_name, last_seen_at
FROM devices
WHERE current_account_id IS NOT NULL
  AND device_secret_hash IS NULL
  AND last_seen_at > now() - interval '7 days';
```

Unlinked devices are exempt by design and must not appear in the gate. The counter
`agent_machine_summary_anonymous_total` (audit metrics) tracks how much of the fleet is still
reporting anonymously.

**Stage 3 progress (2026-07-28, all pushed, nothing released to the fleet):**

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

**Still open — do these next:**

- `requireDeviceAuth` is **not** wired to `categories/resolve`, `machine-summary`'s siblings
  (`action-events`, `daily-stats`, `narrative`, `daily-digest`) or `user-context`. Those routes are
  still fully unauthenticated.
- **`GET /api/agent/user-context` still has the account-match hole** — it takes an `accountId` from
  the query string with no credential and returns the user's behavioural profile. Highest-value
  item left.
- Agent MSI not built. **Blocked deliberately** on Render 2FA (§4.1) — shipping a fleet-wide update
  while the update manifest's only protection is a hash in the Render environment is the wrong order.

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

### 4.3 Rotate the hardcoded signing key

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
- Dependency vulnerabilities in either repo
- Log redaction — there is a 71 MB `_debug-user-agent.log` in the agent repo root
- `porta-operations-console` beyond its auth mechanism (now known: build-time operator key)

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
powershell -ExecutionPolicy Bypass -File "build-1291.ps1"      # builds + fills installer\payload
powershell -ExecutionPolicy Bypass -File "scripts\install.ps1" # ONLY copies from payload
```

`install.ps1` does not build. Running it alone reinstalls stale binaries — check the dates it
prints. A new version means a new `build-<n>.ps1` plus a version bump in the csproj files.

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
