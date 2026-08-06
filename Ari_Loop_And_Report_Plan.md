# Ari — Closing the Loop, and the Report

**What Ari becomes:** you do the work, Ari writes the report someone else is waiting for.
**Why it changes everything:** today Ari asks you things and gives back facts. Effort in, information out — a bad trade nobody pays for. After this, Ari does a job you already hate doing.

---

## The finding that shapes this plan

**Most of this is already built. It's just disconnected.**

| Already exists | Where |
|---|---|
| Tasks with a planned duration, one-active-at-a-time rule, **real time attributed automatically from your activity**, states, 30-day cleanup | `TasksStore` / `AgentTask` (agent core) |
| Task UI | `UserDesktopWidgetWindow.cs` (the widget) |
| Morning goal / midday answer / end-of-day record | `DailyGoalsStore` (agent core) |
| Check-in cards, scheduler, live AI copy | `UserCheckIn*` (agent) + `narrativeService.ts` (server) |
| Plan-vs-reality section in the nightly email | `digestEmailBuilder.ts` — `DigestTaskEntry` |
| Server accepts a tasks payload | `agentController.ts` — `parseTasks` |
| Email sending | Resend, control plane |
| Per-app time for the day | `topApps` |

**So the work is mostly connecting, not building.**

The core defect: **goals and tasks are two separate systems.** Ari asks you a question in the check-in card and stores a sentence. The timed blocks live in the widget, which nobody opens — including you. So Ari can never know whether the thing you said got done, because the two halves never meet.

---

## The order of work

Each step is useful on its own. Nothing here is a big-bang rewrite.

---

### 1. The typing bug — do this today

**Problem:** the morning card appears, you start typing, nothing happens until you click inside the box.

**Why it's first:** this is the single most important moment in the whole product, and you have about three seconds of willingness. Spending them on a click is the worst possible trade.

**Fix:** the card takes keyboard focus the moment it appears, and the text box is focused inside it.

**Size:** an afternoon.

---

### 2. Ari knows what time it is, and what happened yesterday

**Problems seen live:**
- Opened the PC in the morning after being away since 4pm, and Ari sent last night's message *first*, then the morning one after it was dismissed.
- Ask it about "yesterday's goal" and it doesn't know.

**Cause:** this is not the AI being stupid. The server hands it a sheet of facts for **one day** and no sense of the current local time. It can't reason about yesterday because nobody gives it yesterday.

**Fix:**
- A check-in that is more than a few hours late does not fire. It is replaced by the one that's actually due.
- The narrative payload carries: the local time now, yesterday's tasks and outcomes, and whether the previous day's check-in was missed.
- Copy adapts: *"You were away since yesterday afternoon — here's where things stood."*

**Size:** a few days. Mostly server-side.

---

### 3. Make goals and tasks one system ← THE FOUNDATION

**This is the change everything else depends on.**

The morning check-in stops storing a sentence. It **creates tasks** in the store that already exists.

- You type (or tap) two or three things for today
- Each becomes a real task, with a planned duration
- Time gets attributed to them automatically — that machinery already works
- The widget and the check-in are now looking at the same list

**Nothing new gets built here.** `DailyGoalsStore` stops being a separate thing and becomes a view over `TasksStore`.

**Size:** the biggest single item, but it's plumbing, not invention.

---

### 4. Propose — never present an empty box

**Problem:** typing is effort, and people won't. You proved it yourself — you stopped filling in the "tomorrow's task" question.

**Fix:** Ari already knows what you did yesterday and what you didn't finish. So the morning card arrives **pre-filled**:

> Yesterday you didn't finish: **Pricing page**
> Most of your time went to: **Figma, Slack**
>
> Today: `[ Pricing page ]` `[ + add ]`
> **Looks right** · **Change it**

Correcting is far easier than composing. And if the person taps nothing at all, Ari still has a sensible list and the product keeps working.

**Size:** small, once step 3 exists.

---

### 5. Ticking off — one tap, never typing

**Problem:** Ari has no idea whether you did the thing.

**Fix:** the midday and evening cards show today's tasks with the **measured time already next to them**:

> **Pricing page** — 1h 12m · **Done** · **Not yet**
> **Client revisions** — 2h 04m · **Done** · **Not yet**

Two taps and it's finished. Nothing to type, nothing to remember.

**This is the moment the loop closes** — and it's what makes Ari the only tool where "done" arrives with the hours behind it.

**Size:** small.

---

### 6. Carry-forward

Anything not finished rolls into tomorrow's proposal automatically. Nothing is silently dropped.

If something has been carried three days running, Ari says so once — plainly, not naggingly.

**Size:** small.

---

### 7. Kill the "what's tomorrow's task?" question

You stopped answering it, which is the clearest possible signal.

The evening slot should **close the day**, not open a new one:

> You planned three things. You finished two.
> **Pricing page** moves to tomorrow. **OK** · **Drop it**

One tap, and tomorrow's list is already made — which makes step 4 free.

**Size:** small.

---

### 8. The morning screen

The small box becomes a full screen you can't miss:

- What happened yesterday — planned vs. done, with the hours
- A simple graphic of the day
- Today's proposed tasks, ready to accept

**Rule: this has to be a glance, not a meeting.** You already ignore the nightly email. If the morning screen is heavy, people will learn to close it the same way, and you'll have lost the most valuable moment in the product.

**Size:** medium — this is UI work.

---

### 9. Tag tasks with a project or client

Needed before any report can say *"3h 40m on the Nolan project."*

Ask once per project, not per task. Ari can then propose the tag when the same pattern of work appears again.

**Size:** medium.

---

### 10. The report ← the reason someone pays

Built from what steps 3–9 produce: tasks, real hours, done or not, grouped by project.

- Ari drafts it — the writing machinery already exists on the server
- **You approve with one tap. It never sends by itself.** One wrong number to a client and the trust is gone permanently. The tap costs two seconds and saves the relationship.
- You choose who receives it and how often — any number of people
- It lands in someone else's inbox with Ari's name on it, every week

**Deliberately out of scope for now:** financial reporting. Ari knows time and tasks, not money. Adding it later is fine; promising it now is a cheque the product can't cash.

**Size:** medium — mostly assembling things that exist.

---

## What this fixes, in one line each

| Your complaint | Fixed by |
|---|---|
| Typing doesn't register until you click | 1 |
| Sent last night's message this morning | 2 |
| Doesn't understand "yesterday's goal" | 2 |
| Doesn't know if you finished the thing | 3 + 5 |
| Can't tell finished from unfinished tasks | 3 + 5 |
| Unfinished work disappears | 6 |
| "Tomorrow's task" question is useless | 7 |
| Nobody opens the widget to add tasks | 4 — Ari asks instead |
| Ari never changed what you actually did | 5 + 6 + 10 — something outside you now depends on it |

---

## Order to build in

**Now:** 1, 2 — small, and they stop Ari feeling broken.
**Then:** 3 — the foundation. Nothing after this works without it.
**Then:** 4, 5, 6, 7 — the loop closes. **Use it yourself for a week here and see whether it changes what you do.** That is the real test, before any of the rest is worth building.
**Then:** 8, 9, 10 — the report, and the reason someone pays.

---

## What we are not doing

- No website work until the loop is proven on yourself.
- No financial reports.
- No automatic sending, ever.
- No new app features outside this list.
- Security stays capped at half a day a week.
