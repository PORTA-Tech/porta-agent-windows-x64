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
