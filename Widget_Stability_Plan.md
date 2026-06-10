# Widget Stability — Fix Plan
**All edits in one file: `src/Porta.UserAgent/Interventions/UserDesktopWidgetWindow.cs` (+1 tiny constant). Written 2026-06-10, after a full read of the window (2,379 lines) and presenter (920 lines).**

Guiding rule: **smallest possible change per bug, no redesign.** Every fix below states what could regress and how the change prevents it. Things deliberately NOT touched are listed at the end — that list is as important as the fixes.

---

## Fix 1 — The master bug: rogue `SetWindowPos` resizes the window with wrong units
**Bug it fixes:** smaller widget after sleep/wake or boot, menu logo clipped away, random mid-session shrink to a thin horizontal strip showing only the state line.

**Cause (confirmed in code):** `NudgeTopmostWithoutActivation()` (~line 2204) passes WPF logical units (`Left/Top/ActualWidth/ActualHeight`) to the physical-pixel API `SetWindowPos` **without** `SWP_NOMOVE | SWP_NOSIZE`. On a 125–150% scaled display this physically shrinks the window 20–33% every time it runs. It runs from `EnsureVisibleForRecovery()`, which the presenter's 2-second health loop triggers after wake, unlock, and transient render hiccups. The safe sibling `NudgeTopmost()` ten lines above already has a comment warning about exactly this and uses the safe flags.

**Change:** in `NudgeTopmostWithoutActivation`, pass `0,0,0,0` with flags `SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow`. (Keep `SWP_SHOWWINDOW`: recovery legitimately wants "visible without activation"; position/size stay owned by WPF.)

**Why no regression:** the method's only job is "make sure it's on top and visible". Position is still managed by `ApplyAnchorPosition` (logical units), size by the WPF animations. Nothing else reads this method's size/move behavior — it was never correct.

---

## Fix 2 — Dragging must win against the anchor system
**Bug it fixes:** widget can't be moved while a message is showing (and occasional snap-back during normal drags).

**Cause:** every `SizeChanged` calls `ApplyAnchorPosition()`, which force-resets `Left/Top` to the stored anchor. Size animations fire `SizeChanged` dozens of times over 160 ms, and messages always start animations (expand + re-measure on every state push). `DragMove()` pumps the dispatcher, so those events run mid-drag and yank the window back.

**Change (two guards, three lines):**
1. First line of `ApplyAnchorPosition()`: `if (_dragInProgress) return;` — the flag already exists and is correctly set/cleared in `BeginDragCore()`'s try/finally.
2. In the `_reexpandTimer` tick (line ~162) add `_dragInProgress` to the early-return — prevents a queued expand from firing mid-drag of the collapsed pill (same dispatcher-pumping mechanism).

**Why no regression:** when the drag ends, `SaveCurrentPlacement()` already recomputes and stores the anchor from the final position and calls `ApplyAnchorPosition()` itself — so the anchor system resumes ownership the moment the drag finishes. The guard only silences it *during* the drag, which is exactly when it must not act.

---

## Fix 3 — Messages stop holding the widget hostage
**Bug it fixes:** hover doesn't collapse the widget while a message is visible.

**Cause (intentional code, wrong policy):** `ShouldAutoCollapseSummary()` returns false when `_attentionMessage`, `_stateTakeoverMessage`, an active prompt, a critical state, or a visible message card exists.

**Change — block collapse ONLY for surfaces that need a user response:**
- Keep blocking: `_activePrompt != null` (timed prompts with answer buttons) and `_currentState?.CriticalState != null` (action-required cards).
- Stop blocking: `_attentionMessage` (informational toasts like "Paused for 15 minutes") and `_stateTakeoverMessage`. Remove those two conditions and the `HasBlockingSurfaceVisible()` clause (delete the now-unused helper).

**Why no regression:** `Expand()` already calls `RefreshInlineMessage()` (line 1433), so if the user collapses mid-message and it re-expands, the message is still there — message state lives in fields, not in the visual tree. The attention/takeover timers keep running independently, so expiry behaves identically. Prompts keep today's exact behavior.

---

## Fix 4 — One sane minimum size; no more 22px startup strip
**Bug it fixes:** freshly (re)started widget renders as a thin 200×22 strip with empty content until the first state arrives (common right after boot/wake because the watchdog relaunches the UserAgent).

**Cause:** constructor sets `Height = BaseHeight (22)`; `Loaded` and recovery call `UpdateWindowHeight()` while `_currentState == null`, measuring an empty layout → animates to ~22.

**Change:**
1. New constant `DefaultSummaryHeight = 64d` (the settled height of the normal summary surface).
2. Constructor: `Height = DefaultSummaryHeight` instead of `BaseHeight`.
3. In `UpdateWindowHeight()` (expanded branch): if `_currentState == null`, target `DefaultSummaryHeight` instead of the measured-empty value. First real `ApplyState` re-measures and corrects within one state push — same as today, but the intermediate look is a correctly-sized widget instead of a strip.

**Why no regression:** `BaseHeight` remains the floor inside the `Math.Max` for measured content; collapsed sizing (`CollapsedWidth/Height = 24`) is untouched, so the pill geometry can't change. The only visible difference is the empty-state placeholder height.

---

## Fix 5 — Menu closes on a click anywhere
**Bug it fixes:** the menu only closes by re-clicking the menu logo.

**Cause:** the popup uses `StaysOpen = false`, but the widget is a `WS_EX_NOACTIVATE` window that never takes focus — WPF's capture-based light-dismiss doesn't see clicks that land in *other applications*, so outside clicks never dismiss it.

**Change — a poll that exists only while the menu is open:**
- New `DispatcherTimer _menuDismissTimer` (interval ~100 ms), started in `_menuPopup.Opened`, stopped in `_menuPopup.Closed`.
- Each tick: detect a *new* left/right button press via `GetAsyncKeyState` transition; if the cursor (physical px, `FormsCursor.Position` — already used in this file) is outside BOTH the popup chrome bounds and the widget window bounds → `_menuPopup.IsOpen = false`.
- Popup bounds via `popupChrome.PointToScreen(0,0)` + `ActualWidth/Height` converted with the existing DPI transform helpers; wrapped in try/catch (if bounds can't be computed, do nothing — never close blind).

**Why this approach and not alternatives:** extending the global `MouseHook` to report clicks would touch the input-capture path we just stabilized fleet-wide — unacceptable risk for a UI nicety. A 100 ms poll running only while a menu is open costs nothing and touches nothing else.

**Interaction safety:** clicking the menu button while open → poll closes it → `MouseLeftButtonUp` fires `ToggleMenu` → the existing 300 ms `_menuClosedAt` reopen-guard prevents reopen (same as today). Clicking a menu item → cursor is inside popup bounds → poll does nothing; the item's own Click closes the popup (unchanged).

---

## Explicitly NOT touched (the no-new-bugs contract)
- Presenter threading/lifecycle (`EnsureWidgetThreadStarted`, dispatcher recovery, health states) — recently stabilized, fix 1 makes its recovery path harmless.
- Collapse/expand animation timings, pill geometry, drag threshold, placement store format.
- Prompt rendering, break overlay, toast fallback renderer, theme watcher.
- Multi-monitor placement (`SystemParameters.WorkArea` is primary-display-only — a real but separate limitation; noting it for the backlog, not fixing it here).
- The dead code at line ~1888 (`ResolveWidgetStatusText`) — harmless, not worth diff noise now.

## Verification checklist (run on a 125% or 150% display — mandatory, that's where the master bug lives)
1. **Wake/unlock:** sleep → resume, lock → unlock, 5×: widget size pixel-identical, menu logo visible, no strip. (This was impossible before fix 1.)
2. **Recovery path:** wait for a `desktop_widget_window_recovery_requested` log line (or force by locking long enough) → size unchanged.
3. **Strip regression:** kill Porta.UserAgent (watchdog relaunches it) → fresh widget appears at full default height, never 22px.
4. **Drag during message:** trigger a toast (pause from menu → "Paused for 15 minutes") → drag while visible: moves freely, stays where dropped; hover → collapses; re-expand → message still shown until its timer ends.
5. **Pill shape:** hover-collapse 20× in a row → always a 24×24 circle, never a capsule.
6. **Menu dismissal:** open menu → click desktop / another app / widget body → closes; menu items all still work; double-click on menu logo doesn't flicker-reopen.
7. **No-regression sweep:** pause 15m/1h + resume from menu, hide widget → show from Control Center, light/dark theme switch, a real prompt with buttons (must still block collapse), break overlay, position persists across UserAgent restart.

## Ship vehicle
These changes ride in **1.2.83** together with the auto-update groundwork — one fleet-wide manual install, after which the widget fixes are also the first thing users *feel* from that release.
