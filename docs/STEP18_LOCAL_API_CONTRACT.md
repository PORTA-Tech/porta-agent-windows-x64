# Step 18 Local API Contract (Pinned)

This contract keeps the Control Center bound to backend truth only.

## Normalization Layer

Endpoint handlers do not stitch DB/runtime fields directly.
Pinned mappers:

- `StatusMapper`
- `SnapshotMapper`
- `EventMapper`

These map core/runtime structures to API DTOs in one place.

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

Null/partial behavior:

- Endpoint still returns `200` for degraded subsystems.
- Unhealthy subsystem values are explicit in fields (`*_error`, `*_ready`, `*_supported`, `dbMigrationFailed`).

Control Center-oriented grouped blocks:

- `auth`:
  - `ready`
  - `error`
- `outputs`:
  - `notifications_suppressed`
  - `brightness_adjusted`
  - `brightness_supported`
  - `notification_error`
  - `brightness_error`
  - `notification_backoff_sec_remaining`
  - `brightness_backoff_sec_remaining`
  - `notification_backoff_utc`
  - `brightness_backoff_utc`
- `degraded`:
  - `degraded`
  - `service_running`
  - `db_migration_failed`
  - `auth_ready`
  - `notification_ready`
  - `brightness_ready`

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

Migration note:

- `breakSuggestionSecRemaining` and `break_suggestion_sec_remaining` are legacy compatibility fields for the pre-split break-suggestion cooldown path.
- When the canonical short-break and long-break overlay contract lands, the API should expose break-type-specific timing instead of relying on one shared break-suggestion alias.

Control Center-oriented grouped block:

- `current`:
  - `overload_active`
  - `deep_work_active`
  - `notifications_suppressed`
  - `brightness_adjusted`
  - `overload_likelihood`
  - `confidence`
  - `sustained_attention_duration_sec`
  - `f`, `i`, `v` (nullable)

Null/partial behavior:

- If no finalized bucket row exists yet, endpoint still returns safe defaults.
- `baselineReady=false`, `coldStart=true` by default until finalized-state metadata exists.
- `contributorsJson` and `stateFlagsJson` are always emitted with `schema_ver: 1`.

## Authentication behavior

- Protected endpoints still require valid `X-PORTA-TOKEN`.
- `/v1/status` remains diagnosable even when token material is unavailable.
- On token read failure, `/v1/status` exposes:
  - `localApiAuthReady=false`
  - `localApiAuthError=\"TOKEN_READ_FAILED\"`

## JSON discipline

- Booleans are booleans.
- Durations are integers in seconds and use `_sec`/`_sec_remaining` naming.
- Timestamps exposed for diagnostics use ISO UTC millisecond strings with `_utc` suffix.

## Do Not

- Do not compute business logic inside endpoint handlers/controllers.
- Do not expose raw secrets or token material in API payloads/logs.
- Do not let UI depend directly on private DB schemas.
- Do not omit `schema_ver` in JSON blobs.
- Do not use inconsistent field naming across endpoints for the same concept.

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

Null/partial behavior:

- If no rows exist, endpoint returns `[]`.
