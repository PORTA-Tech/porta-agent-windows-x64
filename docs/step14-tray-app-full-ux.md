# Step 14: Tray App Full UX (PORTA Windows v1.0)

## Scope
- Repository: `porta-agent-windows-x64`
- Runtime component: `src/Porta.Agent.Runtime/Layer1`
- UI technology: Windows tray UX via WinForms `NotifyIcon` on `net8.0-windows`

## Implemented Concrete Values
- Tray status refresh interval: `1000 ms`
- Tooltip refresh interval: `2000 ms`
- Pause presets: `1, 5, 10, 15, 30, 60` minutes
- Recent interventions shown: max `5`
- Recent primitive snapshots shown: max `5`
- Alert thresholds:
  - `stress > 0.85`
  - `overload > 0.80`
  - `fatigue > 0.75`
- Alert cooldown per metric: `30 s`
- Default dashboard URL: `http://localhost:3000` (override via `PORTA_CONSOLE_URL`)
- Control-plane websocket URL: `PORTA_CONTROL_PLANE_WS_URL` (optional)
- Local encrypted log DB: `%LOCALAPPDATA%\Porta\Layer1\local-logs.db`
- Local log storage cap: `50 MB` (oldest rows pruned)

## UX Surfaces
- Tray icon states:
  - Active: information icon
  - Paused: warning icon
  - Error: error icon
  - Intervention-present: shield icon
- Menu sections:
  - Status
  - Last signal
  - Consent state
  - Risk summary with sigma
  - Pause remaining
  - Pause/Resume controls
  - Recent interventions
  - Last event snapshot
  - View data collected
  - Open dashboard
  - Export logs JSON
  - Export logs CSV
  - Open disclosure
  - Settings/Help
  - Revoke consent
  - Exit

## State Mapping for Risk Display
- `stress` <- Layer 4 distribution `critical`
- `overload` <- Layer 4 distribution `degrading`
- `focus` <- Layer 4 distribution `stable`
- `fatigue` <- Layer 4 distribution `elevated_risk`
- Display sigma: `1.0 - state_confidence`

## Pause/Resume Behavior
- Pause sets state to `paused_by_user`
- Pause duration is stored and countdown is shown in tray
- Auto-resume runs when pause duration elapses
- Resume clears pause timer and reinstalls hooks
- While paused:
  - signal capture disabled
  - emission disabled
  - aggregation buffers flushed

## Local Security + Offline Logging
- Persisted store: SQLite (`Microsoft.Data.Sqlite`)
- Log payload encryption: `AES-256-GCM`
- Log record integrity: `HMAC-SHA256` signature per record
- First-launch consent acceptance is logged locally as signed/encrypted record
- Export supported in both `JSON` and `CSV`

## Control-Plane Real-Time Feed
- Background websocket worker enabled when `PORTA_CONTROL_PLANE_WS_URL` is set
- Incoming payload supports `interventions[]` array with:
  - `action_type`
  - `start_time`
  - `end_time`
  - `confidence`
  - `policy_id`
  - `reason`
- Interventions are added to tray "Recent Interventions" and logged locally

## Data Models Added
- `Layer1TraySnapshot`
- `Layer1TrayPrimitiveView`
- `Layer1TrayInterventionView`

Defined in:
- `src/Porta.Agent.Runtime/Layer1/Layer1TrayModels.cs`

## Files Updated
- `src/Porta.Agent.Runtime/Layer1/Layer1TrayController.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1AgentHost.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1TrayModels.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1LocalLogStore.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1ControlPlaneStream.cs`
- `src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj`

## Verification
- Command: `dotnet build PORTA.Windows.sln`
- Result: success, `0` warnings, `0` errors
