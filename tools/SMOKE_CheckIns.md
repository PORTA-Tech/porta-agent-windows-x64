# Check-in smoke runbook (Check-in Rework + Digest Hardening)

Two ways to look at the cards. **Do #1 — it deterministically satisfies both acceptance checks
in ~30 seconds.** #2 is an optional confirmation that the live scheduler fires them; some of it is
inherently slow *by design* (that slowness is the Part 1a/1c feature, not a bug).

**Acceptance checks (what you're eyeballing on every card):**
- **A.** The fixed question label sits **above** the controls — "Did you get to your goal?" over
  Yes / Partly / Not-yet (midday), and "Plan for tomorrow?" over the plan box (EOD).
- **B.** The missed-midday / away EOD reads as **one combined away-aware wrap** — *"You were away
  for a stretch today — here's how the day landed against …"* — **not** the generic
  *"Today's goal was …"* line.

---

## 1) Preview harness — fast, deterministic (recommended)

Renders the **real** `UserCheckInCardWindow` built by the **real** `UserCheckInCoordinator`, so what
you see is pixel-for-pixel what the fleet gets. It's a **DEBUG-only** entry point — it does **not
exist** in the shipped Release binary.

```powershell
# from the repo root
dotnet build src\Porta.UserAgent\Porta.UserAgent.csproj -c Debug
& src\Porta.UserAgent\bin\Debug\net8.0-windows\Porta.UserAgent.exe --preview-checkins
```

Or: `powershell -File tools\smoke-checkins.ps1 -Action preview` (builds + launches for you).

**What you'll see — four cards, in order. Answer or dismiss (✕ / Esc) each to advance:**

| # | Card | Check |
|---|------|-------|
| 1 | **Morning** — goal box + tracking chips + "Share with Ari" | (morning owns its own box; no fixed label by design) |
| 2 | **Midday** — report line, then **"Did you get to your goal?"**, then Yes / Partly / Not-yet | **A** — label sits above the buttons |
| 3 | **End of day (normal)** — *"Today's goal was: "Finish the Q3 board deck.""*, then **"Plan for tomorrow?"**, then the plan box | **A** — label above the box; generic wrap copy |
| 4 | **End of day (missed midday + away)** — *"You were away for a stretch today — here's how the day landed against "Finish the Q3 board deck.""*, then **"Plan for tomorrow?"**, then the plan box | **A** + **B** — label above the box **and** the combined away-aware wrap |

Card 3 vs card 4 is the exact **B** comparison: same goal, generic wrap vs away-aware wrap.

> The preview forces the **template fallback** copy (empty narrative line) on purpose — that's the
> worst case that must still read right. In production, when the LLM is reachable, card 4's message
> is the model's away-aware line instead (Part 2a prompt + Part 1b facts); the fixed label and the
> report block are identical either way.

---

## 2) Live scheduler confirmation (optional)

Confirms the real host/scheduler actually fires each kind. Helper: `tools\smoke-checkins.ps1
-Action reset` clears the per-day check-in mutes (so a kind can re-fire) and restarts the UserAgent
(the watchdog relaunches it). After any reset, **use the machine continuously for ~4 minutes** —
that clears the 90 s cold-start grace **and** the 3 min settle-in (Part 1c), so nothing pops the
instant the agent restarts.

- **Morning (fast).** On a goal-day with no goal set yet, before 18:00 local: run `-Action reset`,
  then use the machine ~4 min. → Morning card. Set a goal ("Share with Ari").
- **End of day (fast, via clock).** Set the Windows clock to **≥ 18:00** local (evening), with a
  goal-day that has a goal but no EOD recorded: run `-Action reset`, use the machine ~4 min. → EOD
  card. Check **A**.
- **Midday (slow, by design — Part 1a).** Midday fires after **~2 h of real active work** since the
  morning goal, *not* 2 h of wall-clock. There is no fast way to force this on a live machine with
  capture running — the wall-clock fallback only applies when there is no processed timeline at all.
  Confirm the copy/label via the preview (card 2); confirm the *timing* by working a normal morning.
- **Away wrap (slow, by design — Part 1b).** `awayGap` reads from the live timeline's away seconds
  (≥ 2 h away today); it can't be seeded from the goal row. To see it live, close out a day that had
  a real ≥ 2 h away stretch with a goal set and midday unanswered. For the copy itself, use the
  preview (card 4).

**Clock note:** setting the system clock is the only lever for the "evening" branch; put it back
afterward. Jump it in small steps (not years) to avoid disturbing capture/telemetry.

---

## Why midday/away can't be "forced fast" live

That's the whole point of Part 1: midday now waits for **real work** and the EOD wrap reflects a
**real away gap**, instead of fire-on-the-clock. The pure logic behind both is covered by unit tests
(`UserCheckInSchedulerTests`, `UserCheckInCoordinatorTests`, `UserCheckInActivityGateTests`); the
preview is the way to eyeball the rendered result without waiting for wall-clock reality.
