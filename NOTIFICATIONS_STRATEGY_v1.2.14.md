# Notification System Strategy — v1.2.14

> **Verified through actual code inspection. Not guesses.**
> All findings confirmed by reading live source files in the repo.
> Claude Code implements. Do not edit files here.

---

## Overview

Three issues are addressed here:

1. **Deep work thresholds are too strict** — users never enter deep work state, so all deep-work-gated notifications (focus guidance, notification suppression) never fire
2. **Startup false positive** — a `"deep work notifications suppressed"` toast fires every time Ari starts, caused by a missing `PlannedEndTimestampUtc` on the intervention
3. **Confirmation that `focus_assist_enabled` is correctly wired** — no code change needed

---

## Issue 1 — Deep Work Thresholds Too Strict (MEDIUM)

### Root Cause

**File:** `src/Porta.Agent.Core/PortaExecutionConstants.cs`
**Class:** `DeepWorkDetection` — lines 152–163

```csharp
public const int SustainedAttentionSeconds = 900;   // 15 minutes
public const double ConfidenceMin = 0.65;
public const double FragmentationMax = 0.50;
public const double VolatilityMax = 0.50;
public const double IntensityMin = 0.40;
public const double IntensityMax = 0.75;
public const int DwellOnSeconds = 120;              // 2 minutes of qualifying buckets
public const int DwellOffSeconds = 60;
public const int GapAllowSeconds = 10;
```

`DeepWorkStabilizationComputer.ComputeForFinalizedBucket()` (in `Analysis/DeepWorkStabilizationComputer.cs`) requires ALL of these conditions to be simultaneously true before it accumulates the dwell timer:

- `confidence >= ConfidenceMin` (0.65)
- `fComponent <= FragmentationMax` (0.50) — low fragmentation
- `vComponent <= VolatilityMax` (0.50) — low volatility (erratic behavior between buckets)
- `iComponent >= IntensityMin && iComponent <= IntensityMax` (0.40–0.75) — sustained focused effort
- `sustainedAttentionSec >= SustainedAttentionSeconds` (900s = 15 minutes of continuous session)

Only after ALL of those pass on a given bucket does `onDwellMs` accumulate. Then it needs to accumulate for `DwellOnSeconds * 1000ms` = 120,000ms = **24 qualifying 5-second buckets in a row** before the `"activated"` transition fires.

In practice, real work sessions almost always have brief pauses, brief volatility spikes (switching a document, clicking around, copying text) that exceed 0.50. A single 5-second bucket above any threshold resets `onDwellMs` to zero and the 2-minute dwell window restarts from zero.

The 900-second `SustainedAttentionSeconds` requirement adds another barrier: the derived session metric `SustainedAttentionDurationSec` must already be ≥ 15 minutes before the detector even starts watching. This means even if the user is working steadily, deep work cannot trigger until they've been in-session for 15 uninterrupted minutes at calibration-confidence.

### What the constants mean in plain English

**`SustainedAttentionSeconds = 900`**: Requires the session to have already accumulated 900 seconds of sustained attention before deep work detection even starts accumulating dwell. This is the biggest single barrier.

**`ConfidenceMin = 0.65`**: Requires 65% confidence in the quality of the input signal. This can be difficult early in a session or during any brief idle period.

**`VolatilityMax = 0.50`**: Volatility measures erratic, unpredictable behavior between successive 5-second buckets — frequent context switching, rapidly changing activity patterns. A value above 0.50 blocks deep work detection. Brief task-switching (checking a reference document, responding to one message) can push this above 0.50.

**`DwellOnSeconds = 120`**: After ALL other conditions pass, the system requires them to remain passing continuously for 120 seconds (24 consecutive 5-second buckets). Any single failing bucket resets the timer entirely. This is the final gate.

### The fix — three constant changes

**File:** `src/Porta.Agent.Core/PortaExecutionConstants.cs` — `DeepWorkDetection` class

```csharp
// Change this:
public const int SustainedAttentionSeconds = 900;
// To this:
public const int SustainedAttentionSeconds = 600;

// Change this:
public const double ConfidenceMin = 0.65;
// To this:
public const double ConfidenceMin = 0.55;

// Change this:
public const int DwellOnSeconds = 120;
// To this:
public const int DwellOnSeconds = 60;
```

All other constants remain unchanged:
- `FragmentationMax = 0.50` — keep
- `VolatilityMax = 0.50` — keep
- `IntensityMin = 0.40` — keep
- `IntensityMax = 0.75` — keep
- `DwellOffSeconds = 60` — keep
- `GapAllowSeconds = 10` — keep

**What this achieves:**
- 600s sustained attention = 10 min in-session before deep work kicks in (was 15 min)
- 0.55 confidence minimum = more tolerant of early/imperfect signal quality
- 60s dwell = only 12 consecutive qualifying 5-second buckets required (was 24 = 120s)

These remain conservative thresholds. The system won't fire on casual browsing or distracted work — it still requires 10+ minutes of sustained session, decent confidence, non-fragmented, non-volatile, steady-intensity activity, held for 60 seconds straight.

---

## Issue 2 — Startup False Positive Toast (MEDIUM — VISIBLE BUG)

### Root Cause

**File:** `src/Porta.Agent.Core/SqliteBrainStateStore.cs`
**Function:** `TryInsertDeepWorkNotificationEvent()` — around line 5110

The `AgentControlEvent` for `"deep_work_notifications_suppressed"` is constructed with:

```csharp
PlannedEndTimestampUtc: null,
```

The SQL filter in `ReadPendingInterventions` (which the UserAgent polls every few seconds) is:

```sql
AND (i.planned_end_timestamp_utc = '' OR i.planned_end_timestamp_utc >= $now_utc)
```

With `PlannedEndTimestampUtc = null` (stored as empty string), the condition `planned_end_timestamp_utc = ''` is always true. The intervention **never expires** from the pending queue.

**Consequence:** When deep work is detected, a `"suppress_notifications"` intervention is inserted with `LifecycleState = "activated"` (so `queueDelivery = true`) and no expiry. If Ari is restarted before UserAgent polls and delivers it, the intervention survives the restart. On next startup, UserAgent finds it in the pending queue and delivers it — suppressing OS notifications and showing the toast "Ari detected a sustained focus block and muted Windows notification banners to reduce interruptions" — even though the user may be starting a fresh session hours later with no deep work in progress.

This is why the toast fires on startup and ONLY on startup: it's always a stale intervention from a prior session being delivered on the first poll cycle after restart.

### The fix

In `TryInsertDeepWorkNotificationEvent`, change `PlannedEndTimestampUtc: null` to a short expiry window:

```csharp
// Change this:
PlannedEndTimestampUtc: null,

// To this:
PlannedEndTimestampUtc: eventTimestampUtc.AddMinutes(10),
```

This gives the UserAgent a 10-minute window to pick up and deliver the intervention. If Ari is restarted more than 10 minutes after deep work was detected, the intervention has expired and will not fire.

**File to change:**
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs` — `TryInsertDeepWorkNotificationEvent()` ~line 5117, change `PlannedEndTimestampUtc: null` to `PlannedEndTimestampUtc: eventTimestampUtc.AddMinutes(10)`

**Note:** Apply the same fix to the `"deep_work_notifications_restored"` case in the same function — it is also `PlannedEndTimestampUtc: null` and has the same stale-delivery risk.

---

## Issue 3 — Confirm: `focus_assist_enabled` Is Already Wired (NO CHANGE NEEDED)

### Verification

The full delivery chain for `focus_assist_enabled` is already implemented and correct:

1. **Action mapping** (`SqliteBrainStateStore.cs` ~line 6611):
   ```csharp
   "focus_assist_enabled" => "show_status_toast"
   ```

2. **Title** (~line 6675):
   ```csharp
   "focus_assist_enabled" => "Focus guidance"
   ```

3. **Message** (~line 6708):
   ```csharp
   "focus_assist_enabled"
       => "Ari saw steady focus long enough to turn on focus guidance for this block. ..."
   ```

4. **UserAgent command handler** (`UserAgentInterventionExecution.cs` ~line 107):
   ```csharp
   "show_status_toast" => BuildToastAck(intervention, sessionId, agentInstanceId)
   ```

5. **`BuildToastAck`** calls `_presenter.TryShow(title, message)` → shows in widget.

No code changes needed here. `focus_assist_enabled` will fire correctly once deep work detection thresholds are relaxed (Issue 1) and the user enters a deep work state.

---

## Summary: What Claude Code Should Do

| Priority | Issue | File | Change |
|---|---|---|---|
| 1 | **Startup false positive toast** | `SqliteBrainStateStore.cs` ~line 5117 | `PlannedEndTimestampUtc: null` → `PlannedEndTimestampUtc: eventTimestampUtc.AddMinutes(10)` (both suppressed and restored cases) |
| 2 | **SustainedAttentionSeconds too high** | `PortaExecutionConstants.cs` ~line 154 | `900` → `600` |
| 3 | **ConfidenceMin too high** | `PortaExecutionConstants.cs` ~line 155 | `0.65` → `0.55` |
| 4 | **DwellOnSeconds too long** | `PortaExecutionConstants.cs` ~line 160 | `120` → `60` |

---

## Do NOT Change

- `FragmentationMax`, `VolatilityMax`, `IntensityMin`, `IntensityMax`, `DwellOffSeconds`, `GapAllowSeconds` — all correct
- `UserAgentInterventionExecution.cs` — correctly handles `show_status_toast`, `suppress_notifications`, `restore_notifications`
- `DeepWorkStabilizationComputer.cs` — algorithm logic is correct, only constants need adjustment
- `DeepWorkOutputActionCoordinator.cs` / `QueuedDeepWorkOutputActionCoordinator.cs` — correct
- The anti-spam gate for toasts — correct behavior, do not weaken
- Break suggestion logic — already fixed in v1.2.14, do not touch

---

## Key Verified Facts

- At runtime, `QueuedDeepWorkOutputActionCoordinator` is the registered implementation (`AgentService.cs` line 56) — it queues state as `ActionState = "apply_pending"`, then UserAgent does the actual OS suppression via `BuildNotificationSuppressAck`
- `focus_assist_enabled` and `deep_work_notifications_suppressed` both gate on `gating.SystemActionsEnabled` which requires `CalibrationStage = "calibrated"` — in the UI this is the "Ready" state; this gate is correct behavior
- `TrueBreakSeconds` (from `GetLastInputInfo`) already feeds into `InputBucketRow` and is used by idle detection — no changes needed there for this document
- `NotificationReductionEnabled: true` is the default (`AgentCoreController.cs` `CreateDefaultSettingsState()` ~line 2519) — notification suppression is enabled by default for all users
