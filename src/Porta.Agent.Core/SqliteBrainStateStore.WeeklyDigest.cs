using System.Globalization;
using Microsoft.Data.Sqlite;
using Porta.Agent.Core.Db;

namespace Porta.Agent.Core;

/// <summary>
/// The weekly digest's read side. Separate file, same partial class, because it is one query with
/// a lot of rules and none of them belong in the middle of the report draft.
///
/// <b>Hours come from <c>task_day_hours</c>, never from <c>app_usage_rollup</c>.</b> The rollup
/// returns early on a bucket with no identifiable application, so it is lossy by an unmeasured
/// amount — that loss is already visible as "unattributed" on the Reports page. task_day_hours
/// records the same buckets with no app condition, in the same transaction as the attribution, so
/// it cannot disagree with <c>tasks.active_seconds</c>. A weekly built on the rollup would report
/// fewer hours than the widget showed all week.
///
/// <b>Days are GOAL days, not calendar days.</b> <c>task_day_hours.local_date</c> is written from
/// <c>AgentCoreController.GoalDay</c>, which rolls at 05:00 local. Work at 01:00 on Tuesday belongs
/// to Monday, here and everywhere else in the product.
/// </summary>
public sealed partial class SqliteBrainStateStore
{
    /// <summary>At most this many lines in each block. The COUNTS are still the whole week.</summary>
    private const int MaxWeeklyLinesPerBlock = 8;

    /// <summary>
    /// Reads the task side of the weekly digest for the goal-day range [fromLocalDate, toLocalDate].
    /// Both bounds are inclusive and both are goal days in "yyyy-MM-dd".
    /// </summary>
    public AgentWeeklyDigestData ReadWeeklyDigestData(
        string fromLocalDate,
        string toLocalDate,
        TimeSpan localOffset)
    {
        if (!DateTime.TryParseExact(
                fromLocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime from) ||
            !DateTime.TryParseExact(
                toLocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime to) ||
            to < from)
        {
            return AgentWeeklyDigestData.Empty;
        }

        // Seconds measured against each task INSIDE the week.
        var secondsByTask = new Dictionary<long, double>();
        // Everything else each task needs, read once.
        var tasks = new List<WeeklyTaskFacts>();

        using (ReadGateLease _ = AcquireReadGate())
        using (var connection = DbOpen.OpenReadConnection(_connectionString))
        {
            using (var hours = connection.CreateCommand())
            {
                hours.CommandText =
                    """
                    SELECT task_id, SUM(active_seconds)
                    FROM task_day_hours
                    WHERE local_date >= $from AND local_date <= $to
                    GROUP BY task_id;
                    """;
                hours.Parameters.AddWithValue("$from", fromLocalDate);
                hours.Parameters.AddWithValue("$to", toLocalDate);

                using SqliteDataReader reader = hours.ExecuteReader();
                while (reader.Read())
                {
                    long taskId = reader.GetInt64(0);
                    double seconds = reader.IsDBNull(1) ? 0d : reader.GetDouble(1);
                    if (taskId != 0 && seconds > 0d)
                    {
                        secondsByTask[taskId] = seconds;
                    }
                }
            }

            using (var cmd = connection.CreateCommand())
            {
                // Every task that existed by the end of the week. The windowing is done in C#
                // because it turns on the 05:00 goal-day boundary, which SQL here cannot express
                // without repeating the rule — and a second copy of that rule is how the strip and
                // the task list would start disagreeing about which day something happened on.
                cmd.CommandText =
                    """
                    SELECT t.id, t.title, t.state, t.created_at_utc, t.completed_at_utc, t.ended_at_utc,
                           COALESCE(p.name, '')
                    FROM tasks t
                    LEFT JOIN projects p ON p.id = t.project_id
                    WHERE t.created_at_utc < $createdBefore;
                    """;
                // Exclusive upper bound: the instant after the week's last goal day ends.
                DateTimeOffset weekEndExclusive = new DateTimeOffset(to.AddDays(1), localOffset).AddHours(5);
                cmd.Parameters.AddWithValue("$createdBefore", weekEndExclusive.ToUniversalTime().ToString("O"));

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tasks.Add(new WeeklyTaskFacts(
                        Id: reader.GetInt64(0),
                        Title: reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        State: reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                        CreatedAtUtc: reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                        CompletedAtUtc: reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        EndedAtUtc: reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                        ProjectName: reader.IsDBNull(6) ? string.Empty : reader.GetString(6)));
                }
            }
        }

        return Assemble(tasks, secondsByTask, fromLocalDate, toLocalDate, to, localOffset);
    }

    // internal, not private: Assemble is internal so the rules below are unit-testable
    // without a database, and its parameter cannot be less accessible than it is.
    internal readonly record struct WeeklyTaskFacts(
        long Id, string Title, string State, string CreatedAtUtc, string CompletedAtUtc,
        string EndedAtUtc, string ProjectName);

    /// <summary>
    /// Pure, so every rule below is unit-testable without a database.
    /// </summary>
    internal static AgentWeeklyDigestData Assemble(
        IReadOnlyList<WeeklyTaskFacts> tasks,
        IReadOnlyDictionary<long, double> secondsByTask,
        string fromLocalDate,
        string toLocalDate,
        DateTime weekEndDate,
        TimeSpan localOffset)
    {
        var finished = new List<(AgentWeeklyTaskLine Line, double Seconds)>();
        var carried = new List<(AgentWeeklyTaskLine Line, double Seconds)>();
        var projectSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        int planned = 0;
        int finishedCount = 0;

        foreach (WeeklyTaskFacts t in tasks)
        {
            double seconds = secondsByTask.TryGetValue(t.Id, out double s) ? s : 0d;

            string? createdDay = GoalDayOf(t.CreatedAtUtc, localOffset);
            // completed_at_utc is the field the Tasks page writes; ended_at_utc is what the older
            // close path wrote. Prefer the former, fall back rather than lose a closure.
            string? closedDay = GoalDayOf(
                t.CompletedAtUtc.Length > 0 ? t.CompletedAtUtc : t.EndedAtUtc, localOffset);

            bool isDone = string.Equals(t.State, "done", StringComparison.Ordinal);
            bool workedThisWeek = seconds > 0d;
            bool createdThisWeek = Within(createdDay, fromLocalDate, toLocalDate);
            bool closedThisWeek = isDone && Within(closedDay, fromLocalDate, toLocalDate);

            // A task belongs to this week if the week touched it in any way: it was worked, it was
            // added, or it was closed. A task created months ago, untouched all week and still
            // open, is NOT part of this week's board — counting it would make every week's "planned"
            // grow forever and the score meaningless.
            if (!workedThisWeek && !createdThisWeek && !closedThisWeek)
            {
                continue;
            }

            planned++;

            if (closedThisWeek)
            {
                finishedCount++;
                finished.Add((new AgentWeeklyTaskLine(t.Title, t.ProjectName, seconds, 0), seconds));
            }
            else if (!isDone)
            {
                // Still open at the end of the week. Days open counts from the day it was added,
                // not from Monday: a task that has been open for three weeks should say so.
                int daysOpen = DaysOpen(createdDay, weekEndDate);
                carried.Add((new AgentWeeklyTaskLine(t.Title, t.ProjectName, seconds, daysOpen), seconds));
            }

            // Untagged work is counted in the totals and shown on its own line, but it is never a
            // project row: "where the week went" is a ranking of the projects, not an accounting of
            // every minute, and an "Untagged" bar would read as a project called Untagged.
            if (seconds > 0d && t.ProjectName.Trim().Length > 0)
            {
                string name = t.ProjectName.Trim();
                projectSeconds[name] = projectSeconds.TryGetValue(name, out double existing)
                    ? existing + seconds
                    : seconds;
            }
        }

        return new AgentWeeklyDigestData(
            PlannedCount: planned,
            FinishedCount: finishedCount,
            Finished: Top(finished),
            Carried: Top(carried),
            Projects: projectSeconds
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new AgentWeeklyProjectLine(kv.Key, kv.Value))
                .ToList());
    }

    /// <summary>Longest first — the biggest piece of work is the one worth reading first.</summary>
    private static IReadOnlyList<AgentWeeklyTaskLine> Top(List<(AgentWeeklyTaskLine Line, double Seconds)> lines)
        => lines
            .OrderByDescending(x => x.Seconds)
            .ThenBy(x => x.Line.Title, StringComparer.Ordinal)
            .Take(MaxWeeklyLinesPerBlock)
            .Select(x => x.Line)
            .ToList();

    /// <summary>The goal day an ISO-8601 UTC stamp falls on, or null when it is absent or unparsable.</summary>
    private static string? GoalDayOf(string utcStamp, TimeSpan localOffset)
    {
        if (string.IsNullOrWhiteSpace(utcStamp)
            || !DateTimeOffset.TryParse(
                utcStamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            return null;
        }

        DateTimeOffset local = parsed.ToOffset(localOffset);
        return (local.Hour < 5 ? local.AddDays(-1) : local).ToString("yyyy-MM-dd");
    }

    /// <summary>Inclusive on both ends. Ordinal compare is safe: these are all yyyy-MM-dd.</summary>
    private static bool Within(string? day, string fromLocalDate, string toLocalDate)
        => day is not null
           && string.CompareOrdinal(day, fromLocalDate) >= 0
           && string.CompareOrdinal(day, toLocalDate) <= 0;

    private static int DaysOpen(string? createdDay, DateTime weekEndDate)
    {
        if (createdDay is null
            || !DateTime.TryParseExact(
                createdDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime created))
        {
            return 1;
        }

        // Inclusive of both the day it opened and the day the week ended: a task added on the
        // Sunday and still open on the Sunday has been open one day, not zero.
        int days = (int)(weekEndDate.Date - created.Date).TotalDays + 1;
        return days < 1 ? 1 : days;
    }
}
