# Bug Diagnosis & Fix Strategy — v1.2.14

> **Verified through actual code inspection. Not guesses.**
> All bugs below were confirmed by reading the live source files in the repo.
> Claude Code implements. Do not edit files here.

---

## Bug 1 — Breaks Never Fire (CRITICAL — TWO ROOT CAUSES)

### Root Cause 1A: Focus Window Scan Stops at First Non-"keep" Bucket

**File:** `src/Porta.Agent.Core/SqliteBrainStateStore.cs`
**Function:** `ReadBreakFocusWindowSnapshot()` — around line 5703

**What the code does:**
```csharp
while (reader.Read())
{
    // gap check — stops if time gap > 180s (CORRECT: genuine break in data)
    if ((segmentCursorUtcMs - timestampUtcMs) > (long)(GapResetSeconds * 1000d))
        break;

    string stateFlagsJson = ReadStringOrFallback(reader, 1, string.Empty);
    if (!TryResolveStoredBreakActiveFocusSeconds(stateFlagsJson, out double rowEffectiveFocusSeconds))
    {
        break;  // ← WRONG: stops entire scan at first non-"keep" bucket
    }

    longBreakWindowEffectiveFocusSeconds += rowEffectiveFocusSeconds;
    ...
    segmentCursorUtcMs = timestampUtcMs;
}
```

`TryResolveStoredBreakActiveFocusSeconds` returns `false` whenever the bucket's continuity state is not `"keep"` (e.g., `"pause"`, `"decay"`, `"reset"`). The `break` statement terminates the entire scan immediately. This means **only the most recent uninterrupted "keep" streak counts toward the break threshold**.

A brief idle pause (even one 5-second bucket with `"pause"` state) resets the effective focus accumulation to zero. Since `ShortBreakWindowEffectiveFocusSeconds` never reaches the 20-25 minute threshold, the policy always returns `effective_focus_below_min`. **This is why breaks have never fired.**

**The fix — one word change:**

Change:
```csharp
if (!TryResolveStoredBreakActiveFocusSeconds(stateFlagsJson, out double rowEffectiveFocusSeconds))
{
    break;
}
```

To:
```csharp
if (!TryResolveStoredBreakActiveFocusSeconds(stateFlagsJson, out double rowEffectiveFocusSeconds))
{
    segmentCursorUtcMs = timestampUtcMs;
    continue;
}
```

Non-"keep" buckets should contribute **0 seconds** to the focus window but must NOT terminate the scan. The gap check above it already handles genuine data breaks (`break` there is correct). The `segmentCursorUtcMs = timestampUtcMs` update before `continue` is required — otherwise the gap check on the next iteration will see an artificially large gap and terminate the scan incorrectly.

**Files to change:**
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — line ~5703, `break` → `segmentCursorUtcMs = timestampUtcMs; continue`

---

### Root Cause 1B: 5-Minute Break Expiry + 30-Minute Idempotency Block

**File:** `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — line ~4932

When a break is generated:
```csharp
PlannedEndTimestampUtc: finalizeTimestampUtc.AddMinutes(5),
CooldownEndTimestampUtc: finalizeTimestampUtc.AddSeconds(breakPolicy.CooldownSeconds),  // 30 minutes
IdempotencyKey: BuildBreakEventIdempotencyKey(...)  // unique per 30-min window
```

The `ReadPendingInterventions` SQL filters:
```sql
AND (i.planned_end_timestamp_utc = '' OR i.planned_end_timestamp_utc >= $now_utc)
```

So if the break was generated but not delivered within 5 minutes (UserAgent restart, polling miss, delivery failure), the intervention disappears from the pending queue. But the idempotency key in the control_events table (unique constraint) **permanently blocks re-insertion for the remaining ~25 minutes of the 30-minute cooldown window**.

This creates a silent dead zone: conditions are met, break should fire, but the system silently suppresses it because the key already exists — and the old intervention has expired.

**The fix:**

Extend `PlannedEndTimestampUtc` to match the cooldown window end instead of a fixed 5 minutes:

```csharp
PlannedEndTimestampUtc: finalizeTimestampUtc.AddSeconds(breakPolicy.CooldownSeconds),
```

This makes the intervention visible in the pending queue for the entire 30-minute window, matching the idempotency window. If it expires or gets dismissed, the cooldown naturally prevents a new one anyway.

**Files to change:**
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — line ~4932, change `AddMinutes(5)` to `AddSeconds(breakPolicy.CooldownSeconds)`

---

### Root Cause 1C: SQL Recovery Path Missing `show_break_overlay`

**File:** `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — line ~1812

The `ReadPendingInterventions` SQL that recovers in-progress overlays after a UserAgent restart:

```sql
OR (
    i.command_type IN ('show_break_toast', 'show_stretch_break_toast')
    AND i.execution_state IN ('overlay_started', 'accepted', 'overlay_recovered')
)
```

Since Fix 3 (in `CLAUDE_CODE_EXECUTION_MAP.md`) made `show_break_overlay` the canonical command type for all break interventions, this recovery path is now broken. Interventions in `overlay_started` / `accepted` / `overlay_recovered` state with `command_type = 'show_break_overlay'` are not fetched after a restart.

**The fix:**

```sql
OR (
    i.command_type IN ('show_break_overlay', 'show_break_toast', 'show_stretch_break_toast')
    AND i.execution_state IN ('overlay_started', 'accepted', 'overlay_recovered')
)
```

**Files to change:**
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — line ~1812, add `'show_break_overlay'` to the `command_type IN (...)` list

---

## Bug 2 — Focus Session Kept Counting During Cooking Break (MEDIUM)

### Diagnosis

`GetLastInputInfo` (system-wide Windows idle API, used in `Layer1AgentHost.cs`) runs independently of input capture channels — so partial capture does NOT affect idle detection. When the user walks away, idle time accumulates correctly.

**The most likely explanation:** The widget shows projected/cached state. Between the user leaving and the idle reset firing (needs 180s cumulative via `idlePauseSeconds`, or one bucket with 120s+ idle triggering `TrueBreakFlag`), the widget continued showing the pre-departure focus number. Given the 4-second widget refresh and 5-second bucket cadence, the lag before the reset appears in the widget is at most ~185 seconds — about 3 minutes. If the user left and came back quickly enough, they saw the old value.

The more problematic scenario is when idle accumulates across multiple 5-second buckets without hitting the 120s per-bucket threshold. `idlePauseSeconds = previous.IdlePauseSeconds + bucket.IdleSeconds` — if buckets show, say, 4s idle each (mouse wiggle counted as activity), the 180s reset can be delayed significantly.

### Fix

In `DerivedSessionMetricsComputer.EvaluateTaskContinuity()`, when `TrueBreakSeconds >= IdleResetSeconds` (i.e., Windows says the user has been idle for 180+ seconds), reset the focus episode immediately regardless of the per-bucket `IdleSeconds` accumulation. This ensures system-level idle overrides any capture irregularities.

**Concrete check to add:**
```csharp
// If system-level idle (from GetLastInputInfo) exceeds the reset threshold, force reset
if (bucket.TrueBreakSeconds >= IdleResetSeconds)
{
    // reset focus episode
}
```

Check `InputBucketRow` for `TrueBreakSeconds` — it already exists (populated from `GetLastInputInfo` via `IdleRawAggregates`). This is a secondary path that fires earlier than the accumulated `idlePauseSeconds` path when the user is genuinely away.

**Files to change:**
- `src/Porta.Agent.Core/Analysis/DerivedSessionMetricsComputer.cs` — `EvaluateTaskContinuity()`: add early-exit reset when `bucket.TrueBreakSeconds >= IdleResetSeconds`, before or alongside the existing `idlePauseSeconds` check

---

## Bug 3 — Claude App Covers the Widget (EASY)

### Diagnosis

`NudgeTopmostWithoutActivation()` exists in `UserDesktopWidgetWindow.cs` at line 1781 and is correct. It calls `SetWindowPos(hwnd, HwndTopMost, ..., SWP_NOACTIVATE | SWP_ShowWindow)`.

It is called in **two places only**: initial window setup (line 655) and `EnsureVisibleForRecovery()`. Neither of these runs during normal operation.

`MaintainWidgetSurface()` in `UserDesktopWidgetPresenter.cs` (line 91) runs every 2 seconds but only checks widget health and restart state — it **never re-asserts z-order**.

When an Electron/CEF app (like Claude) opens a `HWND_TOPMOST` window, Windows gives topmost priority to the last-activated topmost window. The widget never re-asserts, so it disappears behind Claude permanently.

### Fix

**Step 1 — Expose `NudgeTopmost()` as internal on `UserDesktopWidgetWindow`:**

In `src/Porta.UserAgent/Interventions/UserDesktopWidgetWindow.cs`, add:
```csharp
internal void NudgeTopmost()
{
    NudgeTopmostWithoutActivation();
}
```

**Step 2 — Call it periodically from `MaintainWidgetSurface()`:**

In `src/Porta.UserAgent/Interventions/UserDesktopWidgetPresenter.cs`, add a counter field:
```csharp
private int _topmostNudgeCounter = 0;
```

At the END of `MaintainWidgetSurface()` (after the health check block, so it runs whether or not restart is needed — but only when the widget surface is active):
```csharp
_topmostNudgeCounter++;
if (_topmostNudgeCounter >= 5)
{
    _topmostNudgeCounter = 0;
    TryDispatchToWidget(window => window.NudgeTopmost(), out _);
}
```

This nudges every 5 × 2s = every 10 seconds. The widget will re-assert topmost before any other app has time to permanently bury it.

**Files to change:**
- `src/Porta.UserAgent/Interventions/UserDesktopWidgetWindow.cs` — add `internal void NudgeTopmost()` wrapper
- `src/Porta.UserAgent/Interventions/UserDesktopWidgetPresenter.cs` — add `_topmostNudgeCounter` field, add nudge call at end of `MaintainWidgetSurface()`

---

## Bug 4 — Control Center vs Widget Discrepancy (LOW — BY DESIGN)

### Diagnosis

Not a true bug:
- Widget polls the local API every 4 seconds (`UiRefreshInterval`) and shows live projected data
- Control center refreshes every 30 seconds and shows the last analyzed snapshot

When you open the control center after 10 minutes, you see data from the last refresh, not current data.

### Fix (Optional)

In the control center's data-fetch logic, trigger a fresh fetch when the panel becomes visible (on tab open or window focus). This is a small UX improvement only — not a correctness issue.

---

## Summary: What Claude Code Should Do

| Priority | Bug | File(s) | Change |
|---|---|---|---|
| 1 | **Focus window scan stops early** | `SqliteBrainStateStore.cs` ~line 5703 | `break` → `segmentCursorUtcMs = timestampUtcMs; continue` |
| 2 | **Break expiry too short** | `SqliteBrainStateStore.cs` ~line 4932 | `AddMinutes(5)` → `AddSeconds(breakPolicy.CooldownSeconds)` |
| 3 | **SQL recovery missing show_break_overlay** | `SqliteBrainStateStore.cs` ~line 1812 | Add `'show_break_overlay'` to recovery `command_type IN (...)` |
| 4 | **Widget z-order** | `UserDesktopWidgetWindow.cs`, `UserDesktopWidgetPresenter.cs` | Expose `NudgeTopmost()`, call every 5 maintenance cycles |
| 5 | **Idle reset during cooking break** | `DerivedSessionMetricsComputer.cs` | Add `TrueBreakSeconds >= IdleResetSeconds` early-reset path |
| 6 | **Control center stale** | Control center tab/panel code | Fresh fetch on panel open (optional) |

---

## Key Verified Facts

- `break_pending_reason_code` **IS** already surfaced in the local API (`PortaApiContracts.cs` line 327) — diagnostics exist at the API level
- `TrueBreakSeconds` comes from `GetLastInputInfo` in `Layer1AgentHost.cs` — independent of capture channels
- The confidence/trust gates (`confidence_below_min`, `trust_below_suggestion_min`) are **correct behavior** and should NOT be weakened
- Fix 1 (Option B — remove context-shift reset) and Fix 3 (add `show_break_overlay` to switch) from `CLAUDE_CODE_EXECUTION_MAP.md` were correctly implemented and are confirmed present in the current codebase
- The biggest single fix is **1A** (the `break` → `continue` change) — this alone will unlock break suggestions for sessions with any pausing, which is essentially every real work session

---

## Do NOT Change

- `BreakSuggestionPolicy.cs` confidence/trust gates (Gate 1, Gate 2) — correct behavior
- `UserBreakOverlayPresenter.cs`, `UserBreakOverlayWindow.cs` — correct
- `PortaBreakSemantics.cs` — correct
- `DerivedSessionMetricsComputer.cs` Option B change — already correctly implemented
- `UserDesktopWidgetStateBuilder.cs` distraction text insertion — already correctly implemented
- `UserAgentInterventionExecution.cs` `show_break_overlay` in switch — already correctly implemented
