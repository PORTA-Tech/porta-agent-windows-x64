# Step 17 Visual Guardrails (Non-Negotiable)

These exclusions are mandatory for Step 17 visual work.

## Forbidden
- Registry theme changes
- Windows theme switching
- Input blocking
- App blocking
- Focus stealing
- Keyboard/mouse manipulation
- Notification spoofing
- Taskbar flashing loops
- Global OS color overlays

## Allowed Scope
- Tray visual state only
- PORTA Control Center accent/theming
- Safe product visuals (streak badge, recovery banner, explain drawer)

## Shared State Requirement
UI surfaces must consume the same presented state model from backend DTOs:
- visual mode
- overload/deep-work flags
- deep-work streak
- latest contributors/why payloads
- action states
