# Step 18 Local API Contract (Pinned)

This contract keeps the Control Center bound to backend truth only.

## `/v1/status`

Status payload includes:

- `overloadActive`
- `deepWorkActive`
- `notificationsSuppressed`
- `brightnessAdjusted`
- `brightnessSupported`
- `dbMigrationFailed`
- `localApiAuthReady`
- `localApiAuthError`
- `notificationSuppressionError`
- `brightnessError`
- `notificationBackoffUntilUtc`
- `brightnessBackoffUntilUtc`
- `baselineReady`
- `coldStart`

Compatibility fields are still exposed in parallel (`notificationSuppressionActive`, `brightnessActionActive`, etc.).

## `/v1/snapshot/current`

Snapshot payload includes:

- `overloadLikelihood` + alias `overload_likelihood`
- `confidence`
- `sustainedAttentionDurationSec` + alias `sustained_attention_duration_sec`
- `cooldownsRemaining`:
  - `breakSuggestionSecRemaining` + alias `break_suggestion_sec_remaining`
  - `deepWorkNotificationEnableSecRemaining` + alias `deep_work_notification_enable_sec_remaining`
  - `brightnessActionSecRemaining` + alias `brightness_action_sec_remaining`
  - `deepWorkNotificationMinOnSecRemaining`
  - `anyActive`
- `contributorsJson` + alias `contributors_json`, with `schema_ver: 1`
- `stateFlagsJson` + alias `state_flags_json`, with `schema_ver: 1`
- `FComponent / IComponent / VComponent` + aliases `F/I/V` (when present in contributors JSON)
- `overloadActive` + alias `overload_active`
- `deepWorkActive` + alias `deep_work_active`
- `notificationsSuppressed` + alias `notifications_suppressed`
- `brightnessAdjusted` + alias `brightness_adjusted`

## `/v1/events/recent`

Pinned ordering:

- `ORDER BY ts_utc DESC, id DESC`

Event response includes proof-trail fields:

- `id`
- `ts_utc` (also available as `tsUtc`)
- `type`
- `why_json` (also available as `whyJson`)
- `state_flags_json` (also available as `stateFlagsJson`, nullable)

Expected recent action coverage includes:

- `DEEP_WORK_NOTIFICATIONS_SUPPRESSED`
- `DEEP_WORK_NOTIFICATIONS_RESTORED`
- `OVERLOAD_BRIGHTNESS_REDUCED`
- `OVERLOAD_BRIGHTNESS_RESTORED`
- `NOTIFICATION_SUPPRESS_FAILED`
- `BRIGHTNESS_CONTROL_FAILED`
- `BREAK_SUGGESTION`
