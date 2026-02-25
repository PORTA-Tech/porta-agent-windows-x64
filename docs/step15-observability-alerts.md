# Step 15: Observability + Alerts (Agent-Side Implementation)

## Repository Scope
- Implemented in: `porta-agent-windows-x64`
- Covered here: agent runtime + tray observability/alerts
- Not implemented here by design:
  - control-plane dashboards/alert routing
  - console backend aggregation
  - fleet-wide RBAC alert governance

Those belong in `porta-control-plane` and console repositories.

## Implemented

### Correlation IDs
- Correlation IDs are generated as UUIDv7-compatible values at Layer 1 window acquisition.
- Propagated through:
  - `Layer2SignalSample.CorrelationId`
  - `Layer2PrimitiveResult.CorrelationId`
  - `Layer3PrimitiveInput.CorrelationId`
  - `Layer3StateResult.CorrelationId`
  - `Layer4StateResult.CorrelationId`
  - `AgentTelemetry.CorrelationId`
  - tray intervention drilldown
  - local encrypted logs

### Structured Telemetry Extensions
`AgentTelemetry` now includes:
- `CorrelationId`
- `PolicyId`
- `PolicyVersion`
- `SignatureVerified`
- `InterventionStatus`
- `CaptureToPrimitiveLatencyMs`
- `PrimitiveToInferenceLatencyMs`
- `PolicyEvaluationLatencyMs`
- `AuditLogWriteLatencyMs`
- `CorrelationTraceComplete`

### Metrics Emission
Agent emits `agent.metric` every 60 seconds:
- `agent_events_total` (per-minute delta)
- `interventions_applied_total` (per-minute delta)
- `interventions_failed_total` (per-minute delta)
- `state_inference_latency_ms`
- `policy_evaluation_latency_ms`
- `audit_log_write_latency_ms`
- `correlation_trace_complete` (`1|0`)

### Alert Rules in Tray
Sustained threshold logic implemented:
- stress > `0.90` for `30s`
- overload > `0.85` for `60s`
- fatigue > `0.75` for `30s`

### Intervention Safety Checks
Incoming websocket interventions are blocked when:
- `signature_verified == false`
- `policy_version != PORTA_EXPECTED_POLICY_VERSION` (default `1.0.0`)

Blocked interventions increment `interventions_failed_total` and emit `agent.error`.

### Local Log Retention
`Layer1LocalLogStore` now enforces:
- time retention: 90 days
- size cap: 50MB (oldest-first prune)

### Tray Trace Drilldown
Clicking a recent intervention shows:
- correlation ID
- policy ID/version
- signature status
- action window/reason
- trace path summary

## Files Updated
- `src/Porta.Agent.Common/Contracts/AgentTelemetry.cs`
- `src/Porta.Agent.Runtime/Layer2/Layer2SignalSample.cs`
- `src/Porta.Agent.Runtime/Layer2/Layer2PrimitiveResult.cs`
- `src/Porta.Agent.Runtime/Layer2/Layer2NormalizationEngine.cs`
- `src/Porta.Agent.Runtime/Layer3/Layer3PrimitiveInput.cs`
- `src/Porta.Agent.Runtime/Layer3/Layer3StateResult.cs`
- `src/Porta.Agent.Runtime/Layer3/Layer3InferenceEngine.cs`
- `src/Porta.Agent.Runtime/Layer4/Layer4StateResult.cs`
- `src/Porta.Agent.Runtime/Layer4/Layer4StabilityEngine.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1TrayModels.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1ControlPlaneStream.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1LocalLogStore.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1TrayController.cs`
- `src/Porta.Agent.Runtime/Layer1/Layer1AgentHost.cs`

## Validation
- `dotnet build PORTA.Windows.sln` passed (0 warnings, 0 errors)
