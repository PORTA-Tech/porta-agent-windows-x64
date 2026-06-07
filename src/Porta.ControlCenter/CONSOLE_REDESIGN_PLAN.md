# Operations Console Redesign — Execution Plan for Claude Code

## What this is

Full redesign of the `porta-operations-console` frontend to match the mockup at
`C:\Users\hp\Downloads\porta-console.html`. The mockup is a complete, working
single-page HTML file — use it as the source of truth for every visual detail.
Do not invent anything. If it is in the mockup, implement it exactly. If it is
not in the mockup, do not add it.

---

## Step 0 — Read the mockup before touching any code

The full mockup is at `C:\Users\hp\Downloads\porta-console.html`. Read it
completely before writing a single line. Everything below references it.

---

## Design system — copy these exactly

```css
--bg: #08080f
--bg1: #0d0d18
--bg2: #111120
--bg3: #161628
--accent: #c8ff47          /* lime green — primary brand */
--accent-dim: rgba(200,255,71,0.1)
--accent-glow: rgba(200,255,71,0.2)
--text: #eeeef8
--muted: #6b6b88
--muted2: #9898b8
--border: rgba(255,255,255,0.06)
--border2: rgba(255,255,255,0.1)
--green: #4ade80
--green-dim: rgba(74,222,128,0.12)
--amber: #fbbf24
--amber-dim: rgba(251,191,36,0.12)
--red: #f87171
--red-dim: rgba(248,113,113,0.12)
--blue: #60a5fa
--blue-dim: rgba(96,165,250,0.12)
```

**Fonts** (load from Google Fonts):
- `Bricolage Grotesque` — display/headings (weights 400, 500, 600, 700, 800)
- `Instrument Sans` — body text (weights 400, 500, 600)
- `JetBrains Mono` — monospace/data values (weights 400, 500)

Google Fonts URL:
```
https://fonts.googleapis.com/css2?family=Bricolage+Grotesque:opsz,wght@12..96,400;12..96,500;12..96,600;12..96,700;12..96,800&family=Instrument+Sans:wght@400;500;600&family=JetBrains+Mono:wght@400;500&display=swap
```

---

## Layout structure

```
<div class="layout">          ← display:flex, height:100vh, overflow:hidden
  <aside class="sidebar">     ← width:220px, fixed left
  <main class="main">         ← flex:1, overflow-y:auto
    <div class="topbar">      ← sticky top, height ~56px
    <div class="content">     ← padding:24px 28px
```

---

## Sidebar — exact structure

```
logo block:
  "por" + <span style="color:var(--accent)">ta</span>   ← font: Bricolage Grotesque 800 18px
  "OPERATIONS CONSOLE" badge below logo

section label "Monitor":
  nav: System Health       [badge: machine count]
  nav: User Activity       [badge: active now count]
  nav: Baseline & Calibration

section label "Events":
  nav: Actions             [badge: action count or "200+"]
  nav: Nightly Digest

section label "Analytics":
  nav: Growth              [badge: user count e.g. "4 users"]

bottom fleet status block:
  • Control Plane: Connected (green blinking dot)
  Machines: [count]
  Active now: [count] (accent color)
  Last sync: [relative time]
```

Active nav item style: `background: var(--accent-dim); color: var(--accent); border-left: 2px solid var(--accent)`

---

## Topbar — exact structure

Left: page title (Bricolage Grotesque 700 15px) + subtitle (12px muted, updates per page)

Right:
- Pill: green blinking dot + "Control plane connected"
- Pill: amber dot + "Realtime disabled"
- Clock showing Dubai time (HH:MM:SS, JetBrains Mono 11px, updates every second)

Page title + subtitle per section:
```
system-health:  "System Health" / "Is Porta running and collecting activity correctly?"
user-activity:  "User Activity" / "What is Ari tracking right now across the fleet?"
calibration:    "Baseline & Calibration" / "How calibrated is each Ari agent?"
actions:        "Actions" / "What did Ari decide, suggest, or apply?"
digest:         "Nightly Digest" / "Did the 23:30 digest fire for each machine last night?"
growth:         "Growth" / "Registrations, activation, retention, and cohort analysis."
```

---

## Shared component: Fleet Status Cards (4-column strip)

Used at top of every page. Each card:
- `background: var(--bg1)`, `border: 1px solid var(--border)`, `border-radius: 10px`, `padding: 16px 18px`
- **2px colored top border** (position:absolute, top:0, left:0, right:0):
  - green card: `var(--green)`
  - amber card: `var(--amber)`
  - red card: `var(--red)`
  - blue card: `var(--blue)`
  - accent card: `var(--accent)`
- Label: 11px, uppercase, letter-spacing 0.8px, muted, font-weight 600
- Value: Bricolage Grotesque 28px 800, letter-spacing -1px, colored to match card
- Sub: 11px muted

---

## Shared component: Card

```
background: var(--bg1)
border: 1px solid var(--border)
border-radius: 12px
overflow: hidden

card-header: padding 16px 20px 14px, border-bottom 1px var(--border)
  card-title: Bricolage Grotesque 700 13px, letter-spacing -0.2px
  card-sub: 11px muted, margin-top 2px

card-body: padding 20px
```

---

## Shared component: Data table

```css
th: 10px, 600, uppercase, letter-spacing 1px, muted, padding 10px 12px, border-bottom
td: padding 10px 12px, border-bottom, 12px muted2
tr:hover td: background var(--bg2)
```

Machine name / data cells: `font-family: JetBrains Mono, 11px, color: var(--text), font-weight 500`

---

## Shared component: State badges

```css
.state-focused:  background var(--green-dim),  color var(--green)
.state-away:     background var(--amber-dim),  color var(--amber)
.state-distracted: background var(--red-dim),  color var(--red)
.state-break:    background var(--blue-dim),   color var(--blue)
.state-offline:  background rgba(255,255,255,0.05), color var(--muted)
```

All: `display:inline-flex, align-items:center, border-radius:5px, padding:3px 8px, font-size:10px, font-weight:700, letter-spacing:0.5px, text-transform:uppercase, font-family:JetBrains Mono`

Derive state from `attentionSegmentKind`:
- `focus` → FOCUSED (green)
- `away` → AWAY (amber)
- `off_task_distraction` or `fragmentation_distraction` → DISTRACTED (red)
- `context_excursion` → CONTEXT SWITCH (amber)
- machine offline / no data → OFFLINE (grey)

---

## Page 1 — System Health

**API:** `GET /v1/machines/summary` → `MachineSummaryReport[]` (poll every 30s)

**Fleet strip (4 cards):**
- HEALTHY (green): count where `agentRunning && userSessionConnected && !partialData && no lastError`
- DEGRADED CAPTURE (amber): count where `partialData === true || missingMajorChannels.length > 0`
- DISCONNECTED (red): count where `agentRunning === false`
- NO LIVE ACTIVITY (blue): count where `activityDetected === false && agentRunning === true`

Sub-text for "No Live Activity": "Last activity [time]" using the most recent `lastActivityUtc` across that subset.

**Grid 2 columns:**

Left card — "Agent Uptime — Last 7 Days":
- Sub: "Online vs offline per machine"
- Legend: Online (green 70% opacity), Partial (amber 60% opacity), Offline (bg3)
- Body: one uptime row per machine
  - Machine name (JetBrains Mono 11px, width 140px, ellipsis)
  - 7 bars (flex, gap 2px): each bar `height:20px, border-radius:3px`
    - online: `background:var(--green), opacity:0.7`
    - offline: `background:var(--bg3)`
    - partial: `background:var(--amber), opacity:0.6`
  - Percentage (JetBrains Mono 11px muted, width 36px, right-align)
- Data source: `GET /v1/machines/:machineId/uptime?days=7`
  - Map `agentRunning === true` → online, `false` → offline
  - If endpoint returns no data, show 7 offline bars at 0%
  - Day labels below: Mon Tue Wed Thu Fri Sat Today

Right card — "Capture Quality Trend":
- Sub: "Healthy / Degraded / Disconnected count per day"
- Legend: Healthy (green), Degraded (amber), Disconnected (red)
- SVG line chart, viewBox "0 0 400 140"
  - 3 lines: healthy count per day, degraded count per day, disconnected count per day
  - Colors: `rgba(74,222,128,0.7)`, `rgba(251,191,36,0.6)`, `rgba(248,113,113,0.6)`
  - Stroke-width 2, linecap round, linejoin round
  - Dots: r=3, same color as line
  - Faint horizontal grid lines at y=2,4,6,8 in `rgba(255,255,255,0.04)`
  - Day labels below: Mon Tue Wed Thu Fri Sat Today
- Data source: derive from the 7 uptime samples across all machines per day

**Machine Detail table (full width card below):**
- Tag top-right: "[n] machines" (accent)
- Columns: Machine | User | Device Status | Last Seen | Agent | Activity | 7d Uptime | Error
- Machine: JetBrains Mono 11px, text color
- User: name 12px + email 11px muted below
- Device Status: state badge (always "Active" for now, green dim)
- Last Seen: relative time from `reportedAtUtc`, JetBrains Mono 11px
- Agent: "Yes" green / "No" muted, JetBrains Mono 11px
- Activity: `activityDetected` → Yes/No, same style
- 7d Uptime: percentage from uptime data, color: >70% green, >30% amber, else red
- Error: `lastError || captureFailureReason || ownershipWarning`, 11px muted, max-width 180px ellipsis

---

## Page 2 — User Activity

**API:** `GET /v1/machines/summary` (same data, poll every 5s for this page — it says "Live")

**Fleet strip (4 cards):**
- ACTIVE RIGHT NOW (accent): count where `activityDetected === true`
- IN FOCUS (green): count where `attentionSegmentKind === 'focus'`
- AWAY (amber): count where `attentionSegmentKind === 'away'`
- AVG FOCUS TODAY (blue): average `focusEpisodeSeconds` across machines with data, formatted as "47m"

**Grid 2fr + 1fr:**

Left card — "Live Machine States":
- Tag top-right: "Live" (green)
- Body: CSS grid, `grid-template-columns: repeat(auto-fill, minmax(240px, 1fr))`, gap 12px
- One machine card per machine:
  ```
  background: var(--bg2), border: 1px solid var(--border), border-radius: 10px, padding: 16px
  
  Top row:
    Left: machine name (JetBrains Mono 11px) + user name below (11px muted)
    Right: state badge (FOCUSED/AWAY/DISTRACTED/OFFLINE)
  
  Metrics grid (2 cols, border-top, padding-top 12px):
    Focus today: focusEpisodeSeconds formatted (Bricolage Grotesque 16px 700)
                 label: "Focus today" (10px muted uppercase)
    Pressure:   pressureLevel * 100 as "XX%" colored: >60% red, >30% amber, else green
                label: "Pressure" (10px muted uppercase)
  
  Bottom: "In state [attentionSegmentSeconds formatted] · Updated [relative time]"
          JetBrains Mono 10px muted
  ```

Right card — "Fleet Distraction":
- Sub: "Patterns firing most"
- Progress bars showing distraction pattern breakdown:
  - Each row: label (12px muted2, width 130px) + track (flex:1, height:6px, bg3, radius 3px) + percentage (JetBrains Mono 11px, width 36px)
  - Patterns to show:
    - "App-switch spiral" → count of machines where `attentionSegmentReasonCode === 'fragmentation_distraction'`
    - "Junk browsing" → `attentionSegmentReasonCode === 'outside_focus_context'`
    - "Social interrupt" → `attentionSegmentReasonCode === 'context_excursion'`
    - "Idle drift" → `attentionSegmentKind === 'away'`
    - "Overload pressure" → `overloadActive === true`
  - Calculate each as (count / total machines) * 100
  - Color accent for >50%, amber for >25%, muted for rest
- Divider line
- Inline stats below: "Avg focus today" (accent) + "Avg pressure" (text)

---

## Page 3 — Baseline & Calibration

**API:** `GET /v1/machines/summary`

**Fleet strip (4 cards):**
- CALIBRATED (green): count where `calibrationStage === 'calibrated'`
- LEARNING (amber): count where `calibrationStage === 'provisional'`
- WARMING UP (red): count where `calibrationStage === 'cold'`
- TOTAL MACHINES (accent): total count

**Calibration Progress card:**
- Title: "Calibration Progress", Sub: "Per-machine progress toward baseline readiness"
- One progress row per machine:
  - Label: machine name (JetBrains Mono 11px, width 130px)
  - Track: height 8px, bg3, radius 3px
  - Fill:
    - `calibrated` → 100%, green fill
    - `provisional` → 55%, amber fill
    - `cold` → `Math.max(5, daysData * 8)`%, muted fill at 40% opacity
  - Value: percentage (JetBrains Mono 11px muted2, width 36px)
  - Note: `daysData` is not in the contract — derive as days since first `reportedAtUtc` for that machine, or show 0 if unknown

**Calibration Detail table:**
- Columns: Machine | User | Calibration Stage | Trust Tier | Days of Data | Last Reported
- Calibration Stage: colored text — green for calibrated, amber for provisional, muted for cold
- Trust Tier: `trustTier` field — green for 'high', muted for others
- Days of Data: show "—" (no reliable source in current contract)
- Last Reported: relative time from `reportedAtUtc`

---

## Page 4 — Actions

**API:** `GET /v1/actions/recent` (returns `MachineActionEvent[]`, poll every 15s)
Also uses machine summary for machine name lookup.

**Fleet strip (4 cards):**
- TOTAL ACTIONS (accent): total count from `listRecentMachineActionEvents`, show "200+" if over 200
- APPLIED (green): count where `executionState === 'executed'`, show as "~XX%"
- FOCUS MODE (blue): count where `action === 'focus_mode_enabled'` or `action === 'deep_work_active'`
- OVERLOAD ALERTS (amber): count where `action === 'inform_overload_risk'` or `actionCategory === 'overload'`

**Grid 2 columns:**

Left card — "Action Timeline":
- Tag: "Live" (accent)
- Sub: "Last 20 Ari decisions — what was decided vs applied"
- One action row per event (max 20):
  ```
  Time (JetBrains Mono 10px muted, width 50px)
  Machine name (JetBrains Mono 11px text, width 160px, ellipsis)
  Action label (12px muted2, flex:1) — use action field, replace underscores with spaces
  Status: "applied" → green "applied", else muted "decided" — JetBrains Mono 10px 600
  ```
- Rows separated by `border-bottom: 1px solid var(--border)`

Right card — "Break Suggestions Log":
- Sub: "Last 10 break suggestions with acknowledgement"
- Filter from action events where `action === 'stretch_break_suggestion'` or `action === 'break_suggestion'`
- If none: show empty state with envelope icon and text "No break suggestions sent yet.\nAri will log them here once triggered."
- If data: same row format as action timeline

---

## Page 5 — Nightly Digest

**API:** `GET /v1/digest/log` (returns `DigestLogEntry[]`, poll every 60s)
This endpoint may not exist yet — if it returns 404 or errors, show empty state.

**Fleet strip (4 cards):**
- FIRES AT (accent, font-size 20px): "23:30" / sub "Dubai time (UTC+4)"
- MIN TRACK TIME (green, font-size 20px): "1h" / sub "Required to fire"
- LAST NIGHT (amber): count of entries from last night, or "—" if none / sub "No entries yet"
- ELIGIBLE TONIGHT (blue): count of machines where `trackedSeconds >= 3600` from today's summary

**Nightly Digest Log card:**
- Title: "Nightly Digest Log", Sub: "First thing to check each morning — did it fire?"
- If no entries: empty state with mail icon + "No digest entries yet.\nFirst entry appears at 23:30 Dubai time when ≥1h is tracked."
- If entries: one row per entry:
  ```
  Date (JetBrains Mono 11px muted, width 80px) — dateLocal field
  Machine (JetBrains Mono 11px text, width 160px) — machineId → look up machineName
  Tracked time — trackedSeconds formatted as "Xh Ym"
  Status:
    fired === true → "✓ Sent" (green, JetBrains Mono 11px 600)
    fired === false → "✗ Skipped" (amber)
    send_failed → "✗ Failed" (red)
  Reason (11px muted, flex:1):
    under_1h → "Under 1 hour tracked"
    email_not_set → "No email configured"
    send_failed → "Send failed"
    account_not_found → "Account not found"
    (empty when fired)
  ```

---

## Page 6 — Growth

**API:** `GET /v1/growth/summary`, `GET /v1/growth/series?metric=registrations&interval=day`, `GET /v1/growth/cohorts`
All require auth headers. Poll every 5 minutes.

If any endpoint returns error or no data: show empty state with text "Growth data unavailable. The control plane may be starting up."

**5-column hero metrics strip** (not fleet cards — different component, smaller):
Each metric block:
- Value: Bricolage Grotesque 26px 800
- Label: 11px muted, margin-top 4px
- Delta: 11px, margin-top 4px — green (↑) for positive, amber (→) for neutral

Metrics:
1. Registered Users (accent): `totalRegisteredUsers`
2. Activation Rate (green): `activationRatePct`% — delta: "[n]/[n] activated"
3. D1 Retention (green): `retentionRateD1Pct`% — delta: "All users returned" if 100%
4. D7 Retention (amber or green): `retentionRateD7Pct`% — delta: show fraction
5. Active Today (blue): DAU for today — from growth series

**Grid 2 columns — charts:**

Left card — "Registrations — Last 30 Days":
- Tag: "+[newRegisteredUsers]" (accent)
- Sub: date range
- SVG bar chart, viewBox "0 0 500 160":
  - One bar per day over last 30 days
  - Days with registrations: accent color (`rgba(200,255,71,0.85)`), full height
  - Days without: near-invisible (`rgba(255,255,255,0.04)`), 3px height
  - Label "+1" above registration bars in accent
  - Day labels on x-axis for the registration dates only
- Data from `GET /v1/growth/series?metric=registrations&interval=day`

Right card — "Daily Active Users":
- Tag: "[n] today" (green)
- Sub: "Account-active-day facts"
- SVG area+line chart, viewBox "0 0 500 160":
  - Area fill: `rgba(200,255,71,0.06)`
  - Line: `rgba(200,255,71,0.8)`, stroke-width 2.5
  - Dots: r=5, fill bg1, stroke accent 2px
  - Value labels above each dot in accent
  - Grid lines at y=1,2,3 faint
- Data from `GET /v1/growth/series?metric=active_users&interval=day`

**Grid 1fr + 2fr — retention:**

Left card — "Retention Funnel" (D1 → D7 → D30):
- 4 rows: Registered, D1 Retained, D7 Retained, D30 Retained
- Each: label (12px muted2) + percentage (JetBrains Mono 12px) + progress bar (height 10px)
- Colors: accent, green, amber, muted(30% opacity for maturing)
- Use `retentionRateD1Pct`, `retentionRateD7Pct`, `retentionRateD30Pct`

Right card — "Cohort Retention Table":
- Sub: "By registration week · cells unlock as window matures"
- Columns: Cohort | Registered | Activated | D1 | D7 | D30
- D1/D7/D30 cells: centered, value (Bricolage Grotesque 15px 700) + "n/n" sub (JetBrains Mono 10px muted)
- Color: green for ≥80%, amber for ≥50%, red for <50%, muted "n/a" if not yet mature
- Data from `GET /v1/growth/cohorts`

---

## Auth — how every API call must be made

Every call to `/v1/*` needs these headers:
```
x-operator-id: porta-ops-console
x-operator-role: operator
```

This is the existing auth pattern used by the console. Do not change it. If any call returns 401 or 403, log the error to console and show an error state on that section only — do not crash the whole page.

---

## Data formatting helpers (implement these once, use everywhere)

```
formatSeconds(s):
  if s < 60: return "${s}s"
  if s < 3600: return "${Math.floor(s/60)}m"
  h = Math.floor(s/3600); m = Math.floor((s%3600)/60)
  return m > 0 ? "${h}h ${m}m" : "${h}h"

relativeTime(isoString):
  diff in seconds from now
  < 60: "just now"
  < 3600: "${Math.floor(diff/60)}m ago"
  < 86400: "${Math.floor(diff/3600)}h ago"
  < 604800: "${Math.floor(diff/86400)}d ago"
  else: formatted date

pressureColor(level):
  level > 0.6: var(--red)
  level > 0.3: var(--amber)
  else: var(--green)
```

---

## Polling intervals

- System Health: 30 seconds
- User Activity: 5 seconds (labeled "Live")
- Baseline & Calibration: 60 seconds
- Actions: 15 seconds
- Nightly Digest: 60 seconds
- Growth: 5 minutes

---

## Empty states

When a section has no data or an API call fails:
```
centered div, padding 40px 20px, color muted
SVG icon (32x32, opacity 0.3)
text: 13px, margin-top 8px
```

Use appropriate icon per section (envelope for digest, lightning bolt for actions, etc.)

---

## What NOT to do

- Do not keep any of the current light theme CSS
- Do not keep the "OPERATIONAL VIEW" section label that appears above every page title
- Do not keep the "Copy Link" button
- Do not keep the "Control Plane / Realtime Channel / Last Update" status row inside each page — this moves to the topbar
- Do not add anything that is not in the mockup
- Do not change the API contract or add new endpoints — use what exists
- The sidebar footer shows "Active now" in accent color — this is the live count of machines where `activityDetected === true`

---

## Final check before marking done

1. Open the redesigned console and compare side-by-side with the mockup HTML
2. Every font must be Bricolage Grotesque / Instrument Sans / JetBrains Mono — no system fonts anywhere
3. Background must be #08080f — not white, not grey
4. The accent color is `#c8ff47` (lime) — not blue, not the old purple
5. Every page's fleet strip must have colored top borders on each card
6. The clock in the topbar must tick every second in Dubai time
7. Machine state badges must be uppercase, JetBrains Mono
8. All data values (percentages, times, counts) must use JetBrains Mono
9. All headings and large numbers must use Bricolage Grotesque
