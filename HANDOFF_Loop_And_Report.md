# Handoff — Ari's new direction and the build

**Written:** August 2026, end of a long strategy session with Yadin.
**For:** the next session (Claude Code, fresh chat).
**Read this whole file before touching code.** Then read `Ari_Loop_And_Report_Plan.md`, which is the actual work queue, and then `Loop_And_Report_Build_Brief.md`, which corrects the file paths in both documents and records the decisions since locked. Where the brief and these two disagree about code, the brief wins; where they disagree about intent, these two win.

---

## 1. The one paragraph that matters

Ari is changing from **a thing that shows you your day** into **a thing that does a job for you.**

The old product asked the user to type things and gave back facts about themselves. Effort in, information out — a bad trade, which is why nobody paid and why the founder himself says it never once changed what he did that day.

The new product: **you do the work, and Ari writes the report that someone else is waiting for.** A client, a boss, a co-founder. The user approves it with one tap and it sends.

Everything in the build queue exists to make that possible.

---

## 2. Why — the evidence, so this isn't re-litigated

- **18 installs ever. 14 never signed in. ~6 registered. 1–2 active on a given day. Zero new users in 30 days.**
- The old churn story ("people left before calibration finished") is **not supported**. Pre-June-2026 activity data was fabricated by an agent bug — see `Growth_Console_Accuracy_Plan.md` in the control plane, which says those rows can't be separated from real ones. Several "churned" machines were VMs, forked 1.0.38 binaries that couldn't capture, or people who never signed in. One real user lasted 14 minutes — that isn't a slow-calibration problem, that's disinterest.
- **The honest diagnosis, from the founder:** it's a value problem. Ari was interesting and not useful.
- **The mechanism that was missing:** Ari opened loops and never closed them. It asked what you'd do each morning, then behaved as if it had never asked. A commitment nobody follows up on is a note, not a commitment. That is why it never changed behaviour.
- **The strategic addition:** somebody other than the user now depends on Ari's output. That's what creates stakes, and it's also the first thing that makes the product spread — every report lands in a stranger's inbox with Ari's name on it.

---

## 3. The key finding about the codebase

**Most of this is already built. It is disconnected.**

| Already works | Where |
|---|---|
| Tasks: planned duration, one-active-at-a-time, **real time attributed automatically from activity buckets**, states, 30-day cleanup | `TasksStore` / `AgentTask` — agent core. See `Porta.Agent.Core.Tests/TasksStoreTests.cs` |
| Task UI | `Porta.UserAgent/Interventions/UserDesktopWidgetWindow.cs` |
| Morning goal / midday answer / end-of-day | `DailyGoalsStore` — agent core |
| Check-in cards, scheduler, activity gate | `Porta.UserAgent/Interventions/UserCheckIn*.cs` |
| Live AI copy for check-ins (Opus, server-side, grounded, fail-soft) | `narrativeService.ts` — control plane |
| Nightly email with a plan-vs-reality tasks section | `digestEmailBuilder.ts` — `DigestTaskEntry` |
| Server accepts a tasks payload | `agentController.ts` — `parseTasks` |
| Email delivery | Resend, control plane |
| Per-app time for the day | `topApps` |

**The central defect:** goals and tasks are two separate systems. The check-in stores a sentence in `DailyGoalsStore`; the timed blocks live in `TasksStore` behind the widget, which the founder says he never opens. So Ari can never know whether the thing the user said actually got done.

**Step 3 of the plan merges them. It is mostly plumbing, not invention.**

---

## 4. Corrections to existing docs — do not inherit these mistakes

`ONBOARDING.md` is dated 2026-08-02 and is wrong in two places:

- It says the **widget USER-handle leak** must be fixed before a wide rollout. It was **fixed on 2026-07-20** — commits `349bc21` (disposal fix + `GuiResourceMeter` + a 239-line handle-count stress test) and `811c0e9` (watchdog relaunch backoff). Both files are in the tree. Still worth one live confirmation on PEACH20 / AYMENE that the handle error stopped, but it is not build work.
- It says version **1.2.122**. The released version is **1.2.126** (2026-08-04).

Also open, from `SECURITY_HANDOFF.md`: four known-failing Core tests as of 2026-08-04.

---

## 5. The build queue

**`Ari_Loop_And_Report_Plan.md` is the work queue. Follow its order.**

Summary:

1. **Typing bug** — the morning card must take keyboard focus immediately. Today it needs a click before it accepts input. An afternoon's work, do it first.
2. **Time and memory** — a stale check-in must not fire (Ari sent last night's message in the morning). The narrative payload needs local time now, yesterday's tasks and outcomes, and whether a check-in was missed.
3. **Merge goals and tasks** ← the foundation. The morning check-in creates real tasks instead of storing a sentence.
4. **Propose, never an empty box** — pre-fill the morning card from yesterday's unfinished work.
5. **One-tap done** — midday/evening cards list today's tasks with the measured time already beside them.
6. **Carry-forward** — unfinished work rolls to tomorrow automatically.
7. **Evening closes the day** — delete the "what's tomorrow's task?" question; the founder stopped answering it himself.
8. **Morning screen** — a full screen showing yesterday's result and today's proposal. Must be a glance, not a meeting.
9. **Project/client tags** — only needed if reports go to more than one recipient. Ask once per project, not per task.
10. **The report** — drafted by Ari, approved by the user, then sent.

---

## 6. Hard rules

- **The report never sends automatically.** Ari drafts, the user taps, then it goes. One wrong number to a client destroys trust permanently; the tap costs two seconds.
- **No financial reporting.** Ari knows time and tasks, not money. Later, maybe. Not now.
- **No website work.** The positioning depends on this loop existing. Writing the page first means promising something the product can't do — the exact mistake that produced the current site.
- **No new app features outside the plan.**
- **Security work is capped at half a day a week** unless something is actively exploitable. Three weeks disappeared into it in July while the pre-launch plan said explicitly not to build.
- **Nothing rolls to the fleet without Yadin's explicit go.**

---

## 7. The checkpoint — stop after step 7

After steps 1–7, **stop building and have Yadin use it himself for a week.**

The test is one question: **has Ari changed what he actually did that day?** Today the honest answer is no, and he's used it for months. If closing the loop doesn't change that for him, steps 8–10 won't save it, and we need to know before building them.

---

## 8. Repo conventions — keep the existing culture

From `ONBOARDING.md` and visible throughout the code:

- Measure before changing. Keep the old path as a fallback. Small reversible commits. Never guess.
- Decision logic lives in **pure, I/O-free functions with a reason code for every refusal** (see `BreakSuggestionPolicy`, `UserCheckInScheduler`). Keep new decision logic in that shape and unit-test it.
- Releases are MSI installers. ~~built **only** via `scripts/build-msi.ps1`~~ — **THIS LINE IS WRONG AND COST A BROKEN MACHINE ON 2026-08-06.** The real script is the numbered `build-<version>.ps1` at the repo root (latest: `build-1297.ps1`). `scripts/build-msi.ps1` publishes with `PublishSingleFile=false` and produces an MSI that fails to start and rolls back. See `Loop_And_Report_Build_Brief.md §9`. Version must still be bumped in three `.csproj` files first.
- Auto-update reaches the fleet within ~6h of publishing a manifest — a bad release reaches every user automatically.
- Plan documents are the briefs: confirmed causes, pinned file paths, locked decisions, a test bar before release.

---

## 9. Things stated here that were read, not run — verify before relying on them

- `TasksStore` behaviour above is read from `TasksStoreTests.cs`, not from a running app. **Confirm time attribution actually works end to end before step 3 leans on it.**
- Whether the widget's task UI writes to the same store the check-in would use.
- Whether `DailyGoalsStore` has any consumer that would break when it becomes a view over tasks.

---

## 10. Start here

Step 1. The morning check-in card should accept typing the instant it appears, with no click. It's small, it's isolated, and it's the most important three seconds in the product.
