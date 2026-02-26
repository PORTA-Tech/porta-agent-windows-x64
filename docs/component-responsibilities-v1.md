# PORTA Component Responsibilities v1

This document is the ownership contract for the 3-process architecture.

## Service (Brain)

Project(s):
- `src/Porta.Agent.Service`
- `src/Porta.Agent.Core`
- `src/Porta.Agent.LocalApi`

Owns:
- Signal acquisition stage
- Primitive normalization stage
- State inference stage
- Stabilization stage
- Local regulation decision stage
- Consent hard gate
- Local SQLite state store
- Local secured localhost HTTP API
- Service-side logs and export bundle generation
- Optional backend connectivity probe

Does not own:
- Any tray icon logic
- Any WPF/WinForms user screen rendering

## Tray (Surface)

Project:
- `src/Porta.Tray`

Owns:
- Always-visible tray icon
- Quick actions (`pause`, `resume`, `open control center`, `privacy`, `exit tray`)
- Mini status popover (status + focus + overload + last action)
- Focusing/opening Control Center

Does not own:
- Signal collection
- Inference
- Storage writes
- Raw hooks

## Control Center (Surface)

Project:
- `src/Porta.ControlCenter`

Owns:
- Main window shell and diagnostics view
- Pulls truth from local API only
- Sends control commands to local API

Does not own:
- Signal collection
- Inference
- Storage writes
- Raw hooks

## Local API Rule

Local API is the only integration seam between surfaces and brain.

Current v1 endpoints:
- `GET /v1/status`
- `POST /v1/control/pause`
- `POST /v1/control/resume`
- `POST /v1/consent/grant`
- `POST /v1/consent/revoke`
- `POST /v1/data/export`
