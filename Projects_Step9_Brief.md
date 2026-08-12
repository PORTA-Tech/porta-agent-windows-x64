# Step 9 and beyond — Projects

Written 2026-08-12 by Yadin. This is the brief. Nothing is to be built until the four questions at
the end are answered.

Read `START_HERE_2026-08-11.md` first for the state of the project. This file is the next piece of
work, not a replacement for that one.

---

## 0. The one thing that is not in the plan

Steps 9 and 10 were gated on a week of real use — the test being *has Ari changed what Yadin
actually did that day?* The loop only became complete on 2026-08-12 (fourth card, estimate-expiry
ask pill, "Tomorrow" that sticks, redesigned digest). So there is no week yet, and Yadin knows it.
He has chosen to start step 9 anyway because it is a straight prerequisite for the report and mostly
adds information rather than changing how the loop behaves.

Remind him of this once if step 10 comes up. Do not repeat it.

---

## 1. What projects are

Projects are a real thing that tasks belong to, in the way Claude has projects. **Ari never guesses
a project.** The user picks it, or the task stays untagged — and untagged is a valid, permanent
state that must never nag.

This overrides the earlier "Ari proposes the tag when the same pattern appears again" design. Yadin
rejected it explicitly.

---

## 2. The picker, on every card where a task is named

It sits on the existing duration row, to the right of the 1h / 2h / 3h chips. Three states:

| State | Appearance |
|---|---|
| Empty | Dashed muted chip: `+ project` |
| Tapped | Expands one row beneath with existing projects plus `+ new`. `+ new` opens a small text box in the same place |
| Picked | Collapses back to a single chip carrying the name |

**At rest it must add zero height.** The user never leaves the card to create a project. The same
picker appears on the widget's `+ task`.

---

## 3. A day with two projects

- **Cards** — no grouping. Three tasks is the ceiling, so a chip per row reads better than section
  headers would.
- **Widget** — the project is a quiet muted prefix before the task name, not a chip. There is no
  room for a chip and it needs no emphasis.
- **Digest** — a `BY PROJECT` block above the task list: name, bar, total.
  - Only rendered if at least one task is tagged.
  - `Untagged` appears only alongside tagged work. This is deliberately different from the
    "Other browsing" rule: untagged is a choice the user made, not something Ari failed to
    recognise, so showing it is honest rather than an admission of ignorance.
  - One project and nothing else means the block says nothing and is omitted.

---

## 4. The projects page — design toward it, do not build it yet

A screen in Control Center. Projects with totals down the left; for the selected project:

- Total time and the number of days it spans
- A per-day bar chart
- Its tasks, with hours and state
- Which applications the time went into
- How it felt — focus quality, best run, pressure

That page is essentially the step 10 report rendered on screen, which is why **the data model has
to support it from the start** even though the page itself comes later.

---

## 5. What leaves the machine

**Yadin's decision, overriding the local-only recommendation.** Project names go everywhere: the
control plane, Anthropic for Ari's take, the digest payload, and the email body. The product does
not work otherwise — a report cannot say "3h 40m on Nolan" if the name never leaves the machine.

**But it must be a setting, defaulting on for Yadin.** The reasoning, in his words: the moment Ari
reaches anyone else, that setting stops being about his clients and becomes his *users'* clients
flowing through his server, his Anthropic account and his email provider. He wants to choose that
default deliberately rather than inherit it.

Worth restating what already leaves today, because it is the context for the above: task titles go
to the control plane and on to Anthropic in the narrative fact sheet; they go to the control plane
in the digest payload (rendered, then discarded — `daily_stats` has no tasks column); and they go
into the digest email itself, through Resend and into Gmail, both of which keep it permanently.
The email is the only one that cannot be purged.

**Name every new field before writing it, with real example values.** This is a standing rule.

---

## 6. State of mind

Focus quality, best run and pressure are already measured. Yadin wants them attributable per
project — that is part of what makes a report worth paying for.

---

## 7. Connecting to other software — future, explicitly not now

Eventually Ari should link to the tools the user actually works in, so a project's picture is richer
than window titles can make it. **Do not start this.** The only ask is: say whether anything built
now would make it harder later.

---

## 8. Constraints that do not move

- **Height.** The morning card is at ~550 DIPs against a 672 work area. The chip at rest costs zero
  height. State the cost of the *expanded* picker before building; if a three-task card with a
  picker open exceeds the budget, say so rather than shrinking something else to fit.
- **Green** means finished work and the primary button. Nothing else, ever. A selected project chip
  uses the neutral fill `#2E353E`, never the accent.
- **No new interruption.** No new card, no new pill. The 45-minute floor between interruptions and
  the three-a-day cap both still hold.
- **Time must never split across projects.** Only one task is active at a time, so a minute belongs
  to one task and therefore to one project. Confirm this holds everywhere — it is the thing that
  would quietly corrupt a client invoice.
- The usual: installing is Yadin's, numbered `build-<version>.ps1` only, never
  `scripts/build-msi.ps1`, no tag/release/manifest, every claim marked verified or inferred.

---

## 9. Answer these before writing any code

1. **A phased plan.** What is step 9 proper, what is the projects page, what is later. This brief is
   bigger than one step and it should be split honestly.
2. **The full field list** — schema changes and payload additions — with real example values.
3. **The height cost** of the expanded picker.
4. **Anything in here that fights something already built**, and why.

---

# 10. Decisions — 2026-08-12, after reading `Projects_Step9_Answers.md`

All four questions in that file are answered here, plus everything else settled in the same pass.
**This section supersedes anything above it that it contradicts.**

## 10.1 Scope — build 9a alone

Build **9a only**: projects exist locally. The picker, the chip, the Control Center list. Nothing
new leaves the machine.

Then stop, hand over the build, and Yadin lives with it before deciding 9b. The extra install is
the price of being able to change his mind about 9b, and he considers that cheap.

**Confirm explicitly that the digest does not change at all in 9a.** Yadin wants to be able to tell
9a and 9b apart by looking at his email. If anything about the digest moves in 9a, say so before
building.

## 10.2 Accepted without change

These three findings are correct and go in as proposed:

- **Case-insensitive unique project names.** "Nolan" and "nolan" splitting a client's total in half
  is exactly the failure this product cannot have.
- **The setting lives in `runtime_state`, not `settings`.** Settings rows are pruned on a timer; a
  preference stored there would silently evaporate.
- **No `byProject` aggregate field.** The email sums the block from the tasks it already carries, so
  the summary can never contradict the list beneath it.

## 10.3 Re-tagging — the invoice risk

Allow re-tagging freely. The protection already exists as a locked decision: **the report never
sends automatically**, so a re-tag cannot reach a client unreviewed.

What needs defining is authority, and it is this:

- The **digest email** is a snapshot of what was known at 23:30 that day. It is **never
  regenerated**, and nothing pretends it was right.
- The **projects page and the report** are current truth, and the report is built from them.
- If a day was re-tagged after its digest went out, **the page says so.**

Nothing is silently rewritten.

## 10.4 The widget — drop the project prefix

Not shown at all. The task title has ~96 DIPs; `Nolan · ` eats ~40% of it, so "Client revisions"
becomes "Client rev…". That trades the thing the user needs to read for the thing they don't. The
widget is about what is being worked on now, not what it bills to.

## 10.5 The picker — hard limits

Measured ceiling is 632 DIPs; today's three-task card is 566.4. A chosen chip at rest costs zero,
as required. One picker open costs 29.6.

- **One picker open at a time.** Three open would be 673 and off the screen.
- **Cap the visible list at six** most-recently-used projects, plus `+ new`. Beyond six, the user
  manages projects in Control Center.
- **The list must wrap.** The row control used today clips instead of wrapping.
- **"Start the day" must never leave the screen.** At eight chips the button currently sits 12.75
  DIPs below the window and there is no scrollbar — it is cut off, not scrolled. That must be
  impossible by construction, not by hoping the list stays short.

## 10.6 The `+ new` flow, and what a project is on a card

`+ new` opens a text box **on the card**, in the same place. Type the name, confirm, it collapses
back into a chip on that row. The user never leaves the card, and presses "Start the day"
afterwards.

**Enter in that box must not start the day.** The box swallows it.

**On a card, a project is only a name.** Client, colour, rate, anything richer belongs in Control
Center. The morning card must not become a form — that is the rule the whole loop depends on.

## 10.7 Retention — two different decisions, do not merge them

**a. Task rows are cheap. Extend them.** Estimated at roughly 200 bytes each, three a day, so under
250 KB a year — nothing beside the gigabytes of build output and old installers that actually fill
this disk. **Measure it, confirm, then extend local task retention to a year or more.** For Yadin's
own projects page, no server is needed at all.

**b. The five-second bucket table is the expensive one.** Roughly 17,000 rows a day, and it is where
the per-app breakdown and the shape of the day come from. **State what it costs per day on disk
today, and what a longer retention would cost, before anyone proposes changing it.** If the projects
page needs per-app time going back months, say so now — that is a real cost and Yadin wants it known
before it is designed in, not after.

**Confirm nothing depends on the 30-day sweep.** If any code assumes closed tasks disappear, name
it.

## 10.8 Server-side storage (9c) — a decision, not an obstacle

There is no technical blocker to keeping task history server-side. It is deferred because a durable
server-side record of what users worked on, and for whom, is a materially bigger commitment than
sending it transiently — and because the local extension in §10.7a answers the projects page without
it.

The end state is **both**: local for depth and speed, server for durability and anything that must
outlive this machine. **Local first.**
