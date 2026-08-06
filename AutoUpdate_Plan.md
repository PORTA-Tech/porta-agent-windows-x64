# Ari Auto-Update — Implementation Plan
**Goal: the fleet updates itself silently with zero user action. Users only ever see one small widget message: "Ari updated." Written 2026-06-10.**

---

## Architecture decision

Today's implementation lives in the Control Center: check → popup → user clicks → UAC prompt → elevated PowerShell installs. That can never reach "zero user action" because it depends on the Control Center being open and a human accepting elevation.

**The updater moves into the Windows service (`ari.exe`).** The service already runs as SYSTEM on every machine, always — it can download, verify, and install with no UAC, no clicks, no Control Center. The Control Center keeps a quiet manual "Check for updates" page; its automatic popup/overlay is disabled. The widget (what users actually see) shows exactly one thing: a small confirmation after a successful update.

What gets REUSED (no rebuilding):
| Asset | Where | Change |
|---|---|---|
| Release manifest endpoint | control plane `clientReleaseService.ts` (env-var driven) | +2 fields |
| `WindowsUpdateInstallScriptBuilder` (verify → stop service → msiexec /qn → restart) | Porta.ControlCenter | move to shared project, parameterize |
| `windows-update.status.json` outcome file | written by the install script | widget reads it for the post-update message |
| SHA256 verification | both sides | unchanged, stays mandatory |
| Control Center update page | Porta.ControlCenter | keep as manual fallback; disable auto-popup |

---

## Part 1 — Control plane: manifest v2 (~30 min)

`clientReleaseService.ts`, two new env-driven fields, additive:
- `requireSignature` (bool, from `PORTA_WINDOWS_RELEASE_REQUIRE_SIGNATURE`, default **false**) — Authenticode enforcement is manifest-controlled. Today's hard-coded `!downloadUri.IsLoopback` rule made every unsigned update fail — that is what blocked this work. When the certificate arrives: sign the MSI, set the env var to true. The cert gates the flag, not the feature.
- `rolloutPercent` (0–100, from `PORTA_WINDOWS_RELEASE_ROLLOUT_PERCENT`, default 100) — staged rollout.

Publishing a release = upload MSI to GitHub Releases + set 4 env vars on Render (version, URL, SHA256, rollout%). No deploy needed.

## Part 2 — Hosting: GitHub Releases (process change, no code)

Google Drive links CANNOT be the update source: share links return an HTML page (with a virus-scan interstitial at 71 MB), not the file — programmatic downloads fail or corrupt.

- Create a release per version on `PORTA-Tech/porta-agent-windows-x64` (or a dedicated public releases repo if the code repo is private — **verify asset URL downloads without auth before first use**)
- Asset: `AriAgentInstaller-1.2.X.msi`, direct URL like `https://github.com/.../releases/download/v1.2.X/AriAgentInstaller-1.2.X.msi`
- The numbered `build-<version>.ps1` at the repo root already prints the SHA256 — copy it into the Render env var. (**Not** `scripts/build-msi.ps1` — hard-fail stub since 2026-08-06.)
- The website download link can also point at the same GitHub asset later (one source of truth; replaces the Drive shuffle)

## Part 3 — Service-side updater: `AgentUpdateWorker` (the core build)

New `BackgroundService` in `Porta.Agent.Service`, registered like the other workers.

**Check loop:** every 6h with random 0–30 min jitter (fleet doesn't stampede). Manifest fetch via existing ControlPlaneClient. Proceed only if ALL true:
1. Manifest version > installed version (semver compare — never sideways, never down)
2. Machine is inside the rollout gate: `hash(machineId) % 100 < rolloutPercent` (stable hash → same machines stay in/out as % grows)
3. This version hasn't permanently failed here before (≥3 failed attempts → blacklist version, report, wait for next release)
4. No update already in flight (lock file in ProgramData)

**Download & verify (into `C:\ProgramData\PORTA\updates\`):**
- HTTPS download → SHA256 compare against manifest — **mandatory, no hash = no install, ever**
- If `requireSignature`: Authenticode must be Valid
- Verify MSI ProductVersion matches the manifest version (no mislabeled uploads)

**Install timing — never interrupt work (the "no mistakes" rule users would feel):**
- Install only when: no interactive session, OR session locked, OR user idle > 10 min
- Never while a focus episode is active or a meeting app is foreground
- If no quiet window appears within 24h, install anyway at 03:00 local (machine is on, user almost certainly absent)

**Install:** reuse the existing PowerShell script builder, moved to a shared project and parameterized (it currently waits on a Control Center PID — make that optional). Launch as a detached SYSTEM process so it survives the service stopping itself. The script already: re-verifies hash → stops service → kills UserAgent/ControlCenter → `msiexec /qn` → restarts service. msiexec is transactional — a failed install rolls back to the working version automatically. That is the rollback story, and it's already built.

**Report:** the script's `windows-update.status.json` outcome (success/exit code/version) is included in the machine summary (one new optional field, same pattern as the log tail) → the console shows update progress across the fleet during a rollout, and a failed update is visible without touching anyone's machine.

## Part 4 — What the user sees: the widget, nothing else

- **Before/during: nothing.** No popup, no countdown, no "update available" nag. Updates at idle; the widget vanishes for ~30s mid-install and the existing watchdog relaunches it automatically.
- **After:** on UserAgent startup, compare current binary version to a `last-acknowledged-version` file; if newer, show ONE small toast via the existing `UserDesktopWidgetPresenter.TryShow`: *"Ari updated to 1.2.84 ✓"* (+ one optional short what's-new line from the manifest `summary` field). Write the file so it never repeats. Reuse the existing toast gating so it can never fire twice.
- **Control Center:** automatic update overlay + taskbar badge disabled (one flag); the Updates page stays for manual "Check now" — useful for support, invisible otherwise.

## Part 5 — Verification protocol (in order, no skipping)

1. **Local loop:** point your own machine at a manifest with `rolloutPercent=100` and a test 1.2.83 build → watch it download, wait for idle, install, relaunch, widget shows "updated ✓", console shows the outcome. Repeat with a deliberately WRONG SHA256 → must refuse and report. Repeat with same-version manifest → must do nothing.
2. **Canary:** publish manifest at `rolloutPercent` that includes only your machine. 48h of normal use — capture keeps working after self-update (the metric that matters).
3. **Fleet:** 1.2.83 announced via website (the last manual install ever). Once most machines run it, publish 1.2.84 (any small fix) at 10% → 50% → 100% over 2–3 days, watching the console's version column.
4. **Definition of done:** fleet reaches 1.2.84 within 24h of 100%, zero user actions, zero capture regressions.

## Safety invariants (the "no mistakes" checklist — every one enforced in code)
1. No SHA256 in manifest → no install, ever
2. Never install equal/lower versions; never reinstall a version that failed 3× on this machine
3. One update in flight per machine (lock file with staleness timeout)
4. Never install during active use — idle/locked/3am only
5. msiexec transactional rollback = failed install leaves the old version running
6. Staged rollout gate, stable per machine
7. Updater worker wrapped in try/catch like the capture loop — an updater bug can never take down capture
8. Outcome always reported to the console — silent failure is not an allowed state

## Order of work
| # | Task | Repo | Size |
|---|---|---|---|
| 1 | Manifest v2 (`requireSignature`, `rolloutPercent`) | control-plane | S |
| 2 | GitHub Release for 1.2.82 (test the URL pattern) | process | S |
| 3 | Move/parameterize install script builder to shared project | agent | S |
| 4 | `AgentUpdateWorker` (check, gate, download, verify, timing, launch, lock, blacklist) | agent | **L — the core** |
| 5 | Update outcome in machine summary + console version/update column | agent + control-plane + console | M |
| 6 | Widget post-update toast + disable Control Center auto-popup | agent | S |
| 7 | Verification protocol steps 1–4 | — | M |

Steps 1–6 are buildable now, entirely testable on your machine while the fleet's stability week runs untouched. 1.2.83 ships when the week is clean — the last version users ever install by hand.
