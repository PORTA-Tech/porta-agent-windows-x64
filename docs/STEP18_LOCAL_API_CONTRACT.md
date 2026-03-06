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

- `overloadLikelihood`
- `confidence`
- `sustainedAttentionDurationSec`
- `cooldownsRemaining`:
  - `notificationEnableSeconds`
  - `notificationMinOnSeconds`
  - `brightnessSeconds`
  - `breakSuggestionSeconds`
  - `anyActive`
- `contributorsJson` with `schema_ver: 1`
- `stateFlagsJson` with `schema_ver: 1`

## `/v1/events/recent`

Pinned ordering:

- `ORDER BY ts_utc DESC, id DESC`

API layer also enforces the same sort before response mapping.
