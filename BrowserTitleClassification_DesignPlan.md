# Browser Title Classification — Design & Plan

**Goal:** Stop mislabeling browser time as distraction by classifying *what* the browser is showing (from the window title), instead of treating every browser as an unrecognized app. Do it **without** weakening the privacy model: read the title in memory, derive a category, store only the category — never the raw title.

**Status:** Design only. No code written. Prepared from a direct read of the capture pipeline.

---

## 1. Why this is needed (measured)

A read-only diagnostic over ~11h of real usage showed distraction was **51%**, dominated by:

| kind | share | cause |
|---|---|---|
| `focus_category_absent` | **37%** | foreground app isn't Development/Productivity — **browsers are `Unknown`**, so all browser work counts as "no productive app in focus" |
| `return_loop_distraction` | **14%** | editor (Development) ↔ browser (Unknown) cycling flagged as "ping-pong" |

Social media, gaming, passive video were negligible. The inflation is almost entirely **browser time being treated as distraction**.

---

## 2. How capture works today (ground truth)

`Porta.UserAgent/Input/UserSessionNativeSignals.cs` → `TryGetForegroundAppHash()` runs every sampling tick:

```
hwnd        = GetForegroundWindow()
processId   = GetWindowThreadProcessId(hwnd)
[cache by processId, 5-min TTL]                       ← (A) per-process cache
identifier  = ResolveStableAppIdentifier(processId)   → "exe:{path}"   (in memory only)
category    = AppCategoryClassifier.ClassifyStableIdentifier(identifier)
hash        = HMAC-SHA256(installSecret, identifier)  → 16-byte hex    ← raw path discarded
RegisterHashCategory(hash, category)                  ← static hash → category map
return hash
```

- **Privacy model:** the raw exe path is read in memory, hashed, and discarded. Only the **hash** and the **category enum** ever persist. (`AppCategory.cs` docstring: "only the enum value is stored, never the raw exe path.")
- Downstream, `DerivedSessionMetricsComputer` reads the per-bucket dominant hash and calls `AppCategoryClassifier.GetCategoryForHash(hash)` → category → drives `focus_category_absent`, `passive_consumption`, `social_media`, etc., and `IsFocusPositive` (Development/Productivity only).
- `AppCategoryClassifier.ClassifyStableIdentifier` is an exe-name allow-list. **No browser is on it** → every browser → `Unknown`.

### The two structural facts that shape the design
1. **Static identity → category.** Works because an exe is always the same category. A browser is *not* — same exe, many categories. So a browser needs a **per-site identity**, not one identity.
2. **Per-process cache (A).** Same processId returns the same hash for 5 minutes — so even reading the title wouldn't change the category mid-session unless the cache key also changes. The cache must incorporate the site for browsers (or be bypassed for them).

---

## 3. Design

### 3.1 Core idea — site becomes part of the app identity
For browser processes, the in-memory identifier becomes:

```
exe:{browserpath}|site:{siteToken}
```

`siteToken` is a coarse, lowercased keyword derived from the window title (e.g. `youtube`, `github`, `gmail`, `reddit`, or `web` when unrecognized). Then everything downstream works **unchanged**:
- `ClassifyStableIdentifier` classifies by `siteToken`.
- the hash incorporates `siteToken`, so `youtube-in-chrome` and `github-in-chrome` are **distinct hashes with distinct categories**.
- `RegisterHashCategory` / `GetCategoryForHash` work per-site.
- continuity/anchor logic now (correctly) treats GitHub→YouTube as a context switch.

This fits the existing architecture instead of fighting it, and keeps storage to **hash + category** only.

### 3.2 Privacy — unchanged model, deliberately
- The title is read into a local string, scanned for a site token, and **discarded**. It is never written to disk, telemetry, or the DB.
- Only `siteToken` (a coarse keyword like `youtube`) feeds the **HMAC hash** (irreversible) and the **category enum**. Neither the title nor the token is stored in the clear.
- Titles are read **only for browser foreground windows**, not all apps.
- **Recommended:** put this behind an explicit setting/consent ("Improve accuracy by reading browser tab titles locally — titles are never stored or sent"), default per your policy, and update the privacy copy in the README/onboarding. This is a posture change worth being loud about even though storage stays category-only.

### 3.3 Title → site token (classification)
Browser tab titles usually look like `… - YouTube - Google Chrome`, `… - Gmail`, `owner/repo - GitHub`, `… - Stack Overflow`. Major sites brand their titles; arbitrary sites and `localhost` often don't, and **Chrome titles do not contain the domain/URL**. So:
- Maintain a keyword → (siteToken, category) table for high-signal sites:
  - Development/Productivity: `github`, `gitlab`, `stack overflow`, `localhost`, `docs.google`/`google docs`, `notion`, `figma`, `jira`, `confluence`, `linear`, `vercel`, `aws`, `azure`, `mdn`, `npm`, docs domains…
  - Entertainment: `youtube`, `netflix`, `twitch`, `disney`, `spotify` (web), `hulu`…
  - SocialMedia: `twitter`/`/ x`, `reddit`, `instagram`, `facebook`, `tiktok`, `linkedin` (note: currently Productivity — keep consistent), `threads`…
  - Communication: `gmail`, `outlook`, `slack`, `discord` (web), `whatsapp web`, `messenger`…
- **Unrecognized title → `web` → Neutral** (see 3.4). We never guess "distraction" from an unknown title.
- Matching is substring/lowercase on the title with a small ordered priority list; tuned conservatively (false "distraction" is worse than false "neutral").

### 3.4 Make "unknown" neutral, not distraction (the real bug fix)
Independent of titles, fix the over-aggressive logic so unrecognized time stops counting as distraction:
- **`focus_category_absent`** (`DerivedSessionMetricsComputer`): today it accumulates for *anything* not Development/Productivity. Change it to accumulate only for **known-distracting** categories (Entertainment, SocialMedia, Gaming, GamingLauncher) — **Unknown and Communication are neutral** (don't add to focus-absence, don't add to focus).
- **`return_loop_distraction`**: suppress when **either** side of the ping-pong is a focus-positive category (editor↔browser-while-working isn't distraction). Alternatively require both sides to be non-focus-positive.
- Introduce an explicit **Neutral** notion (either a new `AppCategory.Neutral`, or treat `Unknown` as neutral) so the three states are clear: focus-positive / distracting / neutral.

These two changes alone would have cut the measured distraction from 51% toward ~14–20%; title classification then *reassigns* the neutral browser time correctly (some to focus, some to genuine distraction like YouTube).

---

## 4. Integration points (exact)

| File | Change |
|---|---|
| `Porta.UserAgent/Input/UserSessionNativeSignals.cs` | Detect browser by exe name; for browsers read foreground title, derive `siteToken`, build `exe:…\|site:…` identifier; key the cache on (processId + siteToken) or bypass cache for browsers (title read is cheap). |
| `Porta.UserAgent/Input/NativeMethods` (same file's P/Invoke block) | Add `GetWindowText` / `GetWindowTextLength` P/Invoke (read-only, bounded buffer). |
| `Porta.Agent.Common/Analysis/AppCategory.cs` | Add browser detection helper + `ClassifySite(siteToken)`; extend `ClassifyStableIdentifier` to parse the `site:` segment; add `Neutral` handling. |
| `Porta.Agent.Core/Analysis/DerivedSessionMetricsComputer.cs` | `focus_category_absent` accumulates only for known-distracting categories; `return_loop_distraction` suppressed when a side is focus-positive. |
| `Porta.Agent.Runtime/Input/FocusIdentityNormalizer.cs` | Mirror the browser identity logic if the Runtime path is also live (it has a parallel `ComputeAppHash`). Verify which path actually feeds production buckets and keep them consistent. |
| Settings / onboarding / README | Consent toggle + privacy-copy update. |
| Tests | New `BrowserTitleClassifier` unit tests; `DerivedSessionMetricsComputer` tests for neutral-Unknown and ping-pong suppression. |

> Open verification item: confirm whether `UserSessionNativeSignals` (UserAgent) or `FocusIdentityNormalizer` (Runtime) — or both — produces the hashes that reach `state_buckets` in the shipping configuration, so the title logic goes in the right place and isn't duplicated inconsistently.

---

## 5. Edge cases & risks
- **Titles without a known site** (localhost, intranet, random blogs) → Neutral. Acceptable; never mislabeled as distraction.
- **Incognito/private windows** — still produce titles; same in-memory-only handling. Consider honoring private windows as Neutral by policy.
- **Localization** — site brands in titles are usually latin (YouTube, GitHub); keyword table should be case-insensitive and accent-tolerant.
- **Performance** — `GetWindowText` on the foreground window per tick is negligible; bounded buffer (e.g. 256 chars).
- **Cache change risk** — moving the browser cache key from processId to (processId+site) is the most behaviorally sensitive change; cover with tests.
- **Privacy regression risk** — the one thing we must never do is persist or transmit the title. Add a test/assert that only hash+category leave the classifier.
- **Continuity churn** — treating each site as a distinct identity will create more context switches; verify it doesn't destabilize focus episodes (interacts with the continuity engine, which already has failing tests — tread carefully and gate).

---

## 6. Suggested build sequence (each build-gated)
1. **Distraction de-escalation (no titles):** make Unknown/Communication neutral in `focus_category_absent`; suppress `return_loop_distraction` when a side is focus-positive. Pure agent logic, net8.0, testable. Biggest immediate truth gain, lowest risk. *(Could ship first even on its own.)*
2. **Win32 + classifier:** add `GetWindowText` P/Invoke and a `BrowserTitleClassifier` (Common) with the keyword table + unit tests. No pipeline wiring yet.
3. **Wire browser identity:** browser detection + `exe:…|site:…` identifier + cache-key change in the capture path; register per-site category. Verify on-machine that sites classify correctly.
4. **Consent + privacy copy.**
5. **Tune** the keyword table against real usage; add a private/incognito policy.

---

## 7. Relationship to prior work
This is a **new workstream** (agent *classification accuracy*), separate from the already-shipped, verified distraction **display** fix (kind consistency + drift guard). The display fix correctly surfaced the data; this work makes the underlying data fair. Step 1 above is the fastest path to your dashboard feeling truthful again.

*Prepared from a direct read of the source. No code modified.*
