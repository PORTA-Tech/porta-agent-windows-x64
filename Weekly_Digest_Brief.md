# Brief — the weekly digest

Read first: `COWORK_START_HERE.md`, `Chat8_Decisions_2026-08-25.md`.
Open in a browser, do not read the source: `Ari_Weekly_Digest_Mockup.html` at the agent repo root.
That mockup is agreed. Build to it exactly. It shows phone and desktop from the same markup;
both must hold.

This is `porta-control-plane`. Pushing deploys. You have my go for this work and nothing else
in that repo.

## What exists

`src/core/digestEmailBuilder.ts` builds the daily digest. `digestCronService.ts` fires once at
20:30 UTC for a single date. There is no weekly anything — no stub, no flag, no cadence concept.
The word "weekly" appears nowhere in `src/`.

## What to build

A weekly digest: its own payload, its own builder, its own send. Sent once a week covering the
seven goal days that ended. Reuse the daily's design values and helpers rather than copying them —
the green rule holds, `#3FBF7F` means work that counted and nothing else.

Sections, in order, exactly as the mockup:

1. Headline resume — planned and finished across the week, scored through the same rule as
   `UserCheckInTaskBoard.SummarizeDay` and the daily. If the weekly and the daily can disagree
   about what "finished" means, that is the bug this section exists to avoid.
2. **The week** — one bar per day, filled to focused time. A day not worked renders as an empty
   track, never a zero-height bar. Below three worked days, replace the strip with a sentence,
   the same rule the client report follows.
3. **What you finished** — closed tasks with project and measured time.
4. **What carried** — still-open tasks with project, measured time, and how many days each has
   been open. The days-open number is the point of the block.
5. **The numbers** — worked, focused, best day, away.
6. **Where the week went** — by project, not by app. Apps stay on the daily. Untagged time is
   not shown and is not implied.
7. **Ari's take** — same serif voice as the daily, drafted from result lines and statuses only,
   never from app usage.
8. Open Ari, then the footer.

## Non-negotiable

- Every column states its width in an inline style as well as the attribute, and every bar
  carries its proportion inline. This is the failure fixed on the daily on 25 August: Gmail iOS
  drops attributes and ignores `table-layout:fixed`, and bars whose proportion lived only in the
  attribute rendered at exactly half regardless of the number beside them.
- No pie chart. Nothing depends on colour alone.
- A metric whose source had no data says so. Never a confident zero for a broken sensor.

## Open question for you to answer, not to decide

When does it send, and does the daily still go out on the same evening? Tell me your
recommendation with the reasoning and stop. I will decide.

## Prove it

Render the weekly at 390px and at 600px, both with the `<style>` block stripped entirely so no
media query can save it — the Gmail iOS worst case, same harness you used this afternoon. Show me
both before pushing. Run the full control plane test suite. Then push, deploy, and confirm off
`/health` with the commit and the time.
