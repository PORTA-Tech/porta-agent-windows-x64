# PORTA Local API Port Management v1

## Rules

1. Preferred port is fixed to `17887`.
2. On startup, if preferred port is busy, service negotiates to the next free port in range.
3. Active port is persisted to a shared endpoint file:
   - `%ProgramData%\PortaAgent\local-api.endpoint.json`
4. Tray and Control Center read the port from this shared config.
5. Token remains in secure storage only:
   - `%ProgramData%\PortaAgent\security\local-api.secure.json`

## Implementation

- Port selection + endpoint write:
  - `src/Porta.Agent.LocalApi/LocalApiRuntimeSettingsStore.cs`
- Service bind:
  - `src/Porta.Agent.Service/Program.cs`
- UI clients read endpoint:
  - `src/Porta.Agent.LocalApi/LocalApiClient.cs`
  - `src/Porta.ControlCenter/MainWindow.xaml.cs`
