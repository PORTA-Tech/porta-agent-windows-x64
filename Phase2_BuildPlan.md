# Phase 2 — "Love it" features: Build Plan (v1.1)

Status: approved direction, grounded in the current codebase (post-1b-v2, 1.2.107). Goal: the
last feature phase before charging — widget feels alive, meetings are measured, reminders exist,
and the category/off-task accuracy stack finally works end to end.

Decisions locked: LLM categorizer uses **Claude Haiku via the Anthropic API** (server-side on the
control plane); reminders are created via **quick-add on the widget**; work runs in **two parallel
tracks**, each item independently shippable.

v1.1 revisions (from design review): 2a is a single continuous **EKG-style pulse line** (not
activity bars) — spike HEIGHT is the primary intensity channel, beat rate secondary, with a
per-state rhythm signature; the widget's existing design and palette are **inherited unchanged**
(additive-only UI). 2c reminder mechanics fully specified: time-based only, three entry points,
T-60 pill → T-0 toast → Windows-notification escalation, missed-reminder recovery, meeting-shield
sound suppression.

---

## 0. Grounding facts (verified in code, not assumed)

- The widget is fully model-driven: `UserDesktopWidgetState` (Title, StatusText, 3 metrics,
  FooterText, LabelPrompt pill, SignupNudge toast) built by `UserDesktopWidgetStateBuilder`,
  rendered by `UserDesktopWidgetWindow` (WPF, 2.6k lines). Live session state (keyboard/mouse
  active, focus app) already reaches the widget every ~2–4s — the "alive" signal exists, it is
  just not rendered.
- `AppCategoryClassifier` is a hand-curated exe-name switch + in-process hash→category /
  hash→label / hash→meeting registries. `BrowserTitleClassifier` is a curated title-keyword
  table; unrecognized titles → token `"web"`, category Unknown (neutral).
- Browser capture (`UserSessionNativeSignals.ResolveForegroundFocusHash`) reads the window TITLE
  only (privacy-gated by `PrivacySettingsStore.ReadBrowserTitlesEnabled()`), folds a site token
  into the identity hash. Titles do not carry the URL → every unbranded page collapses to
  `web`/Unknown. This is the 2e blind spot, confirmed.
- The 2f dead gate, confirmed at `DerivedSessionMetricsComputer` (~line 913): the off-task path
  requires `continuity.ReasonCode ∈ {context_excursion, context_excursion_decay,
  context_shift_reset}` — codes the v2 anchor-migration engine never emits. BUT the anchor engine
  already tracks exactly what we need: `CandidateAnchorAccumulatedSeconds` is live off-anchor
  dwell time on a specific non-anchor app (`AnchorMigrationThresholdSeconds = 240`).
- The 2g suppression already exists at ~line 1090: ping-pong is suppressed when
  `AppCategoryClassifier.IsFocusPositive(GetCategoryForHash(h))` on either side — it just never
  fires because categories are mostly Unknown. 2d/2e feed it; only the unknown↔unknown case
  needs a code change.
- Meeting detection exists and is registry-based (`RegisterHashMeeting` for Zoom/Teams/Webex exes
  + meet-site tokens). The meeting shield holds break nudges; 1b-v2 made it suppress label
  prompts server-side. What 2b adds is *measurement*, not detection.

---

## Track A — visible love (widget)

### 2a. Widget pulse / "alive" — EKG heartbeat line

**What**: ONE continuous heart-monitor-style waveform, scrolling right-to-left, drawn in a thin
(~14px) strip inserted between the status line and the metrics row — plus a subtle synchronized
opacity animation on the mode dot that already exists. Nothing else about the widget changes:
same layout, same sizes, same background `#111118`, same border `#252535`, same fonts. Additive
only — if the pulse strip were removed, the widget is pixel-identical to today.

**The signal model** (order matters — this was the design review's key point):

- **Spike height = work intensity (PRIMARY).** Intensity is a smoothed 0–1 blend of signals the
  agent already computes: `TypingBurstIntensity`, `ScrollVelocityIndex`, `FocusSwitchRate`
  (bucket-level, via the snapshot), refreshed between buckets by the live
  `keyboard_active`/`mouse_active` booleans the widget already receives every ~2–4s. Furious
  typing / fast scrolling / rapid window-switching → tall spikes; trickle activity → short blips.
- **Beat rate = intensity (SECONDARY).** Rate rises with intensity but over a narrower range —
  height is what the eye reads first.
- **State controls color AND rhythm signature** (state always wins over raw intensity):

  | Mode | Color (existing widget palette) | Rhythm |
  |---|---|---|
  | Monitoring/Focus | `#16C784` (existing accent green) | steady, regular beats |
  | DeepWork | existing DeepWork blue (`35,82,112` family) | steady, slightly slower, tall |
  | Distraction | existing distraction accent | **irregular** beat gaps (arrhythmic) |
  | Overload | existing Overload amber (`114,77,32` family) | fast, tall, slight baseline tremor |
  | Away | existing Paused slate | near-flatline, rare tiny blip |
  | Paused / Limited / stale | slate | **frozen** — no animation at all |

  A dead line must MEAN "not watching"; the line never fakes life.

**How**: WPF — a `Polyline`/`StreamGeometry` redrawn by a `DispatcherTimer` (~150ms tick,
~60–90 point ring buffer in the presenter; beat shape is a small pure function — P-QRS-T-like
piecewise curve — unit-testable). Colors resolved from the SAME mode→brush mapping the widget
already uses (the `ApplyState` accent switch) — no new palette constants. Honors
`AppearancePreferenceStore` reduced-motion: static thin line at last intensity, no animation.
No new API, no service change, no new data leaves the device.

**Files**: `UserDesktopWidgetWindow.cs` (strip render + dot sync), `UserDesktopWidgetPresenter.cs`
(intensity smoothing + ring buffer), `UserDesktopWidgetModels.cs` (add `LivePulse` sample state),
`UserAgentHost.cs` (feed live-session + snapshot intensity), new `PulseWaveform.cs` (pure beat
shape + rhythm logic, +tests).

**Done when**: spikes visibly grow within ~5s of hard typing and shrink at rest; distraction
mode beats irregularly; away is near-flat; paused/stale is frozen; widget layout is unchanged
except the added strip; widget CPU stays <1% (measure before/after); reduced-motion fallback
verified.

### 2c. Reminder pill + quick-add — full mechanics

**What it is based on**: ONLY the time the user sets. A plain alarm clock attached to the widget
— no calendar, no sync, no recurrence, no AI guessing, no activity triggers in Phase 2. This is
the free precursor to the paid calendar feature (Phase 5).

**Entry points (three, all opening the same quick-add input)**:

1. The "+" button on the widget metrics row (primary path — see mockup).
2. A new "Add reminder" item in the widget's existing context menu (alongside Pause / Settings).
3. The tray icon menu.

**Creating**: one-line input, natural grammar, deliberately minimal: `in 20m`, `in 2h`,
`at 16:30`, `tomorrow 9am`; remaining text = the title. A live parse preview ("Set for 16:30")
renders before confirming, so what was scheduled is never ambiguous. Enter confirms, Esc closes.
Parsing happens in the widget (`ReminderTimeParser`, pure class + tests); the API receives only
structured data (title + dueAtUtc).

**How it reminds — escalation chain**:

| Moment | Behavior |
|---|---|
| T-60 min | Amber pill appears on the widget: title + live countdown ("in 15m"). Passive, ignorable. Only the NEXT upcoming reminder is shown. |
| T-0 | Pill becomes a pulsing toast (existing widget palette, amber border pulse): actions **Done** and **+10m**. |
| Widget hidden / user agent not running | Standard Windows notification fires instead — a reminder can never be silently lost. |
| PC asleep/off at due time | Fires immediately on wake, tagged "missed by Xm". |
| Toast ignored | Stays visible until acted on. No re-nag, no repeat sound. |

**Edge behaviors (decided)**:

- **+10m** is repeatable without limit; each press moves dueAt +10 min and returns to countdown.
- **Meetings**: reminders DO fire during meetings (the user set them explicitly) — but the
  Windows notification **sound** is suppressed while the meeting shield is active; visual only.
- **Cap**: ~20 pending reminders max (keeps the "next pill" logic simple).
- **Privacy**: reminder text is user-authored and stays local — stored in the service DB, never
  uploaded, wiped by the full data-reset like everything else.

**Storage & plumbing** (reminders live in the SERVICE — they must survive user-agent restarts,
widget crashes, and agent updates):

1. Store: new table `reminders` (id, title, due_at_utc, created_at_utc, state:
   pending|fired|done|dismissed, snooze_count, fired_at_utc) — migration **V21**; done/dismissed
   rows pruned after 30 days (the table is in the prune list, unlike ground_truth_labels).
2. Local API: `GET /v1/reminders/next` (next pending + fired-unacknowledged),
   `POST /v1/reminders`, `POST /v1/reminders/{id}/done|snooze|dismiss`.
3. Service marks pending→fired on a ~30s check; the widget polls next-reminder on its existing
   refresh cadence and renders pill/toast; Windows-notification fallback goes through the
   existing `UserSessionWindowsNotificationController`.

**Files**: `Schema.cs`/`Migrations.cs` (V21), `SqliteBrainStateStore.cs` (CRUD + prune entry),
`AgentCoreController.cs` + `IAgentCoreController.cs`, `LocalApiEndpointMapping.cs`/contracts/
client, `UserDesktopWidgetWindow.cs`/presenter/models (pill, toast, quick-add popover, menu
item), tray menu, new `ReminderTimeParser.cs` (+tests).

**Done when**: create from all three entry points → pill shows countdown at T-60 → toast fires
within 30s of due time with working Done/+10m → survives service restart → missed-while-off
fires on wake with the "missed" tag → Windows notification covers the widget-hidden case →
parser handles the four grammar forms + garbage input gracefully.

### 2b. Meeting measurement

**What**: when a meeting app/site is foreground, low input is **attentive-in-meeting**, not
idle/away/distraction (mockup: "Meeting mode"). Meetings become a measured, first-class segment.

**How**:

1. **Bucket flag**: the finalizer already computes `meetingInProgress` from the dominant hash —
   persist it per bucket (`state_buckets.meeting_flag`, migration piggybacks on V21).
2. **Segment attribution** (`DerivedSessionMetricsComputer`): before the away/idle gates, if the
   bucket's dominant app is a meeting app and idle ≥ threshold, emit a new segment kind
   `AttentionSegmentKinds.InMeeting` (engaged, never distraction, doesn't break the focus
   episode, doesn't accumulate toward `BreakNeed`). Interaction while in a meeting app counts as
   normal focus.
3. **Widget mode**: new `UserDesktopWidgetMode.InMeeting` — teal dot with a slow calm pulse,
   status "Teams foreground · counting as engaged · nudges held", a meeting-time chip, metrics
   show Meetings today / Focus.
4. **Digest/console**: meeting minutes become a real category in the daily rollup ("Meetings
   1h 05m") instead of vanishing into idle.

**The honest ceiling (ship this copy)**: without mic/camera — which would break the consent
model — Ari cannot tell "listening" from "walked away during a call". Disclose in the digest:
meeting time = "meeting app foreground". First-launch tooltip on the meeting chip says the same.

**Files**: `SqliteBrainStateStore.cs` (flag persist), `DerivedSessionMetricsComputer.cs` (+tests),
`AttentionSegmentKinds`, widget state builder/models/window, digest rollup mapper.

**Done when**: a 30-min Teams call with hands off the keyboard produces ~30 min InMeeting (not
Away), zero break nudges, zero label prompts, a meeting chip on the widget, and a "Meetings"
line in the digest. Tests cover: meeting+idle → InMeeting; meeting+typing → focus continues;
non-meeting+idle → Away unchanged.

---

## Track B — accuracy backbone

Order is strict: **2e → 2d → 2f → 2g**. Each ships alone; 2f/2g stay dark until 2d data exists.

### 2e. Browser site/domain capture

**What**: capture the foreground browser's **domain** (coarse token), so unbranded pages resolve
to `nytimes` / `stackoverflow` / their eTLD+1 — never `web`.

**How**: UI Automation (UIA), the consent-safe standard approach — no extension, no history
access:

1. In `UserSessionNativeSignals`, when the foreground exe is a browser and title-reading is
   enabled: resolve the address bar via UIA (`AutomationElement` → `UIA_EditControlTypeId`,
   cached per hwnd; Chrome/Edge/Firefox/Brave/Opera/Vivaldi all expose it), read its value
   **in memory**, extract host → registrable domain (eTLD+1, small embedded public-suffix list
   for common TLDs), lowercase token = domain minus suffix ("news.ycombinator.com" →
   "ycombinator").
2. Precedence: title-rule match (curated, has category) → UIA domain token (category Unknown
   until 2d fills it) → `web`. The token feeds the existing `siteHash` fold — downstream needs
   zero changes.
3. Performance: UIA lookup only on foreground-window or title change, cached element per hwnd,
   50ms timeout, silent fallback to title-only. Failure can never break capture.
4. **Privacy contract (hard)**: only the coarse domain token leaves the method — never the full
   URL, path, query, or title. Same `ReadBrowserTitlesEnabled` consent gate covers it (it is
   less data than the title we already read). Add a privacy test asserting no `/` or `?` can
   survive into a token.

**Files**: `UserSessionNativeSignals.cs`, new `BrowserDomainResolver.cs` (+public-suffix data),
`BrowserTitleClassifier.cs` (precedence), tests for domain extraction + privacy.

**Done when** (their acceptance): youtube/reddit/news/github resolve to distinct token+category;
an unbranded page resolves to its domain token, not `web`; `web` share of browser time drops to
near zero on your own machine within a day.

### 2d. LLM app-categorizer (Claude Haiku, server-side)

**What**: any token the curated lists don't know gets classified ONCE by Haiku on the control
plane, cached forever, and pushed back to the fleet. Kills the "claude was Unknown" class
permanently.

**How**:

1. **Control plane** (Node): table `app_category_labels` (token PK, category, source:
   curated|llm|override, model_ver, created_at). Endpoint `POST /v1/categories/resolve`
   (agent-authed): body = list of unknown tokens; returns known mappings immediately, queues
   never-seen tokens. A worker batches queued tokens → one Anthropic API call (haiku, temperature
   0, strict JSON out) → upsert. Prompt classifies into exactly the 8 existing `AppCategory`
   values, "Unknown" allowed on low confidence. Add `ANTHROPIC_API_KEY` to Render. Ops console
   gets a read-only "Categories" table with a manual-override column (source=override beats llm).
2. **Agent**: `AppCategoryClassifier` gains a persisted overlay (`category-overrides.v1.json` in
   ProgramData, loaded at startup, hot-reloaded). Sync piggybacks the existing heartbeat channel:
   agent sends unknown tokens it has seen (coarse tokens only — already the privacy class that
   leaves the device via hash-label upload), receives mappings, persists. Precedence: server
   override > curated list > LLM > Unknown.
3. **Cost control**: cache-first, batch ≤50 tokens/call, daily API budget cap, dedupe fleet-wide
   (one call per token EVER, not per machine).

**Files**: control-plane repo (table, endpoint, worker, console page); agent:
`AppCategoryClassifier.cs` (overlay), heartbeat/fleet client sync, tests (precedence, overlay
persistence, malformed-response resilience).

**Done when**: a never-seen token ("linear", "obsidian", a domain from 2e) is Unknown at first
sight, categorized within one sync cycle, stays categorized after restart, cost stays at
fractions of a cent per new token; console shows the table filling.

### 2f. Category-aware off-task detection (revive the dead gate)

**What**: "you left your work for something else" finally fires — but only when it should.

**How**: bypass the unreachable reason codes; gate on what the anchor engine already measures:

1. In the continuity decision, surface **off-anchor dwell**: `offAnchorSeconds =
   CandidateAnchorAccumulatedSeconds` and the candidate hash (the non-anchor app being used).
2. New gate in `ComputeAttentionSegmentDecision` (before instability), active only when the
   anchor is locked and interaction is live:
   - candidate category ∈ {Entertainment, SocialMedia, Gaming} and `offAnchorSeconds ≥
     OffTaskDistractionStartSeconds` → `off_task_distraction` fires (reason
     `outside_focus_context`).
   - candidate category ∈ {Development, Productivity} → never off-task; anchor migration
     proceeds untouched (legit focus shift).
   - candidate Unknown/Communication → neutral: no flag (conservative; 2d makes Unknown rare;
     Communication over-flags standups and code review chat, revisit with real data).
3. Anchor migration semantics unchanged — if the user genuinely moves to the new app for 240s+,
   the anchor migrates and off-anchor resets. Off-task can therefore fire only in the window
   between "left the anchor" and "anchor migrated", which for Entertainment/Social/Gaming is
   exactly right: those don't become the anchor of a WORK session in under 4 minutes.

**Files**: `DerivedSessionMetricsComputer.cs` (+ constants), tests.

**Done when** (their acceptance, as tests): (a) anchor=editor, sustained YouTube interaction past
threshold → off_task fires; (b) editor↔browser-docs (both work categories) → stays focus, never
off-task; (c) sustained move to another work app → anchor migrates, continuity kept, no flag.
Plus: the widget's Distraction mode and the "what pulled you off task" digest line light up with
REAL app names for the first time.

### 2g. return_loop over-firing (conservative unknowns)

**What**: with categories present the existing focus-positive suppression starts working by
itself; the remaining change is one predicate: flag ping-pong only when **at least one side is a
known non-work category** (Entertainment/SocialMedia/Gaming). unknown↔unknown → do not flag
(today it flags — that is the false-positive machine).

**Files**: the ping-pong block in `DerivedSessionMetricsComputer.cs` (~line 1090), tests:
work↔work not flagged; unknown↔unknown not flagged; editor↔youtube (rapid, shallow dwell)
flagged; non-work↔non-work flagged.

**Done when**: on your own machine's real data, `return_loop_distraction` share of distraction
segments drops toward genuine cases (measure the before/after ratio over a week).

---

## Sequencing (two parallel tracks, each item shippable alone)

```
Track A (widget):   2a pulse ──► 2c reminders ──► 2b meeting measurement
Track B (accuracy): 2e domains ──► 2d LLM categorizer ──► 2f off-task ──► 2g return_loop
```

- Releases stay small: one item (or one track pair) per version, MSI to both repos + manifest
  bump each time — the Phase 0 checklist is now proven.
- 2b touches the same segment code as 2f — land 2b before 2f to avoid a merge knot.
- 2f/2g ship dark behind their category dependency: code can land early, behavior only activates
  as 2d fills categories (Unknown → neutral means they simply stay quiet until data exists).

## Safety and privacy summary

- 2a: no new data, render-only. 2c: user-authored reminder text stays local, never uploaded.
- 2b: no mic/camera ever; "meeting = meeting app foreground" disclosed in-product.
- 2e: coarse domain token only; consent-gated by the existing browser-titles setting; raw
  URL/title never stored or transmitted (tested).
- 2d: only coarse tokens (already the class of data that leaves the device) go to the control
  plane; Haiku sees tokens, never user activity. Server cache means one LLM call per token ever.
- Accuracy items (2f/2g) fail conservative: Unknown category → neutral, never "distraction".

## Phase-2 exit gate (pre-charging checklist)

1. Widget feels alive (2a) and useful at a glance (2c) — the churn-risk fixes.
2. Meetings measured (2b) — Sana's ask, and idle-in-meeting no longer poisons metrics.
3. `web`/Unknown share of tracked time < 10% on the fleet (2e + 2d).
4. Off-task + return_loop fire on real distraction and only that (2f + 2g test suites green,
   week of self-dogfooding shows sane rates).
5. Then Phase 3 pricing-intent test runs against a product that earns it.
