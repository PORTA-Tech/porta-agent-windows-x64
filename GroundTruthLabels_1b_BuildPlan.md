# Ari 1b — Ground-Truth Label Capture: Build Plan

**Status:** Plan for review. No code written yet.
**Goal:** Start recording whether Ari's guesses were right, as durable, self-contained training examples, so the future learned model (Phase 6) has data. **Done when:** labels are accumulating in a table, visible in a diagnostics readout, and survive the 14-day prune.

This is heuristics + data plumbing. **No ML in 1b.**

---

## 0. Grounding facts (measured from the real fleet DB, not assumed)

Every decision below is justified by data pulled from the live `core-state.db`:

- **Retention is exactly 14 days.** `state_buckets` span: `2026-06-17 → 2026-07-01` = **14.0 days**. Same for `input_buckets`, `interventions`, `events`. The "90 days" disclosure string is **wrong** and should be corrected.
- **The DB is 1.3 GB in 14 days**, dominated by the high-frequency firehose: `primitive_buckets` ~888 MB, `state_buckets` ~167 MB. Write rate ≈ **93 MB/day**. This is why the prune exists and why keeping raw data for months would freeze the machine (VACUUM/checkpoint hang on a >1 GB WAL — documented in `ScheduleBackgroundVacuumIfNeeded`).
- **Labels are a different data class.** A fat label row (full feature snapshot) measures ~3.9 KB; a lean row ~800 B. At realistic volume (see below) that is **single-digit to low-tens of MB per _year_** — the firehose writes that much in ~2 hours. Labels cannot cause the freeze.
- **Real label volume, measured:** user-answerable nudges (`short_break` 23 + `long_break` 6) = **~2/day** passive. The frequent interventions (`inform_overload_risk` 1,210, `focus_mode_enabled` 907, `temporary_app_restriction` 886 over 14 days) are auto-applied system actions, not questions. So passive alone (~2/day → ~360 in 6 months) is too thin; a **sampled** one-tap prompt is required for usable volume.
- **The user's response already flows to the store.** `AgentInterventionAck.Result` (`accepted`/`dismissed`/`skipped`/`snoozed`) already arrives via `POST /v1/interventions/ack` → `AcknowledgeIntervention`. Passive capture = persist what already arrives.

---

## 1. Design principles (the guardrails)

1. **Never grow the firehose.** The 14-day prune on raw tables stays untouched. Machine protection is non-negotiable.
2. **Labels are self-contained.** Each row stores the full feature vector + the answer. It must NOT foreign-key to a `state_bucket` (that row is deleted in 14 days).
3. **Prune-exempt, kept forever, tiny.** Labels live in their own table excluded from the periodic prune.
4. **Local-first, privacy intact.** Labels stay on the device (same privacy class as `state_buckets` — numeric signals + hashed app id + coarse category, **no raw keystrokes, no window-title text**). Server upload is explicitly deferred and consent-gated.
5. **Opportunistic, never intrusive.** Capture at moments Ari is already present (nudge responses, natural boundaries). Never interrupt active focus. Respect meeting shield, pause, and opt-out.
6. **Additive & reversible.** New table, new endpoint, new optional widget element. Nothing about pressure, nudges, or actuation changes. Worst case is "no labels captured" — never a regression or crash.

---

## 2. Architecture overview

```
                    ┌────────────────────────────────────────────┐
 Passive path       │  BREAK / RECOVERY OVERLAY (already exists)  │
 (already flowing)  │  user taps Accept / Dismiss / Skip / Snooze│
                    └──────────────────────┬─────────────────────┘
                                           │ /v1/interventions/ack  (Result already carried)
                                           ▼
                    ┌────────────────────────────────────────────┐
                    │ AcknowledgeIntervention()                  │
                    │   → if Result is a user choice on a nudge:  │
                    │       RecordGroundTruthLabel(snapshot, ...)│
                    └──────────────────────┬─────────────────────┘
                                           ▼
 Active path        ┌────────────────────────────────────────────┐   writes
 (new, sampled)     │  WIDGET one-tap  👍/👎  (boundary-timed)    │──────────►  ground_truth_labels
                    │  shown only when governor says "due"        │   (prune-EXEMPT,
                    └──────────────────────┬─────────────────────┘    self-contained,
                                           │ /v1/labels/focus            local, forever)
                                           ▼
                    ┌────────────────────────────────────────────┐
                    │ RecordGroundTruthLabel(snapshot, ...)      │
                    └────────────────────────────────────────────┘
```

---

## 3. The label table (schema)

New migration **Version 20** (latest is 19), `Schema.ApplyGroundTruthLabelsV20`:

```sql
CREATE TABLE IF NOT EXISTS ground_truth_labels (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at_utc        TEXT    NOT NULL,        -- when the label was captured
    bucket_seq            INTEGER NOT NULL DEFAULT -1,   -- reference only (bucket may be pruned)
    bucket_timestamp_utc  TEXT    NOT NULL DEFAULT '',

    -- The ground-truth answer
    label_source          TEXT    NOT NULL,        -- break_nudge | recovery_nudge | focus_check | overload_check | distraction_check
    prompt_kind           TEXT    NOT NULL,        -- what was asked: accept_break | focused_now | overload_right | on_task_now ...
    response_kind         TEXT    NOT NULL,        -- raw answer: accepted|dismissed|skipped|snoozed|focused|not_focused|on_task|off_task
    label_target          TEXT    NOT NULL,        -- what it teaches: break_need | focus_state | overload | distraction
    label_value           INTEGER NOT NULL,        -- normalized: +1 positive / -1 negative / 0 neutral
    response_latency_ms   INTEGER NOT NULL DEFAULT -1,
    intervention_id       TEXT    NOT NULL DEFAULT '',

    -- Self-contained feature vector (survives bucket pruning)
    features_json         TEXT    NOT NULL,        -- full training-example features (see §4)

    -- Hot columns duplicated out of features_json for cheap SQL filtering/QA
    pressure_level        REAL    NOT NULL DEFAULT 0,
    overload_likelihood   REAL    NOT NULL DEFAULT 0,
    confidence            REAL    NOT NULL DEFAULT 0,
    dominant_state        TEXT    NOT NULL DEFAULT '',
    calibration_stage     TEXT    NOT NULL DEFAULT '',
    trust_tier            TEXT    NOT NULL DEFAULT '',

    schema_ver            INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX IF NOT EXISTS idx_gtl_created ON ground_truth_labels(created_at_utc);
CREATE INDEX IF NOT EXISTS idx_gtl_target  ON ground_truth_labels(label_target, label_value);
```

**Retention:** the table is **added to the full user-initiated reset wipe** (lines ~745–756, so a user "delete my data" clears labels too — consent-correct) but **NOT to the periodic 14-day `PruneIfNeeded` DELETE list**. That single omission is what makes labels durable.

---

## 4. What each label captures (the feature vector)

`features_json` is a snapshot of everything the future model needs, taken at capture time from the finalized bucket + derived results (all already computed in the finalizer). No raw content.

- **Primitives:** typing_burst_intensity (raw+smoothed), scroll_velocity_index (raw+smoothed), focus_switch_rate (raw+smoothed)
- **Rich input dynamics:** keyboard_iki_mean, keyboard_iki_cv, keyboard_burst_rate, keyboard_backspace_rate, mouse_speed_mean, mouse_jerk_p95, mouse_scroll_speed, mouse_micro_correction_rate
- **Focus/context:** focus_app_entropy, focus_switch_count, focus_active_app_count, dominant_app_hash, dominant_app_category, idle_seconds, idle_micro_idle_seconds, true_break_seconds
- **Attention classification (what Ari believed at capture):** attention_segment_kind (the specific kind, e.g. off_task_distraction / social_media_distraction / comm_monitoring_loop / focus), distraction_episode_active, task_continuity_state — so distraction/focus labels can be scored per-kind
- **Derived state:** f_component, i_component, v_component, pressure_level, overload_likelihood, confidence, quality, fatigue_probability, cognitive_stability, sustained_attention_duration_sec (task_continuity_seconds)
- **Meta:** calibration_stage, trust_tier, dominant_state, agent_version, schema_ver

This makes every row a complete `(features → answer)` pair — trainable years later regardless of pruning.

**Label normalization (`label_value`, `label_target`):**

| Event | target | value |
|---|---|---|
| Break nudge **accepted** | break_need | **+1** (Ari right) |
| Break nudge **dismissed / skipped** | break_need | **−1** (not needed) |
| Break nudge **snoozed** | break_need | **0** (ambiguous) |
| Focus check **👍 focused** | focus_state | **+1** |
| Focus check **👎 not focused** | focus_state | **−1** (missed distraction — recall signal) |
| Distraction Ari flagged, user **"off-task / break"** | distraction | **+1** (Ari right — true positive) |
| Distraction Ari flagged, user **"working / on-task"** | distraction | **−1** (Ari wrong — false positive) |
| Overload check **👍 "yes, heavy"** | overload | **+1** |
| Overload check **👎 "no, fine"** | overload | **−1** |

**Why distraction matters most:** the AttentionSignals audit showed 8 distraction kinds were being dropped/misclassified as "focus." These labels directly measure — and later fix — that classification. A `distraction` label validates precision (was a flagged drift real?); a `focus_state` −1 validates recall (did Ari miss a drift while calling it focus?). Together they map the full focus↔distraction boundary, and each also stores which `attention_segment_kind` Ari emitted, so accuracy can be broken down per kind (social media vs comm-monitoring vs off-task, etc.).

---

## 5. Label sources & capture logic

### 5a. Passive — nudge outcomes (wiring, ~2/day, zero UI)
In `SqliteBrainStateStore.AcknowledgeIntervention(ack)`: when `ack.Result ∈ {accepted, dismissed, skipped, snoozed}` **and** the intervention is a user-facing break/recovery nudge (by `action`/`BreakKind`/`DeliverySurface`), also `RecordGroundTruthLabel(...)` using the feature snapshot of the bucket nearest the intervention's timestamp. Delivery-only acks (`applied` on auto actions) write **no** label.

### 5b. Active — sampled one-tap 👍/👎 (widget, ~3–8/day)
A tiny 👍/👎 pill on the existing desktop widget, shown **only** when the service's governor says a prompt is due (§6), at a natural boundary. Non-modal, ignorable, auto-dismisses. On tap it posts to `POST /v1/labels/focus` → `RecordGroundTruthLabel(...)`. If ignored, no label (and it counts toward the cooldown so we don't re-ask immediately).

Prompt copy stays minimal and **neutral — never judgmental** (this is tracking, not surveillance): e.g. *"Focused just now?"* 👍/👎. At the end of a segment Ari flagged as a drift, a rare, tactful *"Working or break just now?"* → **Working** (Ari over-called → distraction −1) / **Break** (Ari right → distraction +1). Optionally a rare *"Feeling the pressure?"* 👍/👎 at overload moments (target = overload). Framing is deliberately non-accusatory so users never feel policed.

---

## 6. The sampling / frequency governor (server-tunable)

Lives in the service (single source of truth), exposed to the widget via `GET /v1/labels/prompt-due`. Rules:

- **Hard caps:** ≤ `MaxActivePromptsPerDay` (default **6**), ≥ `MinMinutesBetweenPrompts` (default **90**).
- **Boundary-only:** fire only at transitions — right after a break overlay completes, on return from an away segment ≥ N min, or an app going idle→active — **never during active typing/focus**.
- **Informative-moment bias:** prefer moments where the label teaches most — mid-confidence (confidence in a middle band) or pressure near a decision threshold. Skip when Ari is already certain.
- **Suppression:** respect meeting shield, pause/DND, cold-start (don't ask before calibration is meaningful), and a per-user opt-out.
- All thresholds are constants in `PortaExecutionConstants` **and** overridable via the runtime policy (like break policy), so we can tune cadence from the server without a release.

Target outcome (measured-volume based): passive ~2/day + sampled ~3–8/day ⇒ **~1,000–1,800 labels / 6 months** — enough to train a simple model, without pulling users into Ari.

---

## 7. Files to touch (concrete)

**Agent core (`porta-agent-windows-x64`):**
- `src/Porta.Agent.Core/Db/Schema.cs` — `ApplyGroundTruthLabelsV20` (table + indexes).
- `src/Porta.Agent.Core/Db/Migrations.cs` — register `Version: 20`.
- `src/Porta.Agent.Core/SqliteBrainStateStore.cs` —
  - `PruneIfNeeded`: leave `ground_truth_labels` **out** of the DELETE list (add a comment so nobody "fixes" it later).
  - Full-reset wipe: **add** `DELETE FROM ground_truth_labels;`.
  - `AcknowledgeIntervention`: write a passive label on user-choice results.
  - New: `RecordGroundTruthLabel(GroundTruthLabel label)`, `ReadLabelDiagnostics()` (counts by day/target).
  - New: `BuildFeatureSnapshot(...)` from the finalized bucket + `OverloadInferenceResult` + `DerivedSessionMetricsResult`.
- `src/Porta.Agent.Core/AgentLocalStoreModels.cs` — `GroundTruthLabel` record, `AgentLabelDiagnostics` record.
- `src/Porta.Agent.Core/IBrainStateStore.cs` / `IAgentCoreController.cs` — new method signatures.
- `src/Porta.Agent.Core/AgentCoreController.cs` — `RecordFocusLabel(...)`, `GetLabelPromptDecision(...)` (the governor), `GetLabelDiagnostics()`.
- `src/Porta.Agent.Core/PortaExecutionConstants.cs` — `LabelCapture` block (caps, min-interval, confidence band, boundary rules).

**Local API:**
- `src/Porta.Agent.LocalApi/PortaApiContracts.cs` — `LocalApiFocusLabelRequest`, `LocalApiLabelPromptDueResponse`, `LocalApiLabelDiagnosticsResponse`.
- `src/Porta.Agent.LocalApi/LocalApiEndpointMapping.cs` — `POST /v1/labels/focus`, `GET /v1/labels/prompt-due`, `GET /v1/labels/diagnostics`.
- `src/Porta.Agent.LocalApi/LocalApiClient.cs` — client methods for the widget.

**User agent (widget):**
- `src/Porta.UserAgent/Interventions/UserDesktopWidgetWindow.cs` + presenter — the 👍/👎 pill: poll `prompt-due` at boundaries, render, post to `/v1/labels/focus`, honor cooldown.

**Tests (`Porta.Agent.Core.Tests`):**
- Migration/schema test: table + indexes exist; **prune leaves it intact**, full-reset clears it.
- `AcknowledgeIntervention` writes a correctly-valued, feature-complete label for accepted/dismissed/skipped/snoozed; writes nothing for auto-applied acks.
- Governor: respects daily cap, min-interval, boundary-only, suppression; picks informative moments.
- Label normalization table (§4) is exact.
- Round-trip: a label survives a simulated `PruneIfNeeded` cycle.

---

## 8. Implementation sequence (each step independently safe & shippable)

1. **Store foundation** — table + migration + prune exemption + `RecordGroundTruthLabel` + diagnostics read. No behavior change. (Verifiable: table exists, survives prune.)
2. **Passive capture** — wire `AcknowledgeIntervention`. Labels begin accruing (~2/day) with zero UI. (Verifiable: a dismissed break creates a −1 break_need row.)
3. **Governor + endpoints + widget one-tap** — the sampled 👍/👎. (Verifiable: prompt appears only at boundaries within caps; a tap creates a focus_state row.)
4. **Diagnostics readout** — a "labels: N today / M this week, by target" line in Control Center / local API, so you can *see* the table filling. This is the literal "done when accumulating."

---

## 9. Explicitly deferred (not in 1b)

- **Server upload of labels** — for cross-wipe durability + fleet-wide counts. Needs a durable table + a consent/privacy review (behavioral features leaving the device). Design later; local prune-exempt is the durable baseline for the beta.
- **Control Center history beyond 2 weeks** — separate item, solved by reading the server's never-pruned `daily_stats` for long ranges (no local growth). Related principle, own task.
- **Conversational / 3-way tiers** — not needed at measured volumes; would over-involve users. Revisit only if volume proves too thin.
- **Fixing the 14-vs-90-day disclosure string** — small honesty fix, bundle with a UI pass.

---

## 10. Safety summary

Additive only. The 14-day raw prune (machine protection) is unchanged. The pressure model, nudge logic, and actuation are untouched. Labels are tiny, local, privacy-equivalent to existing `state_buckets`, and opt-out-able. The worst realistic failure is **no labels captured** — never a freeze, crash, or behavioral regression.

---

## 11. v2 — Moment selection (shipped 2026-07-07)

### What v1 got wrong (observed on the live fleet)

§6 promised "boundary-only" prompting, but the boundary gating was never implemented. The widget polled `prompt-due` on a flat 60s timer and the governor's only gates were caps, cooldown, calibration stage, and the confidence band. Three consequences, all confirmed in code:

1. **Every prompt landed right after login.** Overnight the 90-min cooldown expired and the daily cap reset, so the first eligible poll tick of the day — minutes after boot — always fired. "Were you focused just now?" at that moment labels an away/boot window: a garbage training example, and an annoying one.
2. **The distraction question could never fire.** `ChooseTarget` required `DominantState` to be a distraction state, but the v2 anchor engine never emits those states (the dead off-task gate — see Phase 2f). Structurally unreachable for every user.
3. **The overload question almost never fired.** It required pressure ≥ 0.50 at the exact polling instant (prompts fired at low-pressure login moments), and genuinely high-pressure moments often push confidence outside the 0.35–0.80 informative band — which suppressed the prompt precisely when it mattered. `focus_state` was the permanent fallback winner.

Bonus bug: the widget called `prompt-due` with `meetingInProgress: false` hardcoded, so meeting suppression was dead on the active-prompt path.

### What v2 does instead

The governor stays pure; it gains **moment inputs** and each question now waits for *its* moment (priority top to bottom):

| Question | Trigger (the moment) | Band? | Warm-up? |
|---|---|---|---|
| Distraction — "Working, or taking a break?" (`away_return`) | Return from an away segment of 5–120 min that interrupted ≥10 min of real activity; asked within 3 min of the return | exempt | exempt (asks about the *past* segment) |
| Overload — "Are you feeling overwhelmed?" (`overload_now`) | Pressure **crossed** the 0.50 threshold between polls (latched up to 20 min, consumed on ask, dropped if an away starts) | exempt | required |
| Distraction — "on task now?" (`on_task_now`) | Live distraction `DominantState` (rare until 2f lands; kept so it lights up then) | in band | required |
| Focus — "Were you focused just now?" (`focused_now`) | ≥10 min sustained attention AND a natural input lull (8–180s since last input) | in band | required |

**Warm-up gate**: no "just now" question until 10 min of activity since the last resume — this alone kills the login prompt. Unknown resume time = not warmed up (safe default). Caps, 90-min cooldown, progressive back-off, pause/consent/cold-start suppression: all unchanged from v1.

**Moment tracking** (controller, fed by the live input stream): away segments are detected as gaps ≥5 min between consecutive *distinct* input timestamps; a fresh service start deliberately produces no away-return event, so boot/login can never trigger the away question. A login/overnight gap also fails the ≤120 min and ≥10-min-prior-activity guards — three independent reasons the morning prompt is dead.

**Meeting fix**: suppression is now computed server-side from the live foreground app (`AppCategoryClassifier.IsMeetingApp(FocusAppHash)`); the widget's flag is advisory-only.

**Label stamping**: answered labels get `prompt_trigger` + `seconds_since_resume` merged into `features_json`, so Phase 6 training can weight/filter by moment quality — and so we can audit that prompts stopped landing at session start.

### Files touched

`PortaExecutionConstants.cs` (LabelCapture moment tunables) · `Analysis/LabelPromptGovernor.cs` (v2 rework) · `AgentCoreController.cs` (moment tracker, crossing latch, meeting truth, stamping) · `UserAgentHost.cs` (comment only) · `LabelPromptGovernorTests.cs` (30 tests). No schema change, no contract change, no widget UI change. Full suite: **507 passed / 0 failed** (2026-07-07).

### Interim honesty note

Until 2f/2d land, the away-return "Working, or taking a break?" labels teach the **away classification**, not true off-task detection — that's the consent-safe ceiling today, and still useful training data. The state-based distraction trigger is wired and will start firing the day 2f makes those states reachable.
