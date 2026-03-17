# Ari Video Trace Spec

Schema: `porta.ari.video-trace.v1`

Purpose:
- Convert recorded Phase 1 Ari captures into one renderer-friendly, offline dataset.
- Preserve exact recorded Ari values while trimming the source material into a concise story arc.
- Give the website/video renderer a fixed contract that does not depend on live runtime.

## Files

Phase 2 outputs are written into one trace directory:

- `ari-video-trace.json`
- `ari-video-beat-map.json`
- `curation-summary.json`

Default output root:

- `%ProgramData%\PortaAgent\video-traces`

## Story Arc

Each curated trace is organized around four ordered chapters:

1. `observe`
2. `infer`
3. `regulate`
4. `explain`

The current auto-curation strategy is `event-centered-auto`.
It selects the strongest real Ari intervention event from the supplied raw captures, then builds a 30-60 second window around that event.

## Root Shape

`ari-video-trace.json`

```json
{
  "meta": {},
  "signals": [],
  "snapshots": [],
  "events": [],
  "chapters": []
}
```

## `meta`

`meta` describes trace identity, timing, and provenance.

Fields:

- `schema`: format version, currently `porta.ari.video-trace.v1`
- `traceId`: unique trace identifier
- `label`: human-readable trace label
- `createdAtUtc`: trace export timestamp
- `frameRate`: target renderer FPS
- `durationSec`: final curated runtime in seconds
- `curationMode`: curation strategy name
- `storyArc`: fixed narrative order
- `sourceCaptures`: capture provenance and selected source ranges

## `sourceCaptures`

Each source capture entry includes:

- `captureId`
- `label`
- `directoryPath`
- `observedFromUtc`
- `observedToUtc`
- `segments`

Each segment includes:

- `chapterId`
- `sourceFromUtc`
- `sourceToUtc`
- `traceStartSec`
- `traceEndSec`
- `selectionReason`
- `selectionScore`

This lets the renderer and review process trace every chapter back to the exact recorded Ari source interval.

## `signals`

`signals` are renderer-friendly samples derived directly from recorded Ari signal artifacts.
They may come from either:

- `input_signal_windows` for newer raw-window captures
- `input_buckets` for older historical recorded sessions

Each signal sample includes:

- `signalKind`: `input_signal_window` or `input_bucket`
- local trace timing: `tStartSec`, `tEndSec`
- provenance: `sourceCaptureId`, `sourceStartUtc`, `sourceEndUtc`
- signal intensity: `typingBurstIntensity`, `scrollVelocityIndex`, `focusSwitchRate`
- keyboard activity: `keyboardKeydownCount`, `keyboardBackspaceCount`, `keyboardBackspaceRate`, `keyboardIkiMeanMs`, `keyboardIkiStdMs`, `keyboardIkiCv`, `keyboardBurstCount`, `keyboardBurstRate`, `keyboardCorrectionSpikeCount`, `keyboardCorrectionSpikeRate`
- mouse/scroll activity: `mouseWheelDelta`, `mouseWheelEventCount`, `mouseMoveDx`, `mouseMoveDy`, `mouseMoveSampleCount`, `mousePathLengthPx`, `mouseSpeedMeanPxPerSec`, `mouseJerkP95`, `mouseMicroCorrectionCount`, `mouseMicroCorrectionRate`, `mouseScrollBurstCount`, `mouseScrollBurstRate`, `mouseScrollSpeedMean`
- focus context: `focusObservationCount`, `focusSwitchCount`, `focusActiveAppCount`, `focusAppEntropy`, `focusAppHash`
- idle context: `idleSeconds`, `idleObservationCount`, `idleSecondsWithinWindow`, `microIdleCount`, `microIdleSeconds`, `trueBreakSeconds`, `trueBreakFlag`, `shortIdleBoundaryObserved`
- output environment hint: `notificationToastsEnabled`
- source quality metadata: `qualityScore`, `partial`, `contributorsJson`

Fields remain nullable because historical captures do not contain every raw-window field.
The trace stays truthful by preserving exactly what was recorded instead of inventing missing values.

## `snapshots`

`snapshots` are recorded Ari state outputs derived from `brain_snapshots`.

Each snapshot sample includes:

- local trace time: `tSec`
- provenance: `sourceCaptureId`, `sourceTimestampUtc`
- Ari state: `focusStrength`, `overloadLikelihood`, `fatigueProbability`, `cognitiveStability`
- dominant state: `dominantStateName`, `dominantStateValue`
- confidence and action state: `confidence`, `lastAction`
- control flags: `activationWindowTriggered`, `cooldownTriggered`, `activationWindowOpen`, `overloadActive`
- continuity / fragmentation: `sustainedAttentionDurationSec`, `idleFragmentationIndex`
- explainability payloads: `stateFlagsJson`, `contributors`

## `events`

`events` are recorded Ari intervention or regulation events.

Each event sample includes:

- local trace time: `tSec`
- provenance: `sourceCaptureId`, `sourceTimestampUtc`
- intervention fields: `action`, `triggerState`, `triggerValue`, `confidence`, `durationSeconds`
- explainability fields: `reason`, `contributors`, `whyJson`, `stateFlagsJson`
- delivery/output context: `commandType`, `executionState`, `whatChanged`, `howToUndo`, `lifecycleState`
- identifiers: `interventionId`, `policyId`, `domain`

Phase 2 requires at least one real event because the final story arc must include `regulate`.

## `chapters`

`chapters` lock the high-level story structure for the renderer.

Each chapter includes:

- `chapterId`
- `title`
- `role`
- `startSec`
- `endSec`
- `sourceCaptureId`
- `sourceFromUtc`
- `sourceToUtc`
- `purpose`
- `dataFocus`

The current four chapter ids are:

- `observe`
- `infer`
- `regulate`
- `explain`

## Beat Map

`ari-video-beat-map.json` contains the locked beat timing for editorial and motion design.

Current beat ids:

1. `observe-signals`
2. `observe-boundaries`
3. `infer-state-rise`
4. `infer-confidence`
5. `regulate-trigger`
6. `regulate-outcome`
7. `explain-why`

Each beat includes:

- `beatId`
- `chapterId`
- `startSec`
- `endSec`
- `title`
- `purpose`
- `dataFocus`
- `sourceCaptureId`
- `sourceFromUtc`
- `sourceToUtc`

## Truth Rules

- No field in the trace may be invented.
- `signals`, `snapshots`, and `events` must come from recorded Ari data.
- Curation may trim or reorder source intervals only if provenance is preserved.
- The current auto-curation implementation selects one contiguous real source sequence around the strongest event.
- Future multi-capture stitching is allowed only if every beat and chapter still maps to recorded source intervals.
