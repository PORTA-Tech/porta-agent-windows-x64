# PORTA Security Work — Handoff

**Written:** 2026-07-27, end of session
**For:** the next session (Claude Code, or a fresh chat)
**Repos:** `porta-agent-windows-x64`, `porta-control-plane` (both needed; a third,
`porta-operations-console`, has never been inspected)

---

## How to use this document

Read the whole thing before touching code. Sections 4 and 5 are the important ones — section 5
lists mistakes that were actually made today, and repeating them will waste the user's time.

---

## 1. Verified state (as of this writing)

Both repos are committed, pushed, and in sync with `origin/main`. No uncommitted work.

| Repo | Commit | What |
|---|---|---|
| porta-control-plane | `ed111d4` | narrative: reject unknown accounts before spending a model call |
| porta-agent-windows-x64 | `07107b8` | backup: nightly local VACUUM INTO snapshots with 7-day retention |
| porta-agent-windows-x64 | `a727609` | restore-backup: survive missing processes, always restart the service |

**Test status:** control plane `npm test` → 192 passing. Agent
`dotnet test src\Porta.Agent.Service.Tests` → 69 passing.

**Live-verified on the user's machine** (not just unit tested):

- A backup ran at 20:04, produced `core-state-20260727-200403.db`, 638.83 MB, valid.
- `icacls` on the backups folder shows `BUILTIN\Users:(OI)(CI)(RX)` — read-only, as intended.
- The restore script was run end-to-end: stopped the service, swapped the database, restarted.
  Service came back Running.

---

## 2. What was done today

### porta-control-plane — `ed111d4`

`POST /api/agent/narrative` is unauthenticated (mounted before `requireAuthenticatedOperator`)
and calls Claude Opus. Its only spend control was `NARRATIVE_DAILY_CALL_CAP = 16`, keyed on
`` `${accountId}:${dateLocal}` `` — both attacker-controlled strings, so an attacker could mint a
fresh 16-call budget per made-up id, without limit.

Added `isKnownAccount()` in `src/core/narrativeService.ts`: UUID shape check, then
`getAccountProfile()` must return non-null. Runs **before** the budget is consumed and before the
model is called, and fails closed if the store is unreachable. Three tests cover it.

### porta-agent-windows-x64 — `07107b8`, `a727609`

`core-state.db` had no backup and no restore path. Added:

- `src/Porta.Agent.Service/DatabaseBackupWorker.cs` — nightly `VACUUM INTO` snapshot at 03:00
  local, ticking every 15 min so a sleeping laptop catches up. 7-day retention, 8 GB ceiling,
  never deletes the newest snapshot. Whole tick wrapped in try/catch.
- `scripts/restore-backup.ps1` — stop, swap, restart. Keeps the replaced database as
  `core-state.db.replaced-<stamp>`; never deletes it.
- `src/Porta.Agent.Service.Tests/DatabaseBackupWorkerTests.cs` — 17 tests, mostly retention.
- One line in `AgentService.cs` registering the worker; one `PackageReference` in the csproj;
  one assertion added to `AgentServiceCompositionTests.cs`.
- `Local_Backup_Plan.md` — the design, including what a local backup does NOT protect against.

---

## 3. Deliberately NOT done (do not silently re-add)

- **Per-IP rate limiting.** It was built, then **deleted at the user's explicit instruction**.
  The reasoning: IP is not identity, the numbers had to be guessed, and a wrong guess silently
  breaks real agents. Do not reintroduce it without asking. The proper fix is per-device auth.
- **The Supabase Auth migration** (`Supabase_Auth_Migration_Plan.md`, Stages 0, 1, 2, 4, 5).
  Deferred by agreement. The existing custom auth — scrypt hashes, opaque 32-byte session tokens
  with TTL, RBAC route policies — is sound. Replacing it is a maintenance-burden improvement, not
  a security fix. **Stage 3 is the exception** — see below.
- **Moving the in-memory narrative daily cap into Postgres.** Judged not worth it once the
  account check landed. Reconsider only if it becomes load-bearing.
- **Server-side / off-machine backup.** Requires a consent-screen change first. See section 4.

---

## 4. What is left, in order

### 4.1 Turn on 2FA for the Render account — minutes, do first

`requireSignature` defaults to **false** in the auto-update path (no code-signing certificate
yet, per `AutoUpdate_Plan.md`). The fleet's protection is the SHA-256 in the Render environment
variables. Anyone who can edit those can point the manifest at any MSI, which then installs as
SYSTEM on every machine. The Render login is the highest-value credential in the project.

### 4.2 Stage 3 — per-device auth (both repos) — the main remaining work

Specified in `porta-control-plane/Supabase_Auth_Migration_Plan.md`, Stage 3. **It does not depend
on Supabase** — device secrets are random bytes hashed into the existing Postgres. Follow the plan
as written, with one change: the plan says device registration requires a Supabase JWT; keep the
current session auth instead, since Supabase is deferred.

Closes, in one move, every unauthenticated route under `/api/agent`:
`heartbeat`, `telemetry`, `categories/resolve`, `machine-summary`, `action-events`, `daily-stats`,
`narrative`, `daily-digest`, `user-context`, `presence` — plus `/api/device`.

Highest-value single item in it: `GET /api/agent/user-context` (agentController.ts ~line 988)
takes an `accountId` from the query string with no credential and returns `firstName`, `weakHour`,
`avgStreakMin`, the strong-window bounds and `daysOfData`. Stage 3's device↔account match closes it.

**Keep the plan's log-mode rollout exactly as specified.** It exists so a deploy cannot lock the
fleet out.

### 4.3 Encryption at rest for `core-state.db`

No SQLCipher, no `PRAGMA key` today. Cheap now (a handful of databases exist), expensive later.

**Settle the key-recovery question before writing any code.** If the key is DPAPI machine-bound,
a dead machine makes its own backups unreadable — both jobs done, everything still lost. Simplest
honest option for now: accept that backups are machine-local and protect against corruption, not
hardware death.

### 4.4 Off-machine backup

What the user originally asked for. A copy on the same disk does not survive a dead drive,
a stolen laptop, or ransomware (which runs as admin and can take ownership of any local folder —
the read-only ACL only stops non-admin users).

**Requires, in this order:** a line in the existing consent screen describing what is uploaded,
a Settings switch defaulting to off, then the upload itself. Uploading a user's full behavioural
history under a consent that only mentions local tracking is the specific thing that draws
regulatory penalties — the storing is not the problem, the not-telling is. The user has UAE users
(digest runs on Dubai time).

Also do it **after** 4.2, not before: centralising every user's history raises the value of a
control-plane breach, and the control plane currently has unauthenticated endpoints.

### 4.5 Never reviewed at all

Be honest with the user that these are unexamined, not known-good:

- `porta-operations-console` — the repo has never been opened
- The widget, Control Center, and UserAgent processes
- Local API (127.0.0.1) input validation
- Dependency vulnerabilities in either repo
- Log redaction — there is a 71 MB `_debug-user-agent.log` in the agent repo root

---

## 5. Working rules — read this before writing code

These come from mistakes made today. The user caught them; the user should not have had to.

1. **Never state a number without measuring it first.** A rate limit of 120/min was invented,
   then found to fit only ~6 agents once the real reporting interval was read. A backup folder
   ceiling of 500 MB was invented, when a real snapshot is 638 MB — it would have silently
   capped retention at a single file. Both numbers were sitting in the codebase or on disk,
   waiting to be looked up.
2. **Never assert anything about code that has not been read.** `signer-private.pem` was called
   "the worst single thing in the plan, and it's in git history" — based on a phrase in a
   planning document. It is in `.gitignore` and `git log --all` shows it was never committed.
3. **A question is not an instruction.** "What if we have more than 33 devices?" was answered by
   editing code. The user had to stop it mid-edit. Answer the question; ask before changing
   anything.
4. **Do not quote the timelines in the planning docs as if they were real.** They are the user's
   own estimates, not commitments. Saying "3–4 days" or "the July target is gone" was unhelpful
   and annoying.
5. **Be brief.** Long, structured answers were actively counterproductive here. Short paragraphs,
   plain words, no jargon unless it is explained.
6. **Verify before claiming done.** Check what you can actually run before assuming you cannot —
   see section 6. `dotnet` IS available, so "it compiles" and "the tests pass" are claims you are
   expected to have checked yourself, not to hand to the user.

---

## 6. Codebase facts that cost time to discover

**Building and installing the agent (order matters):**

```powershell
powershell -ExecutionPolicy Bypass -File "build-1291.ps1"      # builds + fills installer\payload
powershell -ExecutionPolicy Bypass -File "scripts\install.ps1" # ONLY copies from payload
```

`install.ps1` does not build. Running it alone reinstalls stale binaries — check the dates it
prints. `build-1291.ps1` is the current one; a new version means a new `build-<n>.ps1` plus a
version bump in the csproj files.

**Touching `core-state.db` from a script** requires all three, in order — `Stop-Service` alone is
not enough because `ari.exe` holds the file open and SCM recovery will relaunch it mid-operation:

1. `sc.exe failure AriAgent reset= 0 actions= none`
2. `sc.exe stop AriAgent`
3. Kill `ari`, `Porta.UserAgent`, `Porta.ControlCenter`

Then restore `reset= 60 actions= restart/15000/restart/15000/restart/15000` and start again.
Do the restore in a `finally` block — a failed operation that also leaves the agent dead is worse
than a failed operation.

**PowerShell:** with `$ErrorActionPreference = 'Stop'`, `taskkill` writing "process not found" to
stderr becomes a *terminating* error. `install.ps1` gets away with `taskkill` only because it does
not set that preference. Use `Get-Process | Stop-Process` instead.

**Code structure:**

- `DbPaths` is `internal` to `Porta.Agent.Core`. Use `CoreStateDbStartup.InitializeAndMigrate()`
  — public, idempotent, returns the path.
- `AssemblyInfo.cs` has `InternalsVisibleTo("Porta.Agent.Service.Tests")`, so workers can be
  `internal sealed` and still tested.
- Workers are registered in `AgentService.ConfigureServices` (~line 82–106), which is called from
  the local API host builder at line 39.
- `SharedDataAclHealer` grants `BUILTIN\Users` **Modify** on `C:\ProgramData\PortaAgent\` with
  inheritance, then re-locks only the `security\` subfolder. Anything else written there — including
  `core-state.db` itself — is deletable by any standard user. New folders must protect themselves.

**What can and cannot be verified from a session (corrected 2026-07-28):**

- **`dotnet` IS available** (8.0.417, `C:\Program Files\dotnet\dotnet.exe`). The agent repo builds
  and `dotnet test src\Porta.Agent.Service.Tests` runs. An earlier version of this document said
  otherwise; that was wrong and cost a session's worth of "the user will have to check this".
  Build and test your own work before reporting it.
- **PowerShell scripts can be parsed and exercised.** Parse-check with
  `[System.Management.Automation.Language.Parser]::ParseFile`, and run a script end-to-end by
  pointing `$env:ProgramData` at a temp tree so it never touches real data. Both found real bugs
  on 2026-07-28 that review alone had missed.
- **`.ps1` files must stay pure ASCII.** They have no BOM, so PowerShell 5.1 reads them in the
  ANSI codepage and a stray em dash silently truncates a string literal — the script then fails
  to parse. `.cs` files are fine and already use em dashes. Parse-check any `.ps1` you edit.
- **Elevation is NOT available.** `Start-Process -Verb RunAs` is refused by the permission layer,
  so anything needing Administrator — stopping/starting `AriAgent`, a real restore, `icacls` on a
  protected folder — has to be run by the user. Reading service state and ACLs works unelevated.
- Control-plane `npx vitest` fails — `node_modules` was installed on Windows and the Linux rollup
  binary is missing. `npx tsc --noEmit` does work, but ~67 pre-existing errors from
  `@porta/api-contracts` failing to resolve are environmental noise; filter for changed files.
- `git` write operations from the sandbox fail with "Operation not permitted" and **strand
  `.git/index.lock`**, which then blocks the user's own commits. This happened twice. Fix:
  `Remove-Item <repo>\.git\index.lock`.
- Both repos have CRLF working copies with no `.gitattributes`, so `git diff` shows whole files as
  changed. Always use `git diff --ignore-cr-at-eol` to see real changes.

**Already correct — do not "fix" these:**

- `POST /api/agent/daily-digest` also reaches a model (`buildAriTake`), but is already guarded:
  it returns `account_not_found` when the profile is null, and checks `wasDigestSentForAccount`
  **before** `buildDigestEmail`. Both run before any model call.
- Control-plane security headers are strong: HSTS with preload, CSP `default-src 'none'`,
  `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, allowlisted CORS.
- Account IDs are UUIDs (`$1::uuid` casts throughout), so they are not enumerable.
- The Anthropic API key has a **$5 hard spend cap** set in the Anthropic console. This is the real
  backstop on model spend and it is already in place.
- **`AgentUpdateWorker` — read and verified 2026-07-27.** It does what its comment claims:
  rejects a manifest with no `Sha256`; requires an `https` download URL; verifies the hash three
  separate times (cached-file reuse, post-download, and again immediately before launch); opens the
  MSI and checks its internal `ProductVersion` against the manifest; caps downloads at 500 MB.
  `Sha256MatchesAsync` is a correct hex comparison. The update channel is sound — its only real
  exposure is whoever controls the Render environment variables, which is why 4.1 is first.
