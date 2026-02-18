# Runtime Input Pipeline and Soak Notes

## Architecture changes (2026-02-18)

1. Queue polling was replaced with `Channel<ScrollEvent>` in `AgentHost`.
2. `AgentHost` lifecycle is now `StartAsync`/`StopAsync` driven.
3. Shutdown is cancellation-aware and drain-aware:
   - Stop input first (`MouseHook.Uninstall()`).
   - Complete channel writer (`TryComplete()`).
   - Await processing loop completion.
   - Cancel and stop metrics loop.
4. Hook callback path is enqueue-only and allocation-minimized:
   - Callback only extracts wheel delta and raises `ScrollDeltaChanged`.
   - `AgentHost.OnScrollDelta` only `TryWrite`s into channel and updates counters.
   - `ScrollEvent` is a readonly struct to avoid per-event heap allocation.
5. Runtime `Program.cs` was simplified to host lifecycle only (Ctrl+C cancel + async stop). ESC key polling was removed.

## Soak test record

### Executed run
- Date (UTC): 2026-02-18
- Duration: ~29.8 seconds
- Method:
  - Started `Porta.Agent.Runtime` in Release mode.
  - Injected synthetic wheel input (`mouse_event` up/down) every ~200ms.
  - Sampled process CPU + memory continuously.
  - Collected runtime `[METRIC]` output.

### Observed metrics
- Runtime metrics:
  - `enqueued=274`, `processed=274`, `dropped=0`, `queue_depth=0` at end of run
  - `eps` stayed around `9.17-9.59` during synthetic input
- CPU:
  - Average over run: `~0.26%`
- Memory:
  - Working set: `110.61 MB` -> max `117.61 MB`
  - Private memory: `44.58 MB` -> max `51.91 MB`
- Responsiveness:
  - Synthetic input path remained stable (no queue growth, no drops).

### Interactive soak completion (user run)
- Date (UTC): 2026-02-18
- Duration: `>= 2 hours` (user-confirmed complete)
- Runtime behavior from captured logs:
  - Repeated intervals with `dropped=0` and `queue_depth=0`
  - `enqueued` and `processed` remained matched (`enqueued == processed`)
  - No crash observed; process stopped cleanly (`AgentHost stopping...`, `AgentHost stopped.`)
- Responsiveness:
  - Scroll input remained responsive during active use (user-observed)

### Phase-1 verdict
- PASS

## Phase-2 completion (deterministic control path, local)

- Date (UTC): 2026-02-18
- Runtime validation summary:
  - OFF profile: `dropped=0`, `queue_depth=0`, stable processing.
  - ON profile: `suppressed=44`, `injected=44`, `dropped=0`, `queue_depth=0`, clean shutdown.
  - Safety/focus blocks during run: `blocked_by_safety=0`, `blocked_by_focus=0`.
- System observation summary:
  - CPU remained low and stable during monitoring.
  - Working set remained stable (about `130-133 MB`, no growth trend).
- Subjective responsiveness:
  - No noticeable lag/stutter, no UI freeze, normal responsiveness.

### Phase-2 verdict
- PASS

## Phase-4 service integration (Windows Service wrapper)

- Service entrypoint:
  - `src/Porta.Agent.Service/Program.cs` now runs through `Host.CreateDefaultBuilder(...).UseWindowsService(...)`.
  - Service name is configured as `PortaAgentService`.
- Runtime bridge:
  - `src/Porta.Agent.Service/AgentService.cs` now owns `AgentHost` and maps SCM lifecycle to runtime lifecycle:
    - `StartAsync` -> `AgentHost.StartAsync`
    - `StopAsync` -> `AgentHost.StopAsync`
  - Start/stop are guarded for idempotency.
- Operations scripts:
  - `scripts/install-service.ps1` publishes service binaries, creates SCM service, configures auto-start, and enables crash-restart policy.
  - `scripts/uninstall-service.ps1` stops and deletes service cleanly.

### Service operations

- Install:
  - `.\scripts\install-service.ps1 -StartAfterInstall`
- Start:
  - `Start-Service PortaAgentService`
- Stop:
  - `Stop-Service PortaAgentService`
- Restart:
  - `Restart-Service PortaAgentService`
- Uninstall:
  - `.\scripts\uninstall-service.ps1`

### Validation checklist (Phase-4)

1. Install/start/stop/restart/uninstall sequence
   - Verify SCM reports expected status at each step.
   - Verify no orphaned `Porta.Agent.Service.exe` process after stop/uninstall.
2. Reboot persistence
   - Confirm service starts automatically after reboot (`start= auto`).
3. Crash-recovery
   - Terminate service process and verify SCM restart policy restarts it.
