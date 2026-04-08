# Widget Notification Contract

This document freezes the product contract for Ari's new desktop widget before implementation starts.

It is grounded in the current repo behavior, not aspirational copy alone. The audited anchors are:

- `src/Porta.UserAgent/Interventions/UserInterventionToastRenderer.cs`
- `src/Porta.UserAgent/Interventions/UserAgentInterventionExecution.cs`
- `src/Porta.UserAgent/Interventions/UserCommunicationRouter.cs`
- `src/Porta.UserAgent/Interventions/CaptureDegradedToastGate.cs`
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs`
- `src/Porta.Agent.Core/AgentBrainPipeline.cs`
- `src/Porta.Agent.Core/Analysis/BreakSuggestionPolicy.cs`
- `src/Porta.Agent.Core/RegulationPolicyEngine.cs`
- `src/Porta.Agent.Core/PortaExecutionConstants.cs`
- `src/Porta.Agent.LocalApi/PortaApiContracts.cs`
- `src/Porta.Agent.LocalApi/UserAgentVisualSignalsAdapter.cs`

## Scope

- The new widget is owned by `Porta.UserAgent`.
- It must keep working when `Porta.ControlCenter` is closed.
- It replaces the current one-off alert style as the primary user communication surface.
- The current toast renderer remains the fallback path if the widget cannot render.
- This contract only freezes product behavior. It does not yet implement the widget.

## Canonical Behavior Glossary

- `focus episode`
  - The durable unit of user-facing focus time.
  - The widget must not treat foreground-app age as the canonical focus timer.
- `distraction episode`
  - A contiguous segment of tracked, non-idle time where the user is no longer continuing the same focus episode because they are off-context or in sustained fragmentation.
- `away segment`
  - Idle or away time.
  - It is not the same thing as distraction.
- `pressure`
  - The expressive user-facing overload score the widget and Control Center should surface.
- `overload active`
  - The conservative stabilized state that Ari uses for interventions and system actions.
- `limited`
  - Ari still has usable state, but one or more channels or trust limits reduce certainty.
  - The widget should stay useful in this state.
- `updating`
  - Ari is showing the last known usable value while fresh analyzed data catches up.
  - `updating` is not the same thing as unavailable.
- `short break`
  - The lighter break intervention with shorter recovery.
- `long break`
  - The stronger break intervention with longer recovery.

## Deprecated Terms

The following terms may still exist in runtime code or transport contracts during migration, but they are not the canonical widget product meanings:

- `focus_elapsed_seconds` as the main focus timer
- `current focus session` as the durable focus label
- `live streak`
- distraction defined as `tracked - focused`
- `stretch_break_suggestion` as the product-facing short-break name
- `break_suggestion` as the product-facing long-break name
- widget as the primary break prompt surface
- `filling in` as the normal fallback when Ari has a usable last-known value

## Legacy Screen Migration Map

| Legacy term or screen element | Current anchors | Canonical target | Migration rule |
| --- | --- | --- | --- |
| `Last focus session` sourced from live foreground-app age | Widget summary text, Control Center status copy, live-session compatibility projections | `focus episode` duration | Keep live signals for presence only. If the number is durable, it must refer to the focus episode, not the current foreground app. |
| `current focus session` copy | Control Center loading and diagnostics text | focus-episode duration or live presence label | Replace the phrase with `focus episode`, `focus timer`, or a live presence label depending on what the screen is actually showing. |
| `distraction time` copy | History summary and older history language | `distraction segments` / `distraction episodes` | Until episode-based distraction ships, treat the old aggregate as compatibility-only and avoid labeling it as the canonical product metric. |
| Break prompts inside widget prompt rail | Current widget fallback transport and older toast-style prompt surfaces | dedicated session overlay for short and long breaks | Old widget or toast prompt plumbing may remain as fallback during migration, but it is not the product-facing destination for break decisions. |

## Product Decisions

- The widget is the primary surface for ongoing status, limited or updating truth, non-break guidance, and compact recovery context.
- Most current one-off notifications become persistent widget states or activity-log entries.
- Break interventions are not widget prompts. They belong to a dedicated user-session overlay owned by `Porta.UserAgent`.
- Only non-break suggestion prompts ask a direct choice question inside the widget.
- Automatic system changes do not ask `Yes / No` after the change already happened. They show `Keep / Undo` or `Restore`.
- Runtime failures do not ask `Yes / No`. They show repair actions such as `Open Health`, `Retry`, or `Settings`.
- Inform-only engine actions never produce a standalone popup in v1.
- Recovery confirmations default to activity-log entries, not interruptions.
- Routine partial capture and freshness delay states must not replace the widget's primary useful metrics. They become `limited` or `updating` indicators unless user action is required.

## Widget Ownership And Data Sources

- Runtime owner: `Porta.UserAgent`
- Primary data sources:
  - `GET /v1/status`
  - `GET /v1/snapshot/current`
  - `GET /v1/events/recent`
  - `GET /v1/settings/effective`
  - `GET /v1/timeline` for later drill-down links, not for the base widget loop
- Quick actions already supported by the local API:
  - pause
  - resume
  - recalibrate baseline
  - update settings
  - test intervention

## Baseline Widget Menu

The compact menu must expose the same high-value control path every time.

- `Open Now`
- `Open Actions`
- `Open System Health`
- `Open Controls`
- `Pause for 15m`
- `Pause for 1h`
- `Resume` when paused
- `Recalibrate`
- `Take Suggested Action` when a prompt is active
- `Help / Settings`

Menu actions should prefer local Control Center deeplinks over browser navigation.

## Widget Summary Semantics

The compact widget summary must use stable product meanings:

- `Work today`
  - Durable processed tracked work time for the current day.
  - It is not the same thing as focused time.
- `Last focus session`
  - The duration of the current or most recent `focus episode`.
  - It is not the age of the current foreground app or window.
- `Pressure`
  - The expressive user-facing overload score.
  - It is not the same thing as `overload active`.
- `Limited`
  - The widget still has usable values, but confidence or capture coverage is reduced.
- `Updating`
  - The widget is showing the last known usable value while fresh analyzed state catches up.

## Widget State Model

The widget has five product-facing states.

| State | Meaning | Entry Conditions | Exit Conditions | Notes |
| --- | --- | --- | --- | --- |
| `collapsed` | Default resting widget with compact status, icon, and menu access | Healthy session, no active prompt, no active critical state | User expands it, a prompt appears, or a critical state appears | This is the default steady-state mode |
| `expanded` | User-opened detail view for current status and quick actions | User clicks the widget or opens the menu/details | User closes it, pointer-hides it, or a higher-priority state appears | Used for reading context, not for prompting |
| `prompting` | Non-break suggestion requiring a choice | A non-break prompt-family event becomes eligible and no critical state is active | User responds, it times out, or a critical state preempts it | Short-break and long-break decisions are not routed here |
| `critical` | Persistent failure or degraded runtime state needing attention | Service, capture, consent, auth, DB, or output failure is active | Root cause clears or user temporarily hides it | Highest semantic priority |
| `hidden-temporarily` | Presentation override used to avoid blocking clicks | Pointer enters widget hit area or user snoozes/hides it | Pointer leaves, snooze expires, or user reopens it | This is a display override, not a semantic queue state |

Break overlays are separate from widget semantic states. They are session-level surfaces owned by `Porta.UserAgent`.

## Priority Rules

- Priority order: `critical` > `prompting` > `expanded` > `collapsed`
- `hidden-temporarily` can mask any visible state, but it preserves the underlying semantic state.
- The widget must never block a click target for longer than necessary to retract to its compact handle.
- Only one prompt-family item may be visible at once.
- Critical states suppress suggestion prompts until the critical state clears.
- If Ari is paused, no suggestion prompt is shown.
- If the same degraded root cause remains active, Ari does not re-alert more than once per repeat window.

## Surface Routing Rules

There are five user-facing output classes.

| Class | Meaning | Examples |
| --- | --- | --- |
| Persistent state | Ongoing condition shown in the widget body or compact bar | monitoring, deep work active, overload active, capture degraded |
| Prompt | A non-break suggestion awaiting a user choice | focus lock-in suggestion |
| Urgent alert | A one-time escalation when entering a new critical failure while the widget is not already expanded on that issue | service lost, notification restore failure, capture handoff failure |
| Activity log item | Non-blocking record of something that happened | overload recovered, notifications restored, brightness restored |
| Session overlay | A dedicated user-session surface outside the widget for stronger interventions | short break, long break |

## Interaction Contract

### Non-Break Suggestions

- Button pattern: `Yes`, `Not now`
- Secondary links: `Turn off this tip`, `Details`
- Timeout: auto-collapse after `5 minutes` if the user does nothing
- Required future outcome states:
  - `accepted`
  - `dismissed`
  - `snoozed`
  - `suppressed_by_preference`

### Break Overlays

- Break decisions do not use the widget prompt rail.
- Button pattern:
  - `Take break`
  - `Skip`
  - `Snooze`
- Required future outcome states:
  - `accepted`
  - `skipped`
  - `snoozed`
  - `suppressed_by_preference`
- If the user accepts, Ari shows a countdown break layer on the dedicated session overlay.
- The widget may show summary state for the active break, but it is not the decision surface.

### Automatic System Changes

- Button pattern: `Keep`, `Undo` or `Restore`
- Secondary links: `Turn off this feature`, `Details`
- Examples:
  - notification suppression turned on
  - brightness reduced
- These are status confirmations with reversal, not yes/no questions

### Runtime Failures

- Button pattern: `Open Health`, `Retry` where possible
- Secondary links: `Settings`, `Dismiss for now`
- No permanent opt-out for hard failures

## Approved Dynamic Copy Tokens

Widget copy in v1 must use fields the local runtime already exposes.

Approved now:

- `{focus_minutes}`
- `{deep_work_streak}`
- `{overload_pct}`
- `{confidence_pct}`
- `{trust_tier}`
- `{missing_channels}`
- `{pause_until}`
- `{cooldown_minutes}`

Not approved for v1:

- current app name
- current window title
- future-day predictions

Reason:

- The current widget-facing Local API contract does not expose foreground app/window names, so copy like `You have been working in {AppName}` is not a valid v1 promise.

## Notification Matrix

### Ambient And Persistent Widget States

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Normal monitoring (`VisualMode=Normal`) | Persistent `collapsed` state | low | `Ari is monitoring your session.` | Menu only: `Open Now`, `Pause`, `Controls` | Continuous state, no alert loop | none | tray tooltip and tray mode only |
| Deep work active (`status.DeepWorkActive` or `snapshot.DeepWorkActive`) | Persistent state, optionally expanded on first entry | medium | `Deep work is active. {deep_work_streak} protected.` | `Open Controls`, `Restore notifications` when muted, `Details` | Expand once per deep-work session, then stay quiet | feature-level via notification reduction settings | tray deep-work mode plus Control Center deep-work state |
| Overload active (`status.OverloadActive` or overload stabilization on) | Persistent state | medium-high | `Overload is elevated. Ari is reducing interruptions.` | `Take a break`, `Open Now`, `Details` | Expand once on state transition, then persist quietly | none for the state itself | tray overload mode plus Control Center banner |
| Paused (`status.Paused`) | Persistent state | low | `Ari is paused.` or `Ari is paused until {pause_until}.` | `Resume`, `Controls` | Continuous while paused | none | tray tooltip with paused text |
| Calibration limited (`!status.BaselineReady`, `status.ColdStart`, low trust) | Persistent badge in collapsed or expanded state | low | `Ari is still calibrating, so guidance is conservative.` | `Recalibrate`, `Open Now`, `Details` | No alert; badge only | session hide allowed | Control Center NOW banner only |

### Suggestion Prompts

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `focus_lock_in_suggestion` when focus-protection suggest level is reached and no stronger state blocks it | `prompting` | medium | `Focus is building. Keep this task in front of you for a bit?` | `Yes`, `Not now`, `Turn off this tip`, `Details` | max of engine cooldown and widget floor `15m`; global prompt floor `12m`; auto-collapse `5m` | family-level permanent opt-out allowed | legacy toast once, then tray badge and Control Center action entry |

### Session Overlay Breaks

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| legacy `stretch_break_suggestion`, canonical `short break` | dedicated session overlay, not widget | medium | `You have been steadily working for {focus_minutes}m. Ready for a short break?` | `Take break`, `Skip`, `Snooze`, `Turn off this tip`, `Details` | family floor `30m`; global break floor `12m`; overlay expiry determined by final break UX | family-level permanent opt-out allowed | current legacy widget or toast path only until overlay implementation lands |
| legacy `break_suggestion`, canonical `long break` | dedicated session overlay, not widget | medium-high | `You have been working for {focus_minutes}m. Ready for a longer break?` | `Take break`, `Skip`, `Snooze`, `Turn off this tip`, `Details` | family floor `30m`; global break floor `12m`; overlay expiry determined by final break UX | family-level permanent opt-out allowed | current legacy widget or toast path only until overlay implementation lands |

### Inform-Only And Guidance States

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `inform_overload_risk` | Persistent status line only, never a prompt | low | `Overload is rising. Ari is watching and will step in only if it keeps climbing.` | `Open Now`, `Details` | no more than one visible copy change per `60s` | family-level mute allowed | activity log only |
| `inform_focus_protection` | Persistent status line only, never a prompt | low | `Focus is building. Ari is watching for interruptions.` | `Open Now`, `Details` | no more than one visible copy change per `60s` | family-level mute allowed | activity log only |
| `inform_fatigue_prevention` | Persistent status line only, never a prompt | low | `Fatigue is building. A short reset may help soon.` | `Open Now`, `Details` | no more than one visible copy change per `60s` | family-level mute allowed | activity log only |
| `focus_mode_enabled`, `focus_assist_enabled`, `fatigue_relief_enabled` | Persistent guidance state, not a yes/no prompt | medium | `Ari switched into protection guidance for this block.` | `Open Controls`, `Details` | max of engine cooldown and widget floor `15m` | feature-level opt-out via settings | activity log only |
| `temporary_app_restriction_enabled` | Persistent guidance state, high-salience chip | medium-high | `Ari is asking you to stay off distracting apps for a few minutes because overload stayed high.` | `Open Controls`, `Details` | max of engine cooldown and widget floor `15m` | feature-level opt-out via settings | activity log only |

### Automatic System Actions

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `deep_work_notifications_suppressed` | Persistent state with active-action chip | medium | `Notifications are muted while deep work stays active.` | `Restore now`, `Keep`, `Turn off this feature`, `Details` | first expansion once per deep-work session; respect engine min-on `120s` and re-enable cooldown `600s` | feature-level permanent opt-out allowed | tray tooltip + Control Center action row |
| `overload_brightness_reduced` | Persistent state with active-action chip | medium | `Screen brightness was lowered to reduce strain.` | `Restore brightness`, `Keep`, `Turn off this feature`, `Details` | first expansion once per activation; respect engine action cooldown `600s` | feature-level permanent opt-out allowed | activity log plus Control Center outputs state |

### Runtime Failures And Degraded States

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `!status.ServiceRunning` | Persistent `critical` state plus one urgent entry alert | critical | `Ari cannot regulate right now because the core service is unavailable.` | `Open Health`, `Retry`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | none | legacy urgent toast, tray unavailable mode, Control Center health banner |
| `!status.ConsentActive` | Persistent `critical` state | high | `Ari is waiting for consent before it can monitor or regulate this session.` | `Review consent`, `Dismiss for now` | once per session, then persistent only | none | tray unavailable mode and Control Center consent banner |
| `status.CaptureContext != UserSession` | Persistent warning state | high | `Ari is reconnecting to your live Windows session.` | `Open Health`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | session hide only | tray unavailable mode and Control Center health banner |
| `USER_AGENT_HEARTBEAT_STALE` | Persistent `critical` state plus one urgent entry alert | critical | `Ari is running, but the live session heartbeat stopped updating.` | `Open Health`, `Retry`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | none | legacy urgent toast, tray unavailable mode, Control Center health banner |
| `USER_AGENT_SESSION_SWITCHED`, `USER_AGENT_INSTANCE_MISMATCH`, ownership warning | Persistent `critical` state plus one urgent entry alert | critical | `Ari detected a session handoff and is reconnecting live input capture.` | `Open Health`, `Retry`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | none | legacy urgent toast, tray unavailable mode, Control Center health banner |
| `USER_AGENT_INPUT_SELF_TEST_FAILED` | Persistent warning state | high | `Ari is connected, but live keyboard or mouse input is not reaching the core yet.` | `Open Health`, `Retry`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | session hide only | tray unavailable mode and Control Center health banner |
| `USER_AGENT_KEYBOARD_SIGNAL_MISSING` | Persistent warning state | medium-high | `Ari is only seeing mouse activity right now. Keyboard capture is limited.` | `Open Health`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | session hide only | tray unavailable mode and Control Center health banner |
| `USER_AGENT_MOUSE_SIGNAL_MISSING` | Persistent warning state | medium-high | `Ari is only seeing keyboard activity right now. Mouse capture is limited.` | `Open Health`, `Dismiss for now` | repeat same root cause no more than every `20m`; reset after `1m` healthy | session hide only | tray unavailable mode and Control Center health banner |
| `snapshot.PartialData == true` with `MissingMajorChannels` | Persistent `limited` state, not takeover by default | medium-high | `Tracking is limited. Missing {missing_channels}. Ari keeps trying to restore full capture.` | `Open Health`, `Details` | repeat same root cause no more than every `20m`; reset after `1m` healthy | session mute allowed, not permanent | tray degraded mode and Control Center data-quality banner |
| `notification_snapshot_failed` | Persistent warning state, urgent only on first entry | high | `Ari could not read your current notification state. Mute banners manually if you need protected focus.` | `Open Health`, `Retry`, `Settings`, `Dismiss for now` | repeat same root cause no more than every `20m` or after explicit retry | none | legacy urgent toast plus outputs warning |
| `notification_suppress_failed` | Persistent `critical` state for the deep-work feature | high | `Ari could not mute Windows notifications for deep work.` | `Open Health`, `Retry`, `Settings`, `Dismiss for now` | repeat same root cause no more than every `20m` or after explicit retry | none | legacy urgent toast plus outputs warning |
| `notification_restore_failed` | Persistent `critical` state until acknowledged | high | `Ari could not restore Windows notification settings cleanly.` | `Open Health`, `Retry`, `Settings`, `Dismiss for now` | repeat same root cause no more than every `20m` or after explicit retry | none | legacy urgent toast plus outputs warning |
| `brightness_control_failed` | Persistent warning state, urgent only on first entry | medium-high | `Windows did not apply Ari's brightness change.` | `Open Health`, `Dismiss for now`, `Turn off this feature` | repeat same root cause no more than every `20m` or after explicit retry | feature-level opt-out allowed | legacy toast once, then outputs warning |
| `brightness_unsupported` | Warning in health and outputs, not repeated spam | medium | `This device does not allow Ari to adjust brightness automatically.` | `Dismiss`, `Turn off this feature`, `Details` | show once per session, then badge only | feature-level opt-out allowed | outputs warning only |
| `status.DbMigrationFailed`, `!status.LocalApiAuthReady`, `!status.NotificationSuppressionReady`, `!status.BrightnessActionReady` | Persistent health state | high | `Ari needs repair before some protections can run.` | `Open Health`, `Settings` | alert on transition only, then persistent | none | Control Center diagnostics banner and tray unavailable mode |

### Activity-Log-Only Transitions

| Event / Trigger | Surface | Severity | Default Copy | CTA Contract | Cooldown | Opt-Out | Fallback |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `overload_recovered` | Activity log item only | low | `Overload eased. Ari returned to normal monitoring.` | `Details` only | log only | mute allowed | no standalone alert |
| `deep_work_notifications_restored` | Activity log item only | low | `Notifications were restored after the focus block.` | `Details` only | log only | mute allowed | no standalone alert |
| `overload_brightness_restored` | Activity log item only | low | `Brightness was restored.` | `Details` only | log only | mute allowed | no standalone alert |

## Anti-Spam Budget

- Ari must not show more than one suggestion prompt in any rolling `12 minute` window.
- Suggestion-family cooldowns can be stricter than engine cooldowns, never looser.
- Inform-only actions never interrupt.
- Recovery events never interrupt.
- Same degraded root cause uses the existing `20 minute` repeat window and `1 minute` healthy reset as the baseline contract.
- If a critical state is active, all lower-severity prompts are queued or dropped.
- If multiple low-signal events arrive together, the widget summarizes them into one status line instead of stacking alerts.

## Fallback Contract

If the new widget cannot render or initialize:

- `Porta.UserAgent` falls back to the current toast renderer and tray icon path.
- Critical failures still produce one urgent toast.
- Suggestions fall back to a single toast plus tray badge, not repeated popup spam.
- Persistent state remains visible through tray mode, tooltip text, and Control Center truth.

## Implementation Implications

This contract requires product and data-model changes before the UX is complete:

- Add widget-native state handling in `Porta.UserAgent`
- Add prompt outcomes beyond `applied`, `verified`, and `failed`
- Add per-tip preferences for permanent opt-out
- Add session-level temporary mute for degraded-state warnings
- Add Control Center deeplinks for `Now`, `Actions`, `Health`, and `Controls`

## Done When

- Ari has a single, persistent desktop widget instead of noisy standalone popup spam.
- Every current action and degraded-state code has one defined surface.
- Inform-only events no longer interrupt the user.
- Suggestions, automatic actions, and failures use different CTA patterns.
- The old toast path is only a fallback, not the primary UX.
