# Claude Code Execution Map — v1.2.13

> **For Claude Code only.** Do not implement anything listed here unless it is your assigned task.
> Do not modify `DerivedSessionMetricsComputer.cs` except for the Option B change described in Fix 1.

---

## Context

Ari (PORTA Windows agent) is not surfacing breaks or distraction status in the widget. Two root causes are confirmed. This document gives you the exact files, the exact change, and the verified wiring so you can implement confidently.

---

## Fix 1 — Option B: Idle-Only Focus Reset (PREREQUISITE FOR EVERYTHING ELSE)

### File
`src/Porta.Agent.Core/Analysis/DerivedSessionMetricsComputer.cs`

### Why breaks are not firing

`BreakSuggestionPolicy.cs` gates every break suggestion on:
```csharp
bool continuityNotActive = snapshot.TaskContinuitySeconds <= 0d
    && (episodeState is "reset" or "idle");
if (continuityNotActive) return Inhibited("continuity_not_active");
```

And even when not fully blocked, it only fires when `effectiveFocusSeconds >= threshold` (20-25 min). Neither condition is ever met because `EvaluateTaskContinuity()` in `DerivedSessionMetricsComputer.cs` contains a **context-shift reset block** that resets or decays the focus episode every time the user switches apps.

The `anchorAppHash` / `contextChanged` system causes constant resets in normal computer use. The episode never accumulates 20 consecutive minutes.

### What to change

In `EvaluateTaskContinuity()`, find the block that handles `contextChanged`. It checks whether the foreground app has changed from an anchor hash, then performs one of three branches: pause, decay, or reset of the focus episode. **Remove this entire block.**

Keep only:
1. The gap reset — resets focus after > 180 seconds of gap since last input
2. The idle reset — resets focus after > 180 seconds of idle time

Do NOT remove: the accumulation logic, the anchor hash tracking variables, the output construction, or anything else.

Do NOT change any other file.

### Expected outcome
After this change:
- The focus episode accumulates continuously across app switches
- `TaskContinuitySeconds` grows to 20-25 min during a normal work session
- `BreakSuggestionPolicy` stops returning `continuity_not_active`
- Break suggestions start firing from the backend

---

## Fix 2 — Distraction Display in Widget Status Text

### Files to change
- `src/Porta.UserAgent/Interventions/UserDesktopWidgetStateBuilder.cs`

### Background

The attention segment system already computes distraction state correctly:
- `LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot)` → true when `AttentionSegmentKind` is `off_task_distraction` or `fragmentation_distraction`
- `LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(snapshot)` → human-readable string like "Rapid switching / fragmentation for 5m." or "Stayed outside focus context for 8m."

The computed `EpisodeStatusText` in `LocalCurrentExperienceStateAdapter` correctly prefers `AttentionSegmentReasonText` — but it's never surfaced into `UserDesktopWidgetState`.

The `BuildStatusText` method in `UserDesktopWidgetStateBuilder` has a fall-through path at the end that returns mode strings ("Deep work is active.", "Today is updating normally."). **Distraction text should be inserted here, before those mode strings, when the snapshot is in a distraction segment.**

### Where to insert

In `BuildStatusText`, find the block that checks `IsFreshLiveSession(liveSession)` and falls through to:
```csharp
return mode switch
{
    UserDesktopWidgetMode.DeepWork => "Deep work is active.",
    UserDesktopWidgetMode.Overload => "Overload is elevated.",
    _ => "Today is updating normally."
};
```

**Before** that `return mode switch`, add:

```csharp
if (snapshot != null && LocalAttentionSegmentAnalytics.IsDistractionEpisode(snapshot))
{
    string distractionText = LocalAttentionSegmentAnalytics.ResolveAttentionSegmentReasonText(snapshot);
    if (!string.IsNullOrWhiteSpace(distractionText))
    {
        return distractionText;
    }
}
```

You will need to add a using for `Porta.Agent.LocalApi` if it's not already present at the top of the file. Check the existing usings first — `LocalAttentionSegmentAnalytics` is in that namespace.

### Guard conditions already in place (do not add more)

By the time control reaches that insertion point, all of these are already handled:
- Paused → already returned
- Degraded toast → already returned
- Critical state → already returned
- Status null / service not running / auth not ready → already returned
- Not fresh → already returned
- Snapshot not available → already returned
- Consent not active → already returned
- CaptureContext not UserSession → already returned
- BackendConnected false → already returned
- IsFreshLiveSession → already returned (live session text takes priority)

So the distraction check only fires when everything is healthy and a live session is not present. This is the right priority.

---

## Fix 3 — Wire `show_break_overlay` Command Type (Low Risk, High Value)

### File
`src/Porta.UserAgent/Interventions/UserAgentInterventionExecution.cs`

### Problem

`IsPromptCommand()` (line ~752) already recognizes `"show_break_overlay"` as canonical:
```csharp
return normalized is
    "show_break_overlay"
    or "show_break_toast"
    or "show_stretch_break_toast"
    or "show_focus_lock_in_toast";
```

But `ExecuteOrReplay()`'s command switch (line ~101) does NOT handle it:
```csharp
LocalApiInterventionAckRequest? ack = (intervention.CommandType ?? string.Empty).Trim().ToLowerInvariant() switch
{
    "show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast"
        => BuildPromptAck(intervention, sessionId, agentInstanceId),
    ...
    _ => BuildAck(..., result: "failed", errorCode: "unsupported_command")
};
```

If the backend sends `CommandType: "show_break_overlay"`, it silently fails with `unsupported_command`.

### Fix

In the `ExecuteOrReplay` switch, change:
```csharp
"show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast"
    => BuildPromptAck(intervention, sessionId, agentInstanceId),
```
to:
```csharp
"show_break_overlay" or "show_break_toast" or "show_focus_lock_in_toast" or "show_stretch_break_toast"
    => BuildPromptAck(intervention, sessionId, agentInstanceId),
```

This is a one-line pattern match addition. `BuildPromptAck` already handles overlay routing correctly through `UserDesktopWidgetPresenter.TryPresentPrompt` → `UserBreakOverlayPresenter`.

---

## Verified: Break Overlay Pipeline is Fully Wired

Do NOT touch any of the following — they are already correct end-to-end:

```
Backend intervention
  → LocalAPI /interventions/pending (polled at bucket cadence)
  → UserAgentInterventionExecutor.ExecuteOrReplay (CommandType: show_break_toast)
  → BuildPromptAck → TryParsePromptPayload → UserInterventionPrompt(BreakKind=short_break, DeliverySurface=break_overlay)
  → UserDesktopWidgetPresenter.TryPresentPrompt
  → PortaBreakSemantics.IsBreakSuggestionAction(tipKey) == true
  → UserBreakOverlayPresenter.TryPresentPrompt
  → Full-screen WPF overlay on dedicated STA thread
  → Countdown timer (90s short / 5min long)
  → OnResponse → ack sent back to LocalAPI
```

Files NOT to touch:
- `UserBreakOverlayPresenter.cs` — correct
- `UserBreakOverlayWindow.cs` — correct
- `UserBreakOverlayStateStore.cs` — correct
- `PortaBreakSemantics.cs` — correct
- `BreakSuggestionPolicy.cs` — correct (problem is upstream, not here)
- `BaselineManager.cs` — correct (SuggestionsEnabled gating works)

---

## Already Done (Do Not Redo)

- **Step 6** (commit 834ed4b): Widget collapse timer fix — stop collapse on CC/message click
- **Step 7** (commit 4aaf03f): Work Today metric — guard against stale data during API outage
- **Service reliability** (by Claude Code): SemaphoreSlim(3,3) on DB reads, 8s command timeout, LocalApiHealthWatchdog with Environment.Exit(1) after 3 health check failures

---

## Build After These Fixes

After all three fixes are implemented and tested:
1. Run the numbered `build-<version>.ps1` at the repo root → produces the MSI. **NOT `scripts/build-msi.ps1`** — that is a hard-fail stub since 2026-08-06; it broke a machine. See `Loop_And_Report_Build_Brief.md §9`.
2. Install with `scripts/install.ps1 -Version 1.2.13` (installs to `C:\Program Files\Ari\`)
3. Verify: work a 25-min session → break overlay should appear; switch apps repeatedly → distraction text in widget status line

---

## Notes on `DerivedSessionMetricsComputer.cs` Edit

This file has been read but NOT modified (a prior accidental edit was reverted via `git checkout`). The file is clean at HEAD. When you open it, search for `contextChanged` to locate the block to remove.

The `anchorAppHash` variable itself and its assignment can stay — it may be used for other things or may become unused after removal. If unused after the change, remove it to avoid a compiler warning. Do not leave dead code if the compiler flags it.
