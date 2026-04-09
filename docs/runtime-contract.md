# Runtime Contract

This document locks the Windows runtime contract that future reliability, notification, installation, and update work must implement.

It is grounded in the current repo behavior, not assumptions. The audited code anchors are:

- `installer/Product.wxs`
- `src/Porta.ControlCenter/App.xaml.cs`
- `src/Porta.ControlCenter/CurrentSessionUserAgentLauncher.cs`
- `src/Porta.ControlCenter/MainWindow.Updates.cs`
- `src/Porta.UserAgent/UserAgentHost/UserAgentHost.cs`
- `src/Porta.UserAgent/Interventions/UserInterventionToastRenderer.cs`
- `src/Porta.Agent.Core/AgentCoreController.cs`
- `src/Porta.Agent.Common/UserAgent/CurrentSessionUserAgentRecoveryEvaluator.cs`
- `src/Porta.Agent.ValidationRunner/Fleet/FleetValidationRunner.cs`

## Canonical Behavior Glossary

These are the canonical product meanings Ari must converge on, even when current transport fields or legacy action names still exist in code during migration.

- `focus episode`
  - The durable unit of user-facing focus time.
  - A focus episode survives short app/window switches, short reading or thinking pauses, and short instability spikes.
  - It does not mean "time since the current foreground app became active."
- `distraction episode`
  - A contiguous segment of tracked, non-idle time where the user is no longer continuing the same focus episode because they are off-context or in sustained fragmentation.
  - It does not mean `tracked - focused`.
- `away segment`
  - Idle or away time.
  - It is not the same thing as distraction.
- `pressure`
  - The expressive user-facing overload score Ari shows to humans.
  - It may rise quickly and does not, by itself, mean interventions must fire.
- `overload active`
  - The conservative stabilized state Ari uses for interventions and system actions.
  - It is not the same thing as the visible pressure score.
- `short break`
  - The lighter break intervention with a short recovery duration.
  - Default local policy targets about `25m` of continuous focus episode time inside a `30m` window, or `20m` under high pressure.
  - During migration, the runtime may still carry legacy action IDs for this behavior.
- `long break`
  - The stronger break intervention with a longer recovery duration.
  - Default local policy targets about `60m` of continuous focus episode time inside a `75m` window, or `50m` under high pressure.
  - During migration, the runtime may still carry legacy action IDs for this behavior.
- `limited`
  - Ari still has usable truth, but one or more channels, trust limits, or runtime constraints reduce certainty.
  - `limited` is not the same thing as unavailable.
- `updating`
  - Ari is showing the last known usable value while fresh analyzed state catches up.
  - `updating` is not the same thing as unavailable.

## Deprecated Product Terms

The following terms may still exist in current code, database fields, or transport contracts during migration, but they are not the canonical product meanings:

- `focus_elapsed_seconds` as the main user-facing focus timer
- `current focus session` as a synonym for the durable focus timer
- `live streak`
- `sustained attention` as the primary user-facing focus label
- distraction defined as `tracked time - focused time`
- `stretch_break_suggestion` as product copy for the short-break path
- `break_suggestion` as product copy for the long-break path
- widget-as-break-prompt-surface
- `filling in` as the routine steady-state fallback when Ari has a last-known usable value

## Legacy Term, Event, And Screen Migration Map

This table exists to stop old stored events, old tests, and old UI copy from silently surviving under the wrong meaning while migration is in progress.

| Legacy term or surface | Current anchors | Canonical target | Migration rule |
| --- | --- | --- | --- |
| `current focus session` | Control Center summary copy, widget-compatible projection text, and older diagnostics strings | `focus episode` duration or a live presence label such as `active`, `idle`, `paused`, `limited`, or `updating` | If the UI is showing a durable timer, call it a focus episode. If it is showing immediate live status only, use a live-presence label and do not imply episode duration. |
| `break_suggestion` | `BreakSuggestionPolicy`, pending interventions, stored control events, validation gates, and local API cooldown aliases | Product-facing `long break` | Keep the transport action ID for compatibility until the break overlay migration lands, but product copy, docs, and future screens should treat it as the long-break path. |
| `stretch_break_suggestion` | `BreakSuggestionPolicy`, pending interventions, stored control events, validation gates, and anti-spam prompt families | Product-facing `short break` | Keep the transport action ID for compatibility until the break overlay migration lands, but product copy, docs, and future screens should treat it as the short-break path. |
| `microbreak_*` break fields | Runtime policy JSON, local API break-window snapshot fields, and Control Center diagnostics copy | short-break thresholds and short-break effective focus window | Keep the field names for compatibility during schema migration, but docs and screens should describe them as short-break data rather than microbreak data. |
| `short_break_*` legacy break fields | Runtime policy JSON, local API break-window snapshot fields, and Control Center diagnostics copy | long-break thresholds and long-break effective focus window | Keep the field names for compatibility during schema migration, but docs and screens should describe them as long-break data when that is the product meaning. |
| `breakSuggestionSecRemaining` and `break_suggestion_sec_remaining` | Local API snapshot cooldown payload | Shared legacy break-suggestion cooldown alias | Keep this alias for compatibility until the API contract grows separate short-break and long-break overlay timing fields. |
| `distraction time` / `tracked - focused` | Control Center history copy, history summaries, and compatibility metrics | Episode-based distraction | Treat the old math as deprecated compatibility output only. New UI copy must talk about distraction segments or distraction episodes, not a leftover-time metric. |
| Break prompts inside the widget | Widget contract, toast fallback, and prompt-family transport | Dedicated user-session break overlay owned by `Porta.UserAgent` | Break events may still travel through old prompt transport during migration, but the product target is a separate overlay, not the widget body. |

## Runtime Roles

- `AriAgent` service
  - Machine-level runtime, local API host, deterministic engine, persistence, and regulation coordination.
- `Porta.UserAgent`
  - Interactive user-session process for tray state, direct user notifications, session heartbeat, and session-bound input capture.
- `Porta.ControlCenter`
  - WPF control surface for local truth, account/update UX, and diagnostics.

## Install And Access Contract

Current audited facts:

- The MSI installs `ari.exe`, `Porta.UserAgent.exe`, and `Porta.ControlCenter.exe`.
- The MSI creates an `HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Run` entry named `PORTA User Agent` that launches `Porta.UserAgent.exe --startup-source=run-key`.
- The MSI creates Start Menu and Desktop shortcuts for Control Center.
- First install launches Control Center once after install when `NOT Installed` and `UILevel >= 5`.

Locked contract:

- Users must not have to manually launch `Porta.UserAgent` after install or after sign-in.
- Users must be able to reopen PORTA from Start Menu or Desktop without knowing process names.
- Installation and first-run copy must state that Ari runs in the background through the service plus `Porta.UserAgent`, and that Control Center is only the UI surface.

## Control Center Closed

Current audited facts:

- Control Center is a normal WPF window and closes normally.
- Closing Control Center stops the UI process only.
- The service and `Porta.UserAgent` are separate binaries and are installed separately.

Locked contract:

- The user must not need the Control Center window open for Ari to function.
- If `AriAgent` and `Porta.UserAgent` are healthy, Ari continues capturing, evaluating, and regulating when Control Center is closed.
- Control Center is allowed to render truth and issue commands, but steady-state health must not depend on the UI being open.

## Service Down

Current audited facts:

- `UserAgentVisualSignalsAdapter` already maps missing service/snapshot truth to unavailable states.
- `UserAgentHost` already changes tray state to `Unavailable` when the local API is not configured or reachable.

Locked contract:

- If the service is down or the local API is unreachable, Ari must present an unavailable/degraded state and must not present stale healthy state as current truth.
- Direct user communication may explain that core regulation is unavailable, but must not imply that live regulation is still active.

## Porta.UserAgent Down

Current audited facts:

- `Porta.UserAgent` is the only audited interactive-session heartbeat publisher.
- `AgentCoreController` rejects input packets when the heartbeat is missing, stale, or mismatched.
- Control Center currently runs a 5-second `EnsureHealthy` watchdog while it is open.

Locked contract:

- If `Porta.UserAgent` dies, live capture and user-session regulation are degraded or unavailable.
- That state must auto-recover without requiring the user to keep Control Center open.
- The runtime must never silently treat a dead or mismatched `Porta.UserAgent` as healthy.

## Session Switch

Current audited facts:

- Control Center currently reacts to `SessionLogon`, `SessionUnlock`, `ConsoleConnect`, and `RemoteConnect`.
- The recovery evaluator already reasons about same-session process ownership, launch grace, stale heartbeat, and session mismatch.

Locked contract:

- After logon, unlock, console connect, or remote connect, there must be exactly one trusted `Porta.UserAgent` owner for the active session.
- A previous session owner must not remain trusted after the active session changes.
- Session convergence must not depend on manual refresh in Control Center.

## Stale Heartbeat And Input Trust

Current audited facts:

- Heartbeat freshness window: `30s`.
- Launch grace window: `12s`.
- Control Center ensure throttle: `5s`.
- Fleet validation runner expects degraded truth within `60s` after stopping `Porta.UserAgent` and recovery within `45s` after restart.

Locked contract:

- A heartbeat older than `30s` is stale and live input from that owner is not trusted.
- Stale heartbeat, agent mismatch, and session mismatch must all produce degraded capture truth, not silent no-op behavior.
- Automatic recovery should begin within the existing `5s` watchdog budget and should converge within the existing `45s` recovery budget used by validation.

## Update Available

Current audited facts:

- Control Center checks the latest Windows release on a `30-minute` interval.
- Availability today is determined by `manifest.version > local_version` and presence of `msiUrl`.
- The current UX is an account banner plus update overlay.

Locked contract:

- Update availability is product-wide state, not only an account-page detail.
- When an update is available, Control Center must show a visible global indicator and a clear entry point to the update surface.
- The existence of an update must not require the user to discover the Account screen first.

## Update Required

Current audited facts:

- A release is treated as required when `release.Required == true` or `current_version < minimumSupportedVersion`.
- Current install flow downloads the MSI and launches `msiexec /i` elevated.

Locked contract:

- Required updates must be visibly distinguished from optional updates.
- Update v1 remains MSI/WiX-based.
- The update experience must be treated as upgrade flow, not first-install flow, even though the transport remains MSI.

## Notification Categories

Current audited facts:

- The current direct user channel is the `Porta.UserAgent` tray/toast path.
- The current pending-intervention executor directly supports break, focus lock-in, stretch break, and notification suppression/restore.
- Recovery toast logic already exists for recent overload recovery events.

Locked contract for notification v1:

- Direct notifications must continue to use the existing `Porta.UserAgent` tray/toast path.
- Voice is out of scope for v1 and remains opt-in for later work.
- Ari must communicate directly to the user, without requiring Control Center, for these categories:
  - suggestions made
  - actions taken
  - state transitions that matter to the user
  - no-action guidance when Ari chooses not to act
  - runtime degradation that limits capture or regulation
  - update availability and update-required state, where appropriate

## Break Intervention Surface

Locked contract:

- The widget is not the canonical surface for break decisions.
- Short-break and long-break decisions belong to a dedicated user-session surface owned by `Porta.UserAgent`.
- Control Center may render state and history for break behavior, but break acceptance or skip decisions must not depend on Control Center being open.
- Legacy widget or toast prompt plumbing for break actions is transitional only and must not be treated as the long-term product contract.

## Non-Goals For This Contract

- This contract does not migrate updates to MSIX/App Installer.
- This contract does not make Control Center the tray runtime.
- This contract does not introduce voice as a default channel.

## Done When

- Any engineer can answer whether Ari should still work when Control Center is closed without ambiguity.
- Any engineer can classify service-down, user-agent-down, stale-heartbeat, and session-switch behavior against one written contract.
- Future work on notifications, install UX, and updates can implement against this document instead of informal expectations.
