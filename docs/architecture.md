# PORTA Agent Architecture and Phase-6 Readiness

## Dependency and runtime rules
1. Dependency direction is strict: `Service -> Runtime -> Common`.
2. Target platform is locked to `x64` across all projects.
3. Hook callback remains enqueue-only and non-blocking.
4. Timing-critical logic uses `Stopwatch.GetTimestamp()`.
5. Risky behavior is controlled through feature flags:
   - `PORTA_SUPPRESSION_ENABLED`
   - `PORTA_ACTUATION_ENABLED`
   - `PORTA_AUTO_STOP_MS` (soak/test automation only)

## Explicit Actuation Contract (Phase-7.3)
Actuation is emitted only as a typed, versioned contract:

```csharp
struct ActuationCommand {
    int ContractVersion;
    double StimulationIntensity;    // 0..1
    double SceneDynamics;           // 0..1
    double VisualTemperatureShift;  // -1..1
    double PacingModifier;          // -1..1
    AuthorityLevel AuthorityLevel;  // Normal | Reduced | Safe
    string Note;
}
```

Rules:
1. Runtime/control interfaces do not emit raw controller scalars.
2. Mapping from controller signal to `ActuationCommand` is deterministic and centralized.
3. Environment mapping (`ActuationMapper`) is a deterministic unit transcode only and does not reinterpret intent.

## State Persistence Across Sessions (Phase-7.4)
Persist across sessions:
1. `BaselineArousalMean`
2. `BaselineVariance`
3. `DriftOffset`
4. `LastStableKp`, `LastStableKi`, `LastStableKd`

Do not persist:
1. Integral accumulator
2. Transient instability spikes

Startup behavior:
1. Warm-start baseline from persisted session when available.
2. Reset transient controller state (`IsInitialized=false`, integrator/derivative transients reset).

## Determinism Guarantees (Phase-7.5)
Determinism requirements:
1. Fixed timestep control evaluation (`TickIntervalTicks`) with strict monotonic timestamps.
2. No random sources in control/aggregation/safety pipeline.
3. Race-free engine evaluation through serialized access (`DeterministicControlEngine` lock).
4. Immutable replay order (timestamp-ordered with stable tie-break by input index).
5. Floating-point stabilization via deterministic quantization (`MathEx.Quantize`) at pipeline boundaries.

Acceptance property:
1. Given identical signal samples and timestamps, actuation outputs are identical.

## Control Clock Strictness (Phase-7.9)
Clock authority is now enforced inside the single processing loop:
1. Independent async control-clock task removed.
2. Tick schedule is monotonic and fixed-step (`TickIntervalTicks`).
3. Idle periods advance deterministically from monotonic runtime time.
4. Busy periods advance authority from processed input timestamps.
5. Due ticks are evaluated in-order with deterministic catch-up (no skipped ticks, no double-evaluation).

## Multi-Axis Safety Policy (Phase-7.10)
Safety arbitration policy is now an explicit behavioral contract even when only one physical axis is actuated.

Priority hierarchy:
1. Safe-mode override (`signal_confidence < threshold`) has absolute precedence.
2. Authority resolution (`normal|reduced|safe`) is applied before per-axis clamps.
3. Primary axes: `stimulation_intensity` then `scene_dynamics`.
4. Secondary axes: `visual_temperature_shift` then `pacing_modifier`.

Authority reduction policy:
1. `normal`: scale `1.0`
2. `reduced`: scale `0.5`
3. `safe`: scale `0.0`

Cross-axis clamp order:
1. Clamp/rate-limit `stimulation_intensity`.
2. Clamp `scene_dynamics` against intensity-derived envelope.
3. Clamp `visual_temperature_shift` within safe intensity envelope.
4. Clamp `pacing_modifier` within safe intensity envelope.

Safe-mode override precedence:
1. Confidence breach returns `ActuationCommand.Safe(...)` and bypasses all lower-priority arbitration.

## Persistence Orchestration Parity (Phase-7.11)
Service-mode and app-mode now share the same persistence lifecycle through `AgentHost`:
1. Startup loads session state before hooks/processing start.
2. Shutdown flushes session state after processing drains.
3. Session storage uses a shared default path in `CommonApplicationData` (`%ProgramData%\PortaAgent\control-session.json`).
4. Crash/corruption resilience:
   - Atomic write (`.tmp` + `File.Replace`)
   - Backup fallback (`control-session.json.bak`) on load
5. Warm-start behavior is identical across runtime and service hosts.

## Stability Acceptance Criteria (Phase-7.6)
Formal validation suite enforces:
1. `no_oscillation`: under sustained error, output converges without sign-flip oscillation.
2. `no_runaway_amplification`: saturating inputs cannot drive output past bounded safe limits.
3. `bounded_actuation`: all actuation contract fields remain in defined ranges.
4. `deterministic_replay`: identical recorded streams produce identical actuation outputs.

## Testing Requirements Before Track 3 (Phase-7.7)
Required validation scenarios:
1. `constant_high_arousal_convergence`
2. `oscillating_noise_damping`
3. `step_change_critical_damping`
4. `signal_loss_safe_fallback`
5. `replay_logs_determinism`

Latest validation execution (UTC):
- `run_utc`: `2026-02-19T10:00:15.9186529Z`
- `command`: `dotnet run --project src/Porta.Agent.ValidationRunner/Porta.Agent.ValidationRunner.csproj -c Release`
- Results:
1. `PASS` `constant_high_arousal_convergence` `monotonic_violations=0;tail_span=0.000000;outputs=240`
2. `PASS` `oscillating_noise_damping` `sign_flips=1;flip_ratio=0.005556;max_abs_pacing=0.143151;outputs=180`
3. `PASS` `step_change_critical_damping` `overshoot_ratio=1.000000;crossings=1;outputs=240`
4. `PASS` `signal_loss_safe_fallback` `non_safe_outputs=0;tail_checked=60;outputs=480`
5. `PASS` `replay_logs_determinism` `outputs=125;identical=True`
6. Summary: `total=5 failed=0`

## Runtime Control Snapshot Telemetry (Phase-7.8)
Runtime now emits control-decision snapshots into telemetry when enabled:
1. Enable with `PORTA_CONTROL_DIAGNOSTICS_TELEMETRY=1`
2. Telemetry kind: `control_snapshot`
3. Snapshot includes:
   - Aggregated state fields (`agg.*`)
   - Control features (`feat.*`)
   - Proposed policy command (`policy.*`)
   - Safety-arbitrated command (`safety.*`)
4. Local export:
   - NDJSON file at `%ProgramData%\PortaAgent\control-snapshots.ndjson`
   - Optional override: `PORTA_CONTROL_SNAPSHOT_EXPORT_PATH`

Schema decision (minimal canonical contract):
1. Name: `control_snapshot`
2. Version: `schemaVersion=1`
3. Canonical fields:
   - Identity/time:
     - `schemaVersion`
     - `tickTimestamp` (monotonic `Stopwatch` ticks)
     - `capturedUtc` (UTC wall-clock for transport correlation)
     - `precisionDecimals`
   - Aggregated:
     - `aggScrollRate`
     - `aggKeyboardRate`
     - `aggFocusStability`
     - `aggInteractionVariance`
     - `aggCompositeArousal`
     - `aggSignalConfidence`
   - Features:
     - `featArousalError`
     - `featArousalVelocity`
     - `featArousalIntegral`
     - `featEngagementIndex`
     - `featInstabilityIndex`
     - `featSignalConfidence`
   - Policy proposed:
     - `policyProposedIntensity`
     - `policyProposedDynamics`
     - `policyProposedVisualShift`
     - `policyProposedPacing`
     - `policyProposedAuthority`
   - Safety applied:
     - `safetyAppliedIntensity`
     - `safetyAppliedDynamics`
     - `safetyAppliedVisualShift`
     - `safetyAppliedPacing`
     - `safetyAppliedAuthority`
     - `safetyNote`
4. Precision:
   - Numeric values rounded to 6 decimals (`MidpointRounding.AwayFromZero`) before serialization.

## CI Validation Gate (Phase-7.12)
Regression prevention is enforced in CI:
1. Workflow: `.github/workflows/validation-gate.yml`
2. Runner: `windows-latest`
3. Gate command:
   - `dotnet run --project src/Porta.Agent.ValidationRunner/Porta.Agent.ValidationRunner.csproj -c Release --no-build`
4. Failure policy:
   - Any non-zero exit from `Porta.Agent.ValidationRunner` fails the build.

## Baseline thresholds (Phase-6 gate)
The following thresholds define PASS/FAIL for production-readiness smoke and soak runs:

| Metric | Threshold |
|---|---|
| Average CPU | <= 2.0% |
| Max working set | <= 200 MB |
| Max queue depth | <= 32 |
| Dropped events | 0 |
| Runtime process crash | 0 |

## Phase-6 soak matrix (UTC-stamped)
Source data: `docs/phase6-soak-results.csv`

### Scenario A: runtime_off
- `utc_start`: `2026-02-18T12:24:54.1315602+00:00`
- `utc_end`: `2026-02-18T12:26:27.7844503+00:00`
- `duration_seconds`: `93.7`
- `suppression_enabled`: `False`
- `actuation_enabled`: `False`
- `avg_cpu_pct`: `0.087`
- `max_working_set_mb`: `74`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- Verdict: PASS

### Scenario B: runtime_on
- `utc_start`: `2026-02-18T12:26:27.9201280+00:00`
- `utc_end`: `2026-02-18T12:28:00.1321077+00:00`
- `duration_seconds`: `92.2`
- `suppression_enabled`: `True`
- `actuation_enabled`: `True`
- `avg_cpu_pct`: `0.09`
- `max_working_set_mb`: `70.36`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- Verdict: PASS

### Scenario C: service_mode
- `utc_start`: `2026-02-18T12:28:00.1350977+00:00`
- `utc_end`: `2026-02-18T12:29:47.4220082+00:00`
- `duration_seconds`: `107.3`
- `suppression_enabled`: `False`
- `actuation_enabled`: `False`
- `avg_cpu_pct`: `0.083`
- `max_working_set_mb`: `71.32`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- Verdict: PASS

## Config defaults lock (Phase-6)
Defaults are explicitly locked in code paths:
1. Suppression default: `OFF` (`PORTA_SUPPRESSION_ENABLED` unset -> false)
2. Actuation default: `OFF` (`PORTA_ACTUATION_ENABLED` unset -> false)
3. Service name default: `PortaAgentService`
4. Telemetry queue mode: bounded queue with drop-oldest policy
5. Heartbeat interval: 5 seconds

## Known limitations
1. Current soak matrix is short-duration automation (about 1.5-1.8 minutes per scenario), not multi-hour endurance.
2. Service installation/SCM operations require elevated privileges and are not exercised by this automation script.
3. Soak script captures host-level CPU/memory and runtime metrics logs; it does not yet include ETW trace correlation.

## Readiness summary
1. Baseline thresholds are defined and measured.
2. Soak matrix includes suppression OFF/ON and service mode with UTC timestamps.
3. No queue growth or dropped events were observed in the matrix.
4. Operational runbook and rollback are documented in `README.md`.

## Build verification
1. `dotnet build PORTA.Windows.sln -c Release -m:1` succeeded at `2026-02-18T12:45:13.5173553Z` (UTC).

## Phase-7.1 Keyboard Capture (Capture-Only) Validation
Date (UTC): 2026-02-18

Configuration:
- `PORTA_KEYBOARD_CAPTURE_ENABLED=1`
- `PORTA_SUPPRESSION_ENABLED=0`
- `PORTA_ACTUATION_ENABLED=0`

Results:
1. Run A
- `utc_start`: `2026-02-18T13:08:54.5511464+00:00`
- `utc_end`: `2026-02-18T13:10:20.0405545+00:00`
- `duration_seconds`: `85.5`
- `avg_cpu_pct`: `0.248`
- `max_working_set_mb`: `84.55`
- `max_queue_depth`: `1`
- `max_dropped`: `0`
- `metrics_samples`: `16`
- `log_file`: `docs/soak-logs/keyboard_capture-20260218T130854Z.log`

2. Run B
- `utc_start`: `2026-02-18T15:02:05.3080475+00:00`
- `utc_end`: `2026-02-18T15:10:20.0516462+00:00`
- `duration_seconds`: `494.7`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- `metrics_samples`: `87`
- `log_file`: `docs/soak-logs/keyboard_capture-20260218T150202Z.log`

Assessment:
- Capture-only keyboard hook remained stable.
- No dropped events observed.
- Queue depth remained bounded.
- Full 2-hour soak remains pending for strict closure.

## Phase-7.2 Foreground / Focus Tracking Validation
Date (UTC): 2026-02-18

Configuration:
- `PORTA_KEYBOARD_CAPTURE_ENABLED=1`
- `PORTA_SUPPRESSION_ENABLED=0`
- `PORTA_ACTUATION_ENABLED=0`
- Foreground tracking via `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`

Short validation run:
- `utc_start`: `2026-02-18T15:15:45.4083763+00:00`
- `utc_end`: `2026-02-18T15:16:15.4897443+00:00`
- `duration_seconds`: `30.1`
- `focus_enqueued`: `1`
- `focus_processed`: `1`
- `focus_dropped`: `0`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- `log_file`: `docs/soak-logs/focus_validation.log`

Assessment:
- Foreground/process change capture is integrated into the deterministic queue.
- No suppression or injection behavior changes were introduced.
- Focus event counters are emitted in `runtime_metrics` as:
  - `focus_enqueued`
  - `focus_processed`
  - `focus_dropped`

## Phase-7.2 Combined Stability Soak (Scroll + Keyboard + Focus)
Date (UTC): 2026-02-19

Run:
- `utc_start`: `2026-02-19T05:43:47.2262723+00:00`
- `utc_end`: `2026-02-19T06:43:49.9626472+00:00`
- `duration_seconds`: `3602.7`
- `suppression_enabled`: `False`
- `actuation_enabled`: `False`
- `keyboard_capture_enabled`: `True`
- `avg_cpu_pct`: `0.01`
- `max_working_set_mb`: `83.24`
- `max_queue_depth`: `0`
- `max_dropped`: `0`
- `max_keyboard_dropped`: `0`
- `max_focus_dropped`: `0`
- `max_focus_enqueued`: `68`
- `max_focus_processed`: `68`
- `metrics_samples`: `718`
- `log_file`: `docs/soak-logs/phase72-combined-20260219T054347Z.log`

Assessment:
- Combined input path remained stable with no queue growth and no dropped events.
- Focus and keyboard channels remained bounded under mixed interactive/passive use.
- Verdict: PASS (1-hour combined soak).
- Strict 2-hour gate can be run later if required by release policy.
