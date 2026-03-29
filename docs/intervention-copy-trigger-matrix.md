# Intervention Copy And Trigger Matrix

This document freezes the default runtime meaning of every user-facing intervention message emitted from `Porta.Agent.Core`.

Scope:
- Payload copy source: `src/Porta.Agent.Core/SqliteBrainStateStore.cs`
- Generic regulation thresholds: `src/Porta.Agent.Core/RegulationPolicyEngine.cs`
- Break suggestion triggers: `src/Porta.Agent.Core/Analysis/BreakSuggestionPolicy.cs`
- Overload stabilization defaults: `src/Porta.Agent.Core/PortaExecutionConstants.cs`
- Deep-work transitions: `src/Porta.Agent.Core/Analysis/DeepWorkStabilizationComputer.cs` and `src/Porta.Agent.Core/Outputs/DeepWorkOutputActionCoordinator.cs`
- Brightness transitions: `src/Porta.Agent.Core/Outputs/OverloadBrightnessActionCoordinator.cs`

The values below are the local default rules. A runtime policy envelope can override some thresholds, but this is the behavior shipped when no override is present.

## Shared Defaults

- Overload stabilization turns on at `0.72` after `60s` dwell and turns off below `0.58` after `45s` dwell. Minimum confidence is `0.55`.
- Overload regulation policy defaults: soft `0.65`, hard `0.80`, activation `15s`, hard activation `20s`, recovery `60s`, confidence gate `0.60`, hard confidence gate `0.70`, cooldown `600s`.
- Focus regulation policy defaults: soft `0.75`, hard `0.88`, activation `60s`, recovery `60s`, confidence gate `0.60`, cooldown `600s`.
- Fatigue regulation policy defaults: soft `0.70`, hard `0.82`, activation `120s`, recovery `180s`, confidence gate `0.60`, cooldown `900s`.
- Break policy defaults: suggestions enabled, confidence at least `0.50`, trust tier must allow suggestions, continuity must be active, delivery boundary must be reached, cooldown `1800s`.
- High overload pressure for the break policy means any of:
  - the focus window already flagged high overload pressure
  - overload stabilization is active
  - overload likelihood is at least `0.72`
- Short break threshold:
  - low overload: `60m` effective focus inside a `75m` window
  - high overload: `45m` effective focus inside a `75m` window
- Stretch break threshold:
  - low overload: `30m` effective focus inside a `35m` window
  - high overload: `25m` effective focus inside a `35m` window
- Deep-work detection defaults: sustained attention at least `900s`, confidence at least `0.65`, fragmentation at most `0.50`, volatility at most `0.50`, intensity between `0.40` and `0.75`, dwell on `120s`, dwell off `60s`.
- Notification suppression defaults: failure backoff `300s`, enable cooldown `600s`, minimum on-time `120s`.
- Brightness adjustment defaults: reduce by `5` points, never below brightness `20`, action cooldown `600s`, failure backoff `600s`.

## Overload Messages

| Action | Exact Trigger | Cooldown / Downgrade Rules | Payload Intent |
| --- | --- | --- | --- |
| `inform_overload_risk` | Emitted when the overload regulator crosses its activation rule and resolves to `Inform`, or when a stronger overload action is downgraded because overload nudges or restrictions are disabled. The generic overload regulator does not emit a break prompt directly. | Planned end `60s`. If `temporary_app_restriction_enabled` is blocked, overload falls back to `focus_mode_enabled` when an overload nudge feature is enabled, otherwise to `inform_overload_risk`. If `focus_mode_enabled` is blocked, it also falls back here. | Ari explains that overload is rising, says no change has been made yet, points the user to `Now`, and says the guidance can be muted in `Settings`. |
| `focus_mode_enabled` | Emitted by the overload regulator at `Nudge` level, or as the downgrade target for an overload `Restrict` action when temporary app restriction is disabled but at least one overload nudge feature is still enabled. | Planned end `600s`. Overload policy cooldown `600s`. If all overload nudge features are disabled, it degrades to `inform_overload_risk`. | Ari explains that overload stayed elevated long enough to turn on focus guidance, tells the user to keep one task in front of them, and points to `Settings` for opt-out. |
| `temporary_app_restriction_enabled` | Emitted by the overload regulator at `Restrict` level when overload stays above the policy threshold long enough and temporary app restriction is enabled. | Planned end `180s`. If temporary app restriction is disabled, overload downgrades to `focus_mode_enabled` when overload nudge features are available, otherwise to `inform_overload_risk`. | Ari explains that risk stayed high long enough to limit distracting apps for a short block, tells the user what to do, and points to `Settings` for opt-out. |
| `break_suggestion` | Emitted only by `BreakSuggestionPolicy`, not by the generic overload regulator. Conditions: suggestions enabled, confidence at least `0.50`, trust tier allows suggestions, continuity active, delivery boundary reached, break cooldown expired, and effective focus crosses the short-break threshold. Uses the high-overload threshold when overload is active or likelihood is at least `0.72`. | Prompt duration `300s`. Break cooldown `1800s`. If the delivery boundary is not reached or cooldown is active, the event is not emitted. | Ari explains that it sees a long work run and rising strain, asks for a short break now, and makes `Later` plus `Turn off this tip` explicit. |
| `overload_brightness_reduced` | Emitted when overload transitions from inactive to active and the brightness coordinator successfully reduces brightness. | Trigger follows overload stabilization entry. Reduction is `5` points, never below `20`. Brightness action cooldown `600s`. | Ari explains that overload stayed high and brightness was lowered to reduce strain, gives the user a restore path, and points to `Settings` for feature opt-out. |
| `overload_brightness_restored` | Emitted when overload transitions from active to inactive and brightness restore succeeds. | Trigger follows overload recovery. Restore path sets a `600s` cooldown. | Ari explains that overload eased and the old brightness was restored, says no action is required, and points to `Settings` if the feature should be changed. |
| `brightness_unsupported` | Emitted when the brightness controller reports that the device does not support automatic brightness control during entry or ramp handling. | Unsupported state is latched so the same unsupported warning is not spammed repeatedly. Failure backoff `600s`. | Ari explains that automatic brightness control is unavailable on this device, tells the user to adjust brightness manually, and points to `Health` plus `Settings`. |
| `brightness_control_failed` | Emitted when brightness read or set operations fail during overload entry or restore handling. | Failure backoff `600s`. Repeats are gated by runtime backoff. | Ari explains that Windows did not apply the brightness change, tells the user what manual action is available, and points to `Health` plus `Settings`. |
| `overload_recovered` | Emitted when overload stabilization transitions from active to inactive after the off-dwell rule is satisfied. | This is a state-transition log event, not an action loop. | Ari explains that overload returned to normal, says no action is needed, and points to `Now` while allowing recovery updates to be muted in `Settings`. |

## Focus Messages

| Action | Exact Trigger | Cooldown / Downgrade Rules | Payload Intent |
| --- | --- | --- | --- |
| `inform_focus_protection` | Emitted when the focus regulator resolves to `Inform`, or when stronger focus actions are downgraded because enabled features do not allow them. Focus policy defaults are soft `0.75`, hard `0.88`, activation `60s`, recovery `60s`, confidence gate `0.60`. | Planned end `60s`. If `focus_assist_enabled` is blocked, it downgrades to `focus_lock_in_suggestion`. If all stronger focus options are blocked, it settles here. | Ari explains that focus is building, says it is watching for interruptions only, points the user to `Now`, and says the guidance can be muted in `Settings`. |
| `focus_lock_in_suggestion` | Emitted directly by the focus regulator at `Suggest` level, or as the downgrade target for `focus_assist_enabled`, or as the downgrade target for focus-domain `temporary_app_restriction_enabled` when focus guidance is allowed but app restriction is disabled. | Prompt duration `300s`. Focus policy cooldown `600s` after restrict-level recovery only. | Ari explains that steady focus is building, asks the user to stay on one task, and makes `Later` plus `Turn off this tip` explicit. |
| `focus_assist_enabled` | Emitted directly by the focus regulator at `Nudge` level, or as the downgrade target for focus-domain `Restrict` when temporary app restriction is disabled but focus guidance is enabled. | Planned end `600s`. If focus guidance is disabled, it falls back to `focus_lock_in_suggestion`. | Ari explains that focus stayed strong long enough to turn on guidance, tells the user to keep distractions low, and points to `Settings` for opt-out. |

## Fatigue Messages

| Action | Exact Trigger | Cooldown / Downgrade Rules | Payload Intent |
| --- | --- | --- | --- |
| `inform_fatigue_prevention` | Emitted when the fatigue regulator resolves to `Inform`. Fatigue policy defaults are soft `0.70`, hard `0.82`, activation `120s`, recovery `180s`, confidence gate `0.60`, cooldown `900s`. The generic fatigue regulator does not emit a stretch prompt directly. | Planned end `60s`. If `fatigue_relief_enabled` is blocked, fatigue falls back here. If fatigue-domain `temporary_app_restriction_enabled` is blocked and animation reduction is off, it also falls back here. | Ari explains that fatigue is building, says it has not stepped in yet, points the user to a quick reset or `Now`, and says the guidance can be muted in `Settings`. |
| `fatigue_relief_enabled` | Emitted directly by the fatigue regulator at `Nudge` level, or as the downgrade target for fatigue-domain `Restrict` when temporary app restriction is disabled but animation reduction is enabled. | Planned end `600s`. If animation reduction is disabled, it downgrades to `inform_fatigue_prevention`. | Ari explains that fatigue stayed elevated long enough to turn on relief guidance, suggests a short reset, and points to `Settings` for opt-out. |
| `stretch_break_suggestion` | Emitted only by `BreakSuggestionPolicy`, not by the generic fatigue regulator. Conditions: suggestions enabled, confidence at least `0.50`, trust tier allows suggestions, continuity active, delivery boundary reached, cooldown expired, and effective focus crosses the stretch-break threshold. Uses the high-overload threshold when overload is active or likelihood is at least `0.72`. | Prompt duration `300s`. Break cooldown `1800s`. If the boundary is not reached or cooldown is active, no event is emitted. | Ari explains that it sees a long steady run, suggests a short stretch break, and makes `Later` plus `Turn off this tip` explicit. |

## Deep-Work Notification Messages

| Action | Exact Trigger | Cooldown / Downgrade Rules | Payload Intent |
| --- | --- | --- | --- |
| `deep_work_notifications_suppressed` | Emitted when deep work transitions from inactive to active and notification suppression succeeds. Deep work requires baseline-ready system actions, confidence at least `0.65`, fragmentation at most `0.50`, volatility at most `0.50`, intensity between `0.40` and `0.75`, sustained attention at least `900s`, and `120s` on-dwell. Low quality or partial data pauses the dwell. The coordinator also requires no active suppression, no enable cooldown, no failure backoff, a valid notification snapshot, and a successful apply. | Minimum on-time `120s`. Enable cooldown `600s`. Failure backoff `300s`. | Ari explains that a sustained focus block was detected, says Windows banners were muted, tells the user they can keep the block going or restore notifications, and points to `Settings` for opt-out. |
| `deep_work_notifications_restored` | Emitted when deep work transitions from active to inactive and notification restore succeeds. Deep work exit uses the off-signal and `60s` off-dwell. | Restore is blocked until minimum on-time `120s` elapses. Failure backoff `300s`. | Ari explains that the focus block ended and notifications were restored, says no action is needed, and points to `Settings` for feature control. |
| `notification_snapshot_failed` | Emitted when Ari enters deep work, tries to capture the current Windows notification snapshot, and snapshot capture fails or returns an invalid snapshot. | Failure backoff `300s`. No suppression is attempted after a failed or invalid snapshot. | Ari explains what failed, why restore safety is now uncertain, tells the user the manual action available, and points to `Health` plus `Settings`. |
| `notification_suppress_failed` | Emitted when Ari enters deep work, snapshot capture succeeds, and the suppression apply step fails. | Failure backoff `300s`. | Ari explains that deep-work entry happened but Windows notifications could not be muted, tells the user the manual action available, and points to `Health` plus `Settings`. |
| `notification_restore_failed` | Emitted when Ari exits deep work and restore fails. This includes both explicit restore failures and exit cases where the persisted snapshot is missing or invalid, because both map to a restore failure on exit. | Failure backoff `300s`. If minimum on-time has not elapsed yet, restore is delayed instead of failing. | Ari explains that the focus block ended but notification restore did not complete cleanly, tells the user to inspect banners, and points to `Health` plus `Settings`. |

## Notes On Message Ownership

- The central user-facing copy is generated in `SqliteBrainStateStore.ResolveInterventionToastMessage`.
- The payload message is intentionally generic and truthful. It does not mention foreground app names because the local widget contract does not currently expose a human-readable app or window label.
- For suggestions, the message text must remain consistent with the explicit prompt metadata:
  - `Yes`
  - `No`
  - `Later`
  - `Turn off this tip`

