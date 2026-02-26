# PORTA Local Store (SQLite) v1

This repo uses SQLite as the mandatory local data plane for the Windows agent.

## Database

- Path: `%ProgramData%\PortaAgent\core-state.db`
- Retention: `90` days (automatic prune every ~30 minutes)

## Tables

1. `input_buckets`
- `timestamp_utc`
- `typing_burst_intensity`
- `scroll_velocity_index`
- `focus_switch_rate`

2. `state_buckets`
- `timestamp_utc`
- `focus_strength`
- `overload_likelihood`
- `fatigue_probability`
- `cognitive_stability`
- `confidence`
- `contributors_json`

3. `interventions`
- `timestamp_utc`
- `action`
- `trigger_state`
- `trigger_value`
- `confidence`
- `duration_seconds`
- `escalation_level`
- `reversible`
- `reason`
- `contributors_json`

4. `diagnostics_snapshots`
- `timestamp_utc`
- `backend_connected`
- `consent_active`
- `paused`
- `last_error`
- `policy_version`
- `schema_version`
- `agent_version`
- `last_event_timestamp_utc`

5. `brain_snapshots` (compatibility + timeline reads)
- Full combined snapshot row per second.

## Implementation

- Store implementation:
  - `src/Porta.Agent.Core/SqliteBrainStateStore.cs`
- Store contracts:
  - `src/Porta.Agent.Core/IBrainStateStore.cs`
  - `src/Porta.Agent.Core/AgentLocalStoreModels.cs`
- Controller write paths (events + diagnostics):
  - `src/Porta.Agent.Core/AgentCoreController.cs`
