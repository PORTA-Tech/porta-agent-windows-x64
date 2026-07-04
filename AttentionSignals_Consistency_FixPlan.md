# Attention Signals Consistency — Audit & Fix Plan

**Scope:** Make the Agent and the Control Center agree on all four signal types — **distraction, focus, overload, away** — across the live widget, the Day view, and the Week / 2‑Week history views.

**Status:** Investigation complete. No code changed yet. This document is the implementation plan.

---

## 1. Verdict

This is a real, calibration‑independent defect, and it is not one bug — it is a family of bugs caused by **one root design flaw**: the **agent** that *produces* attention‑segment kinds and the **read/display side** that *consumes* them do not share a single definition of what those kinds are. They drifted apart, so newer kinds the agent emits are silently dropped or mis‑classified on the way to the Control Center.

It affects every machine regardless of calibration stage, trust tier, or active days. Calibration has nothing to do with it (the attention‑segment engine runs independent of `gating.ColdStart`). My earlier "it's cold‑start, it will appear once calibrated" explanation was wrong.

The reason the **widget shows distraction but the Control Center does not** is the smoking gun: they use two different code paths over the same data (detailed in Bug #1 / #3 below).

---

## 2. The data pipeline (ground truth)

```
AGENT (Porta.Agent.Core)
  DerivedSessionMetricsComputer.EvaluateAttentionSegment()
    → emits attention_segment_kind (one of 17 strings) + distraction_episode_active + task_continuity_seconds
  SqliteBrainStateStore.BuildStateBucketStateFlagsJson()   [line 4257]
    → serializes them verbatim into state_buckets.state_flags_json   (kind stored as-is, lowercased)

READ (Porta.Agent.LocalApi)
  SqliteBrainStateStore.ReadTimeline()                     [line 1315]
    → for buckets > 5s, picks ONE raw 5s bucket per group (MAX(id)) — a SAMPLE, not an aggregate
  SnapshotMapper.Map()                                     [line 11]
    → LocalApiCurrentSnapshotResponse, via LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind()

DISPLAY (Porta.ControlCenter)
  HistoryTimelineEpisodeBuilder.Build()  → episode bars (focus/distraction/away/overload)
  HistoryFocusHeatmapBuilder.Build()     → day/week/2-week cells
  HistoryPageInsightsBuilder.Build()     → summary numbers
WIDGET (Porta.UserAgent)
  UserDesktopWidgetStateBuilder          → live distraction badge
```

Key fact: the raw kind string **is** correctly written to the database. Nothing is lost at write time. Everything below is a **read/display** defect, which means **historical data is mostly recoverable** once the read side is fixed.

---

## 3. The taxonomy drift (the core problem)

The agent (`DerivedSessionMetricsComputer.cs`) can emit **17** distinct `attention_segment_kind` values. The read side (`LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind`, line 221) recognizes only **9**. Any unrecognized kind hits the `_ => FocusSegmentKind` default and **silently becomes "focus."**

| # | Kind emitted by agent | Source (DerivedSessionMetricsComputer) | Recognized on read? |
|---|---|---|---|
| 1 | `focus` | steady state | ✅ |
| 2 | `away` | away gate (line ~884) | ✅ |
| 3 | `context_excursion` | line ~922 | ✅ |
| 4 | `off_task_distraction` | line ~922 | ✅ |
| 5 | `fragmentation_distraction` | line ~965 | ✅ |
| 6 | `rolling_micro_burst` | Pattern 2, line ~1039 | ✅ |
| 7 | `return_loop_distraction` | Pattern 3, line ~1101 | ✅ |
| 8 | `unfocused_session` | Pattern 4, line ~1126 | ✅ |
| 9 | `shallow_dwell_distraction` | Pattern 5, line ~1161 | ✅ |
| 10 | `focus_departure_distraction` | Pattern 8, line ~1362 | ✅ |
| 11 | `social_media_distraction` | Pattern 9, line ~745 | ❌ → "focus" |
| 12 | `gaming_launcher_distraction` | Pattern 10, line ~778 | ❌ → "focus" |
| 13 | `active_gaming_distraction` | Pattern 11, line ~811 | ❌ → "focus" |
| 14 | `social_media_loop` | Pattern 12, line ~844 | ❌ → "focus" |
| 15 | `passive_consumption` | Pattern 1, line ~1227 | ❌ → "focus" |
| 16 | `comm_monitoring_loop` | Pattern 7, line ~1253 | ❌ → "focus" |
| 17 | `focus_category_absent` | Pattern 13, line ~1279 | ❌ → "focus" |
| 18 | `bad_session_start` | Pattern 14, line ~1312 | ❌ → "focus" |

The 8 dropped kinds (#11–18) are exactly the **most common real‑world distractions**: social media, YouTube/video (passive consumption), Slack/Teams/email checking (comm monitoring), "no productive app open" (focus category absent), gaming. The 9 recognized kinds are niche rapid‑switching patterns. So in practice, for a normal user, distraction almost never renders in history.

There is an in‑code warning that proves this is unfinished wiring, not intent — `LocalAttentionSegmentAnalytics.cs` line 228:

> `// Block F — new kinds must be recognised; without this they silently become "focus"`

Someone hit this once for the "Block F" kinds and fixed those nine. The later category/Phase‑3 kinds were added to the agent and never registered on the read side.

**Root architectural cause:** the agent emits **raw string literals** (Porta.Agent.Core); the read side keeps its **own constant list** (Porta.Agent.LocalApi). There is no shared source of truth, so they drift every time a kind is added.

---

## 4. Complete bug inventory

### Bug #1 — Read‑side normalizer drops 8 kinds (PRIMARY)
- **Where:** `LocalAttentionSegmentAnalytics.NormalizeAttentionSegmentKind` (LocalApi, line 221).
- **Effect:** Kinds #11–18 collapse to "focus." History distraction bars never render for them.
- **User sees:** "no distraction in Day/Week/2‑Week."
- **Severity:** Critical.

### Bug #2 — `IsDistractionKind` drops the same 8 kinds, mis‑credits them as focus
- **Where:** `IsDistractionKind` (LocalApi, line 71); consumed by `ResolveStoredFocusEpisodeSeconds` (SnapshotMapper, line 527).
- **Effect:** A social‑media/passive bucket is not seen as distraction here, so `FocusEpisodeSeconds` is set to `task_continuity_seconds` → the bucket can be **counted as focus time**. Distraction is not just hidden, it is mislabeled as focus.
- **Severity:** High (corrupts focus totals too).

### Bug #3 — Split‑brain: summary + widget count distraction, but history bars don't
- **Where:**
  - `IsDistractionEpisode` (LocalApi, line 47) checks the `distraction_episode_active` **boolean first** → inclusive, catches all 17 kinds. Used by `HistoryPageInsightsBuilder` (summary seconds) and `UserDesktopWidgetStateBuilder` (line 205, the widget badge).
  - History **bars** go through `ResolveAttentionSegmentKind` (kind‑based) → exclusive, drops 8.
- **Effect:** On the *same* Control Center page, the summary "distraction seconds" number counts an episode that the visual timeline doesn't draw. The **widget shows distraction the Control Center history hides** — exactly your reported symptom.
- **Severity:** Critical (self‑contradictory UI).

### Bug #4 — Episode enum + heatmap switch have no slot for the 8 kinds
- **Where:** `HistoryTimelineEpisodeKind` enum (HistoryTimelineEpisodes.cs, lines 5–19); `FlushAttentionRun` mapping (line 173, unknown → `ContextExcursion`); `HistoryFocusHeatmap` `DistractionEpisodeCount` (lines 50–58) and the seconds switch (lines 159–166).
- **Effect:** Even if Bug #1 were fixed, there is no enum member or display label/colour for the 8 kinds, and the unknown‑mapping would mislabel them as "context excursion." Must be fixed together with #1.
- **Severity:** High (blocks the #1 fix from fully working).

### Bug #5 — Reason‑code/text tables only cover a subset of kinds
- **Where:** `ResolveAttentionSegmentReasonCode/Text` (LocalApi, lines 110–169); `SnapshotMapper` fallback text (lines 68–78).
- **Effect:** Recognized‑but‑newer kinds (and the 8 once added) render generic/"Steady focus" reason text in tooltips, so episode detail is wrong/empty.
- **Severity:** Medium (cosmetic but misleading).

### Bug #6 — Two different definitions of "focus" on the same page
- **Where:** `HistoryFocusHeatmapBuilder` — cell colour/`FocusedSeconds` come from `focus_strength` (line 181–182), while focus **bars** come from `FocusEpisodeSeconds`/`task_continuity` (episodes). `HistoryPageInsightsBuilder.FocusedSeconds` also uses `focus_strength` (line 53–54).
- **Effect:** A cell can be coloured "focused" while showing zero focus‑episode bars, and vice‑versa. This is part of why "focus episodes don't show" even when the cell looks focused. Needs a deliberate decision: which is the canonical "focus" for each surface, then make them consistent.
- **Severity:** Medium–High (confusing, and interacts with Bug #2).

### Bug #7 — Coarse‑bucket reduction samples one raw bucket and loses short episodes
- **Where:** `ReadTimeline` (line 1336–1376) uses `INNER JOIN … MAX(id) … GROUP BY id/$stride` → it keeps the **last 5s raw bucket** in each 300s/600s group, not an aggregate. Binary flags (`overload_active`, `distraction_episode_active`, away, focus‑episode) and short distraction/focus episodes that don't land on the sampled bucket are dropped in Week/2‑Week.
- **Evidence it's already known:** `PopulateTimeline` (MainWindow, lines 7021–7039) hand‑patches this **for overload only** by overlaying the finer Week/Today timelines onto the 2‑Week heatmap. Distraction, focus, and away get **no** such compensation.
- **Effect:** Week/2‑Week under‑report distraction/focus/away even for recognized kinds; today's column was special‑cased to match Day view.
- **Severity:** High (structural accuracy problem; the overlay hacks are fragile).

### Bug #8 — No regression guard
- **Where:** Tests exist (`HistoryPageInsightsBuilderTests`, `HistoryFocusHeatmapBuilderTests`, `DerivedSessionMetricsComputerTests`, `StateBucketJsonContractsTests`) but none assert that **every kind the agent can emit is recognized by the read side.** That's why this regressed twice.
- **Severity:** High (process gap — without it, this returns).

### Status by signal
- **Distraction:** broken (Bugs #1–#5, #7). Worst affected.
- **Focus:** partially broken (Bugs #2, #6, #7) — dual definition + mis‑credit + sampling loss.
- **Overload:** mostly working but propped up by manual overlays (Bug #7); binary‑flag loss is the underlying issue.
- **Away:** mostly working (recognized end‑to‑end), but subject to the same sampling loss in Week/2‑Week (Bug #7).

---

## 5. Fix plan (phased)

### Phase 0 — Single source of truth (foundation)
1. Create `AttentionSegmentKinds` in **`Porta.Agent.Common`** (the assembly every project references): the canonical 18‑constant list plus classifier helpers `IsDistraction(kind)`, `IsAway(kind)`, `IsContextExcursion(kind)`, `IsFocus(kind)`, and `NormalizeKind(kind)`.
2. Make the agent (`DerivedSessionMetricsComputer`) emit these constants instead of raw string literals.
3. Make `LocalAttentionSegmentAnalytics` delegate to the Common classifier instead of its own private list.
   - *Outcome:* one place defines kinds; drift becomes impossible by construction.

### Phase 1 — Close the normalizer gap (fixes Bugs #1, #2)
4. Add all 8 missing kinds to the canonical list and to `IsDistraction`/`IsDistractionKind`.
5. Reconcile `ResolveStoredFocusEpisodeSeconds` (SnapshotMapper) to use the same inclusive distraction predicate so distraction buckets are never counted as focus.

### Phase 2 — Make history bars render every kind (fixes Bugs #3, #4)
6. Decide the display taxonomy for the 8 kinds: either add dedicated `HistoryTimelineEpisodeKind` members (distinct labels/colours) or fold them into a small set of display buckets (e.g., "Off‑task," "App‑switching churn," "Passive/consumption"). Recommend grouping for UI simplicity, keeping the precise kind in the tooltip.
7. Update `FlushAttentionRun` mapping, the heatmap `DistractionEpisodeCount`, and the seconds switch to cover them.
8. Unify the predicate: history bars, summary, and widget should all classify distraction the **same way**. Standardize on `IsDistractionEpisode` (boolean‑first, then kind) so the live `distraction_episode_active` flag and the kind can never disagree.

### Phase 3 — Reason text/labels (fixes Bug #5)
9. Extend reason‑code/text tables (LocalApi + SnapshotMapper fallback) to cover all kinds so tooltips read correctly.

### Phase 4 — Define "focus" consistently (fixes Bug #6)
10. Decide per surface: heatmap cell intensity and summary "focused seconds" should use the same focus definition as the focus **bars**. Recommend: drive both from focus **episodes** (`task_continuity`) for "time in focus," and keep `focus_strength` only as a secondary intensity/quality shading — clearly separated, not conflated.

### Phase 5 — Fix coarse‑bucket reduction (fixes Bug #7, retires the overlay hacks)
11. Change `ReadTimeline`'s reduction so binary/categorical signals are **aggregated**, not sampled: per output bucket, OR‑combine `overload_active`/`distraction_episode_active`/away, and pick the **most‑severe** attention kind in the group (and max `task_continuity`) rather than the last raw row. This makes Week/2‑Week accurate for **all four** signals and lets the Week/Today/2‑Week overlay patches in `PopulateTimeline` be simplified or removed.
    - *Note:* keep the existing performance constraint (do the reduction in SQL; don't load all rows into memory).

### Phase 6 — Regression guard (fixes Bug #8)
12. Add a contract test that enumerates the canonical kind list and asserts: (a) every kind is recognized by `NormalizeKind`, (b) every distraction kind is caught by `IsDistractionEpisode`, (c) every kind has an episode‑enum mapping and a reason text. With the Phase‑0 single list, this test fails the build the moment a new kind is added without wiring.
13. Add an end‑to‑end test: synthesize a `state_buckets` row for each of the 8 previously‑dropped kinds → assert it renders as a distraction episode in `HistoryFocusHeatmapBuilder` and is counted by the summary.

### Phase 7 — Backfill / data note
14. Historical buckets already store the true kind string in `state_flags_json`, so after Phases 1–2 **past distraction reappears automatically** — no re‑collection needed — except where Phase‑5's reduction loss already discarded it in Week/2‑Week (those recover going forward once Phase 5 ships).
15. No DB migration or recalibration is required. No agent‑side data change is required for the primary fix (Phases 1–4 are read/display only); Phase 0 and Phase 5 touch the agent and the store but are backward compatible.

---

## 6. Suggested sequencing

1. **Phase 0 + Phase 1 + Phase 2 + Phase 6** ship together as the first PR — this is the user‑visible fix (distraction reappears in history, widget and console agree) plus the guard that stops it regressing. Read/display only + shared constants; low risk.
2. **Phase 3 + Phase 4** second PR — labels and focus‑definition consistency.
3. **Phase 5** third PR — the reduction rework; higher‑risk (touches a hot SQL path and the overlay logic), so isolate it and lean on the Phase‑6 tests.

---

## 7. Files in scope

| File | Project | Role |
|---|---|---|
| `Analysis/DerivedSessionMetricsComputer.cs` | Core | emits kinds (→ use shared constants) |
| `SqliteBrainStateStore.cs` (`BuildStateBucketStateFlagsJson`, `ReadTimeline`) | Core | write verbatim; coarse reduction (Phase 5) |
| `LocalAttentionSegmentAnalytics.cs` | LocalApi | normalizer + predicates + reason text |
| `Mappers/SnapshotMapper.cs` | LocalApi | flag → response mapping; `ResolveStoredFocusEpisodeSeconds` |
| `HistoryTimelineEpisodes.cs` | ControlCenter | episode enum + builder |
| `HistoryFocusHeatmap.cs` | ControlCenter | day/week/2‑week cells + counts |
| `HistoryPageInsights.cs` | ControlCenter | summary numbers |
| `MainWindow.xaml.cs` (`PopulateTimeline`, overlays) | ControlCenter | overlay hacks (simplify after Phase 5) |
| `UserDesktopWidgetStateBuilder.cs` | UserAgent | widget badge (align predicate) |
| **NEW** `AttentionSegmentKinds.cs` | **Common** | canonical taxonomy + classifiers |
| Tests (`*HistoryFocusHeatmap*`, `*HistoryPageInsights*`, `StateBucketJsonContracts*`) | Tests | add coverage + contract guard |

---

*Prepared from a direct read of the source. No code modified.*
