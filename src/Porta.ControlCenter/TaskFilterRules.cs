namespace Porta.ControlCenter;

using Porta.Agent.LocalApi;

/// <summary>
/// Which tasks each tab on the Tasks page shows.
///
/// Chat 4 (2026-08-24) — lifted out of MainWindow so the rules can be tested. They were wrong in a
/// way nobody could see without clicking: the In-progress tab gated on MEASURED HOURS and never
/// looked at the status at all, so a task moved out of Upcoming appeared in neither tab — it could
/// not enter In progress (no hours) and it could not leave Upcoming (the predicate was date-only).
/// Moves to done and blocked landed correctly, which is why it looked like the move itself worked.
///
/// The ordering principle for every predicate here: <b>status first</b>. A status is something the
/// user or the measurement said out loud. Hours are evidence, and they are consulted only for rows
/// written before the status column existed.
/// </summary>
internal static class TaskFilterRules
{
    public const string All = "all";
    public const string InProgress = "prog";
    public const string Blocked = "block";
    public const string Done = "done";
    public const string Upcoming = "upcoming";
    public const string Week = "week";

    /// <summary>Whether one row belongs in the named tab. <paramref name="today"/> is injectable so
    /// the date-relative tabs are testable without waiting for tomorrow.</summary>
    public static bool Matches(string filter, LocalApiTaskRow t, DateTime today) => filter switch
    {
        All => true,

        // Status first. The hours clause survives ONLY for legacy rows carrying no status: it was
        // added when 'in_progress' was "a value almost nothing carries", which stopped being true
        // once time landing on a task promoted it. A row that HAS a status is judged on it.
        InProgress => t.Status == "in_progress" || (HasNoStatus(t) && t.Hours > 0),

        Blocked => t.Status == "blocked",
        Done => t.Status == "done",

        // A task you POSTPONED is the main thing that belongs here, and it was once the one thing
        // this could not see: it read the expected-finish date and never the deferred-until day.
        //
        // It must also LEAVE once it has been moved. Anything the user explicitly moved to another
        // status is no longer upcoming, whatever its dates say.
        Upcoming => t.Status != "done"
                    && t.Status != "in_progress"
                    && t.Status != "blocked"
                    && IsAhead(today, t.DeferredUntilDay, t.ExpectedFinishDay),

        Week => WorkedWithin(t, 7, today) || IsOpen(t, today),
        _ => WorkedWithin(t, 0, today) || IsOpen(t, today)
    };

    /// <summary>
    /// A row written before the status column existed. Only these fall back to judging by measured
    /// hours; anything carrying a status is judged on the status.
    /// </summary>
    public static bool HasNoStatus(LocalApiTaskRow t) => string.IsNullOrWhiteSpace(t.Status);

    /// <summary>Still open: not finished, and not parked for a later day.</summary>
    public static bool IsOpen(LocalApiTaskRow t, DateTime today)
        => t.Status != "done" && !IsAhead(today, t.DeferredUntilDay, t.ExpectedFinishDay);

    public static bool WorkedWithin(LocalApiTaskRow t, int days, DateTime today)
        => DateTime.TryParseExact(
               t.LastWorkedDay, "yyyy-MM-dd",
               System.Globalization.CultureInfo.InvariantCulture,
               System.Globalization.DateTimeStyles.None,
               out DateTime d)
           && (today.Date - d.Date).TotalDays <= days;

    /// <summary>True if either day is set and lands after today. Deferral wins when both are.</summary>
    public static bool IsAhead(DateTime today, params string[] days)
    {
        foreach (string day in days)
        {
            if (DateTime.TryParseExact(
                    day, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime d)
                && d.Date > today.Date)
            {
                return true;
            }
        }

        return false;
    }
}
