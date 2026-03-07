# Project Boundaries (Step 0A)

This document pins the intended ownership split before signal-ingest cutover work begins.

`0A` is structural only. It does not claim that session capture has already moved out of the service. That behavior change happens in `0B`, `0C`, and `0F`.

## Active application boundaries

- `src/Porta.Agent.Service`
  - Windows machine service host.
  - Owns service startup, local API host, persistence bootstrap, deterministic engine composition, and machine-level output controllers.
  - Must not own interactive-session tray UX long term.
  - As of `0B`, direct keyboard/mouse/focus capture is disabled in the service; input enters through authenticated user-session ingest only.

- `src/Porta.UserAgent`
  - Interactive user-session process boundary.
  - Reserved for tray UX and all session-bound input capture and forwarding.
  - Must not become a second inference or persistence kernel.

- `src/Porta.ControlCenter`
  - WPF control surface.
  - Renders local API truth and sends control commands through the local API.
  - Must not talk directly to hooks, SQLite, or kernel internals.

## Supporting libraries

- `src/Porta.Agent.Core`
  - Deterministic engine implementation, bucket/finalizer logic, state models, policies, and persistence coordination.
  - The service hosts this library; the project remains separate to keep the kernel testable.

- `src/Porta.Agent.LocalApi`
  - Localhost API contracts, client, endpoint mappings, and runtime settings access.

- `src/Porta.Agent.Common`
  - Shared DTOs and common helpers.

- `src/Porta.Agent.Runtime`
  - Shared Windows runtime helpers and legacy runtime components that are being narrowed over time.
  - After the `0A` boundary, this is not the authoritative top-level tray/process boundary.

## Validation and tooling

- `src/Porta.Agent.Core.Tests`
  - Kernel and local API contract regression tests.

- `src/Porta.Agent.ValidationRunner`
  - Standalone validation harness.

## Repository note

- `src/Porta.Tray`
  - Not part of the active solution.
  - Treat as residual/stale repo content until tray responsibilities are fully represented by `Porta.UserAgent`.
