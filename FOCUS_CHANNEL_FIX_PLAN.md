# Focus-Channel / Calibration Fix Plan (v1.2.92)

**Status:** approved to implement both fixes. No code written yet — this is the plan.
**Author context:** Diagnosed from live fleet data (12 machines) + full code trace, June 2026.

---

## 1. The confirmed problem (one sentence)

Machines that are online and capturing keyboard/mouse never calibrate (stuck "Warming up", 0% pressure, no focus data) because their **`focus` (foreground-app) channel is missing**, which forces a quality penalty that pins signal quality below the calibration bar **forever**.

## 2. The exact mechanism (traced end-to-end)

1. **Calibration is gated on signal quality.** `BucketDataQualityScorer.Compute`:
   - `focusAvailable = bucket.Focus.FocusEvents.Count > 0 || bucket.Focus.ObservedIntervalCount > 0`
   - if `!focusAvailable` → `missing.Add("focus")`, `score -= 0.24` (**FocusMissingPenalty**), `partial = true`.
   - Result: quality sits ~0.5–0.66, never reaches Calibrated.

2. **The focus channel is populated only from a non-empty posted `focusAppHash`.** `BucketAggregators.ApplyInputSignalsSliceToBucket` (line ~189):
   ```csharp
   if (!string.IsNullOrWhiteSpace(focusAppHash))
       bucket.Focus.RecordObservedInterval(sliceStartUtcMs, sliceEndUtcMs, focusAppHash);
   ```
   So if `focusAppHash` is null/empty in the posted packet → **no interval → focus channel missing.**

3. **The posted hash is `focus.DominantAppHash ?? fallbackFocusAppHash`** (`UserAgentHost.FlushSignalsAsync` line ~582), both ultimately from `UserSessionNativeSignals.TryGetForegroundAppHash()`.

4. **`TryGetForegroundAppHash()` returns null SILENTLY (no log) when:**
   - `GetForegroundWindow() == IntPtr.Zero`, or
   - `GetWindowThreadProcessId(...) == 0`.
   Only *exceptions* log (`foreground_app_hash_failed`). Production logs show **none** of those → it is the **silent null path**, i.e. the UserAgent's thread/desktop cannot see a foreground window (locked/secure desktop, disconnected/RDP session, or a desktop mismatch after a service-watchdog relaunch). Keyboard/mouse keep working via low-level (`WH_KEYBOARD_LL`/`WH_MOUSE_LL`) hooks that read the session input queue regardless of desktop.

## 3. Live evidence (production, all 12 machines)

| Machine | Ver | Quality | Missing channels | Calib |
|---|---|---|---|---|
| DESKTOP-0MMJH0I (owner) | 1.2.90 | 0.80 | `[keyboard]` (focus PRESENT) | **calibrated** |
| THOR | 1.2.91 | 0.58 | `[keyboard, mouse, focus]` | cold |
| AYMENE | 1.2.90 | 0.54 | `[mouse, focus]` | cold |
| DESKTOP-G014LVJ | 1.2.82 | 0.66 | `[keyboard, focus]` | cold |
| DESKTOP-842SQFP | 1.2.82 | 0.58 | `[keyboard, focus]` | cold |

- Independent of agent version (owner on older 1.2.90 works; THOR on 1.2.91 doesn't).
- Ruled out: install-secret failure / hook failure (no error codes; `capture_hooks_ready keyboard=True mouse=True`).
- Several broken machines also show **UserAgent churn**: watchdog relaunches (`stale_heartbeat`, `missing_process`) and a **widget restart loop** (AYMENE re-inits its widget ~every 5s; 842SQFP logs `desktop_widget_window_recovery_requested` → watchdog relaunch).
- Anomaly to revisit later (not this cohort): EBBY & DANISH report version **1.38.0** (bad/forked version string), both stale/offline.

## 4. The two fixes (both ship in 1.2.92)

### FIX A — UserAgent capture robustness (root cause)

**A1. Observability for the silent null (ship first, low risk).**
- In `TryGetForegroundAppHash()`, when `GetForegroundWindow()==0` or `processId==0`, increment a counter and log once-per-N ticks: `foreground_window_unavailable reason=no_foreground|zero_pid count=…`.
- Surface a rolling `focusCaptureUnavailableCount` (or `focusChannelUnavailable` bool) in the user-agent heartbeat → machine summary, so the console shows *which* machines and *how often*. **This is what confirms the diagnosis in production within a day.**
- Risk: none (pure logging/telemetry). Must not throw.

**A2. Bind the sampling thread to the active input desktop (the actual capture fix — highest risk, validate hardest).**
- Before foreground reads, ensure the capture/sampling thread is attached to the input desktop: `OpenInputDesktop()` + `SetThreadDesktop()` on the dedicated sampling thread, guarded, revert/clean up on failure, never throw out of the sample tick.
- Scope strictly to the sampling thread so it cannot affect the widget UI thread.
- Risk: desktop binding has side effects on window ops; **must** be on its own thread, fully guarded, and behind a kill-switch (see §6). If A1 logs show `GetForegroundWindow()==0` is NOT the cause, A2 can be held back.

**A3. Explicit locked/away focus state.**
- When the desktop is legitimately locked/secure (no foreground by design), record an explicit "locked/away" focus observation for the slice instead of nothing — so the channel still registers (as away/idle) and calibration isn't starved during lock periods. Keeps semantics correct (locked = away, not focus).

**A4. Stop the widget restart loop / decouple it from capture liveness.**
- Investigate `desktop_widget_thread_starting → window_initializing → window_shown` looping (~5s on AYMENE) and `desktop_widget_window_recovery_requested` → watchdog relaunch (842SQFP).
- Ensure a failing/looping **widget** never triggers a UserAgent process relaunch that resets the in-memory focus aggregator. Capture-loop liveness must be judged on the capture loop, not widget health.

> **UPDATE (investigated):** Two findings change the sequencing.
> 1. **Capture liveness is ALREADY decoupled from widget health.** `UserAgentHost.ResolveHeartbeatCaptureFailureCode` derives `capture_loop_stalled` / `capture_loop_erroring` from the *capture-loop tick*, not the widget. So the widget loop is **not** what bricks calibration — Fix B fixes that directly. The protective intent of A4 is largely already in the code.
> 2. **The widget loop shares A2's root cause.** `MaintainWidgetSurface` restarts whenever `IsWidgetHealthyForHeartbeat()` is false; on these machines the window is created (`window_shown`) but never becomes `IsSurfaceVisibleForHeartbeat` — the **same locked/disconnected/desktop-mismatch condition** that makes `GetForegroundWindow()` return 0. Attaching to the input desktop (A2) is expected to resolve the widget loop at the source.
>
> **Therefore A4 moves to the fast-follow build with A2/A3** (shared root cause). A standalone WPF-threading rewrite now is the single most likely way to introduce new bugs, for no calibration benefit (B already un-bricks). Residual safe hardening to do *with* A2: make the heartbeat's widget-health read strictly non-blocking so a flapping widget can never stall the heartbeat publish.

### FIX B — Core calibration resilience (safety net, helps regardless of cause)

**B1. A missing focus channel must not permanently brick calibration.**
- Allow Provisional/Calibrated to be reached on keyboard + mouse + idle quality even when focus is absent. Focus-dependent **metrics** (focus episodes) stay gated separately and simply show "no focus data" — but the machine still calibrates and surfaces pressure / away / overload instead of 0% / blank forever.

**B2. Treat focus as a "soft" channel for the calibration threshold.**
- Keep reporting focus as missing (transparency in `missingMajorChannels`), but stop the `FocusMissingPenalty` (0.24) from dragging quality below the calibration floor on machines whose other channels are healthy. (Tune: either reduce the penalty's effect on the gate, or exclude focus from the "major channel" hard requirement while keeping it in the displayed list.)

**B3. Pressure/overload degrade gracefully without focus.**
- `OverloadInferenceComputer` keys `FocusSwitchRate` on `FocusChannelAvailable`. Ensure pressure/overload compute from keyboard/mouse/idle when focus is unavailable rather than collapsing to 0%.

**Guardrail for Fix B:** must NOT change behavior for healthy machines that already have focus (owner stays Calibrated / high, identical numbers). All B changes are additive paths that only engage when focus is absent.

## 5. Why both (per your call)

- **Fix A** restores real foreground capture → focus data actually appears + machines calibrate properly.
- **Fix B** is the safety net: even if some box genuinely can't capture foreground (true headless/locked/RDP), it still calibrates and shows pressure/away instead of looking dead.
- **A1's telemetry confirms** whether the foreground-null theory is right — so even if the root cause is slightly different, we'll *see* it in production and Fix B already prevents the "blank forever" symptom.

## 6. Safety / "don't introduce new bugs"

- Every fail path stays exception-safe; nothing new may throw out of a sample tick or flush.
- **Kill-switch:** A2 (desktop binding) and A3 behind a runtime flag (privacy.json / config) defaulting **on**, so it can be disabled fleet-wide without a rebuild if it misbehaves.
- **No change to the healthy path:** owner-style input (focus present) must produce identical quality/calibration. Add a regression test asserting this.
- Reuse the existing isolated branch + build-gate discipline (build + full test run on Windows before any MSI).

## 7. Tests (added/updated before build)

- `BucketDataQualityScorer`: focus present → quality unchanged (regression); focus absent + healthy kb/mouse/idle → calibration reachable under B1/B2.
- `FocusRawAggregates`: locked/away observation (A3) registers the channel.
- `OverloadInferenceComputer`: pressure computes without focus (B3).
- UserAgent foreground helper: `GetForegroundWindow()==0` path increments counter, returns null, never throws (A1); desktop-binding helper guarded/reverts (A2) — mocked.
- Full existing agent test suite must stay green (watch for the pre-existing failing tests already on `main` — those are unrelated to this change).

## 8. Build + MSI + rollout (yes, a new MSI is required)

A native agent change ⇒ rebuild ⇒ new signed MSI ⇒ fleet auto-update. Steps:

1. **Version bump 1.2.91 → 1.2.92** in all three csproj:
   `Porta.Agent.Service`, `Porta.UserAgent`, `Porta.ControlCenter`.
   (Service `FileVersion` must equal the MSI `ProductVersion` — `AgentUpdateWorker.GetInstalledVersion` reads `ari.exe` FileVersion.)
2. **Build on Windows** (sandbox has no dotnet): `dotnet build` + `dotnet test` green, then produce `AriAgentInstaller-1.2.92.msi`, capture its **SHA256**.
3. **GitHub release** in `PORTA-Tech/porta-windows-releases`: upload the MSI + update the manifest (version, URL, SHA256 — mandatory).
4. **Staged rollout** via Render env `rolloutPercent`: start **10%**, ensure the canary includes a couple of broken machines (or temporarily target THOR/AYMENE), watch:
   - new `focusCaptureUnavailableCount` (confirms cause),
   - focus channel starts appearing, `missingMajorChannels` drops `focus`,
   - calibration moves cold → provisional → calibrated,
   - no new errors / no regression on already-healthy machines.
5. **Ramp** 10% → 50% → 100% once the canary looks right.

> Broken machines only get the fix once they receive 1.2.92, so the rollout must reach them (they auto-update). Expect calibration to take real active time to climb after the channel starts flowing.

## 9. Sequencing (as implemented)

**Build 1.2.92 (this build) — implemented:**
- **A1** foreground-unavailable detection + throttled `foreground_window_unavailable` log + `ReadForegroundUnavailableCount()` accessor (`UserSessionNativeSignals.cs`). Confirms the cause via the live `userAgentLogTail` we can already read in the console.
- **B** calibration resilience (`BaselineManager.cs`): focus-only-partial buckets now feed the keyboard/mouse/idle baselines; focus metrics skipped when focus is absent so the focus baseline is never polluted. No change to the `partial` flag, the quality scorer, or healthy machines.
- B3 needs no code: pressure is computed from typing/scroll/focus components; focus-absence only lowers *confidence* (via coverage), never hard-zeros pressure. Verified in `OverloadInferenceComputer`.

**Build 1.2.93 (fast-follow) — after A1 logs confirm `GetForegroundWindow()==0`:**
- **A2** bind sampling thread to the active input desktop (the actual capture fix) — behind a kill-switch.
- **A3** explicit locked/away focus state.
- **A4** widget restart loop (same root cause as A2) + make heartbeat widget-health read non-blocking.

Rationale: B already converts "blank/Warming-up forever" → "calibrates + shows pressure/away" *without* the risky native changes. A1 confirms the root cause from production before the riskiest edit (A2) ships. A4 rides with A2 because they share the input-desktop root cause. This is the lowest-new-bug path to the same end state you approved.
