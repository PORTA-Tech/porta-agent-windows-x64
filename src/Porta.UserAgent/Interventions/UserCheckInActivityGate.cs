namespace Porta.UserAgent.Interventions;

// Part 1c — settle-in gate. A check-in must not pop the instant the machine/session resumes; the
// user has to be continuously active for a few minutes first. This is the pure state machine behind
// that: the host feeds it each poll's activity signal and it tracks a "continuously-active-since"
// timestamp, resetting on idle/away so a fresh window is required after every resume.
//
// Fail-soft: an UNKNOWN activity signal (live session momentarily unavailable) neither starts nor
// resets the window — it leaves the existing state untouched. And WithinSettleWindow only ever
// BLOCKS when we positively know we are inside a fresh active window; when the timestamp is unset it
// does not block, so a missing signal degrades toward the old "fire on activity" behavior rather
// than wedging a check-in permanently.
internal static class UserCheckInActivityGate
{
    // A check-in only fires after this much continuous activity since the last resume.
    public static readonly TimeSpan SettleInDelay = TimeSpan.FromMinutes(3);

    // Tri-state activity for the current poll: true = clearly active, false = clearly idle/away,
    // null = unknown (signal unavailable this tick).
    public static DateTimeOffset? Advance(
        DateTimeOffset? activeSinceUtc,
        DateTimeOffset nowUtc,
        bool? activeNow)
    {
        return activeNow switch
        {
            true => activeSinceUtc ?? nowUtc,   // start counting from the first active tick
            false => null,                       // idle/away resets — re-settle on return
            null => activeSinceUtc,               // unknown: leave state untouched (fail-soft)
        };
    }

    // True only when we KNOW we are still inside the fresh-activity settle window and a check-in
    // should be held back. Unset timestamp => do not block (fail-soft toward firing).
    public static bool WithinSettleWindow(
        DateTimeOffset? activeSinceUtc,
        DateTimeOffset nowUtc,
        TimeSpan delay)
    {
        return activeSinceUtc is { } since && (nowUtc - since) < delay;
    }
}
