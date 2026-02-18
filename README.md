# PORTA Agent Windows x64

Production-ready scroll processing agent for Windows service and console execution.

## Build Targets
- `Porta.Agent.Common`: `net8.0`, `x64`
- `Porta.Agent.Runtime`: `net8.0-windows`, `x64`
- `Porta.Agent.Service`: `net8.0-windows`, `x64`

Dependency direction is enforced: `Service -> Runtime -> Common`.

## Runtime Defaults (Locked)
- `PORTA_SUPPRESSION_ENABLED`: `false`
- `PORTA_ACTUATION_ENABLED`: `false`
- `PORTA_AUTO_STOP_MS`: disabled unless explicitly set
- Service name: `PortaAgentService`

## Local Build
```powershell
dotnet build PORTA.Windows.sln -c Release -m:1
dotnet build src/Porta.Agent.Common/Porta.Agent.Common.csproj
dotnet build src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj
dotnet build src/Porta.Agent.Service/Porta.Agent.Service.csproj
```

## Run
### Runtime console mode
```powershell
dotnet run --project src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj
```

### Service host (interactive process)
```powershell
dotnet run --project src/Porta.Agent.Service/Porta.Agent.Service.csproj
```

### Install as Windows service (admin)
```powershell
.\scripts\install-service.ps1 -StartAfterInstall
```

## Diagnostics
1. Runtime logs are emitted to stdout with UTC timestamps (`[INFO]`, `[EVENT]`, `[COUNTER]`, `[ERROR]`).
2. Key production counters:
   - `name=runtime_metrics`
   - `queue_depth`
   - `dropped`
   - `processing_errors`
3. Service status checks:
```powershell
Get-Service PortaAgentService
sc.exe query PortaAgentService
```

## Rollback
1. Stop and remove service:
```powershell
.\scripts\uninstall-service.ps1
```
2. Checkout previous stable tag/commit:
```powershell
git fetch --tags
git checkout <stable-tag-or-commit>
```
3. Rebuild and reinstall:
```powershell
dotnet build src/Porta.Agent.Service/Porta.Agent.Service.csproj
.\scripts\install-service.ps1 -StartAfterInstall
```

## Phase-6 Release Checklist
- [x] All phase acceptance criteria complete
- [x] `dotnet build` passes for Common, Runtime, Service
- [x] Soak matrix completed (suppression OFF/ON, service mode)
- [x] Baseline thresholds verified:
  - [x] avg CPU <= 2.0%
  - [x] max working set <= 200 MB
  - [x] max queue depth <= 32
  - [x] dropped events == 0
- [ ] No P0/P1 defects open
- [x] `docs/architecture.md` updated with UTC-stamped measured results
- [x] Install/run/diagnostics/rollback instructions verified
- [ ] Stable git tag created and pushed
