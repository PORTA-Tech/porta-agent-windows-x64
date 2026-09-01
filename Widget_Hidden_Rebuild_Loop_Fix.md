# Widget rebuild loop when hidden — fix brief

**One hour of work. Do not widen it.**

## The bug, from source

`UserDesktopWidgetPresenter.cs`, the recovery path:

- line ~627 `if (currentWindow is { IsLoaded: true })` — the reuse branch
- line ~684 `if (currentWindow is not null && !currentWindow.IsLoaded)` → `CloseReplacedWindow(currentWindow, "never_loaded")`

`UserDesktopWidgetWindow.StartHidden()` (line ~1341) calls `EnsureHandle()` and deliberately never
calls `Show()`, so `IsLoaded` stays false forever. The window therefore never enters the reuse
branch and is destroyed as a `never_loaded` leftover on every maintenance tick, then rebuilt, then
destroyed. `WidgetMaintenanceInterval` is 2 seconds.

The comment on line 683 says the quiet part out loud: "a constructed-but-never-loaded leftover
(its Show() threw last time)". That test was written for a window whose `Show()` failed. It also
matches every deliberately hidden window, and nothing distinguishes the two.

Reaching the recovery path at all is a second condition: `RefreshSurfaceVisibilityHealth()`
(line ~4749) sets the heartbeat visibility flag to 0 whenever `_hiddenByUser` is true, and
`IsWidgetOperationalLocked()` requires that flag true. So a hidden widget is never "operational"
and the maintenance loop always falls through to recovery. **Leave that alone. See scope below.**

## The fix

Distinguish "hidden on purpose" from "never came up". Add a flag on the window, set it in
`StartHidden()`, clear it the moment the window is genuinely shown.

1. `UserDesktopWidgetWindow`: `internal bool StartedHidden { get; private set; }`.
   Set true in `StartHidden()`. Set false in `EnsureVisibleForRecovery()` immediately after the
   `Show()` call, so a window that has actually come up goes back to being judged by `IsLoaded`
   and a genuinely failed `Show()` later is still closeable as broken.

2. `UserDesktopWidgetPresenter`, reuse gate (~627):
   `if (currentWindow is { IsLoaded: true } or { StartedHidden: true })`

3. `UserDesktopWidgetPresenter`, never-loaded close (~684):
   `if (currentWindow is not null && !currentWindow.IsLoaded && !currentWindow.StartedHidden)`

Nothing else. The reuse branch already handles both cases correctly and I checked both:

- still hidden → `HideWidget()` returns early (already hidden), `ready` is true because
  `_visibilityStore.IsHidden()` is true, returns. No rebuild.
- hide expired → `ShowWidgetIfHidden()` → `EnsureVisibleForRecovery()` → `Show()`. This works on a
  start-hidden window: `_windowLifecycleState` is `WindowLifecycleOpen` from its field initialiser
  (line 163), so `CanAcceptUiActions` is true from construction and neither guard bails out.

## Scope — do not touch these

**Do not change `IsWidgetOperationalLocked` or `RefreshSurfaceVisibilityHealth`.** Making a hidden
widget count as "operational" is the tempting deeper fix and it is out of scope here, because
`IsWidgetHealthyForHeartbeat` feeds the heartbeat the service watchdog restarts the user agent on.
Changing what "healthy" means changes restart behaviour on every machine in the fleet. Once reuse
works, the cost of leaving it is one wasted reuse check every 2 seconds, which is nothing.

**Do not reintroduce the startup flicker.** `StartHidden()` exists because showing then hiding
caused a one-frame flash (commit 96cab4c). Do not "fix" this by calling `Show()` off-screen and
hiding. If the flag approach somehow fails, stop and tell me rather than switching approach.

**Do not touch `Porta.Agent.Core` or anything from the 1.2.180 task work.**

## Test

New presenter-level test. The existing `GuiHandleLeakStressTests` proves each individual window
cleans up; it never involves the presenter, which is why the unbounded creation was invisible.

- visibility store reports hidden, drive N maintenance cycles (N ≥ 20): assert exactly **one**
  window is constructed, not N, and that no `never_loaded` close occurs
- if the harness can read `GuiResourceMeter`, assert the handle count is flat across those cycles
- hide expires mid-run: the widget becomes visible without a rebuild
- hide while already showing (the existing path) behaves exactly as today
- a window whose `Show()` genuinely fails is still closed as broken, i.e. the flag has been
  cleared and does not mask a real failure

The hide is offered as 1h, 2h and a day, so cover: restart while hidden, expiry while running, and
a hide that outlives a restart.

## Prove it

Build the solution, run all five test projects, report the counts. The four restore-after-restart
failures stay as they are.

Then on this machine: hide the widget for 1h, restart the user agent, and show me the handle count
holding flat across several minutes instead of climbing ~63 a minute. That is the whole proof.

## What is NOT claimed

This is a resource leak and a broken widget while hidden. There is no evidence it caused data
loss; that claim was withdrawn. Do not reopen the damage question, do not touch telemetry, and do
not build guards, counters or coverage metrics in this build.
