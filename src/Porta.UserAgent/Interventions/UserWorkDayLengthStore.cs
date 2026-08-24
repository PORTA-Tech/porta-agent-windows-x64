namespace Porta.UserAgent.Interventions;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

/// <summary>
/// Complaint 2 (2026-08-11) - how long the user's working day actually is, learned from the days
/// already measured rather than asked for. "Do not ask the user how long they intend to work. Ari
/// has been measuring his days for weeks. Adding that question turns a glance into a meeting."
///
/// One number per goal-day: the wall-clock span from the first captured activity to the latest.
/// Today's entry is overwritten as the day grows, so the value left behind at rollover is the full
/// day. The average of the most recent completed days feeds UserCheckInDayTiming.ResolveDayLength,
/// which clamps it - so a single strange day cannot produce a strange schedule.
///
/// Every failure path falls back to "no history", which yields the 8h default. A day length is a
/// convenience, never a correctness requirement: getting it wrong moves the cards, and the evening
/// guard is what stops a wrong day length closing someone's day early.
/// </summary>
internal sealed class UserWorkDayLengthStore
{
    /// <summary>How many completed days feed the average. Short, so a changed routine shows up fast.</summary>
    public const int RecentDays = 5;

    /// <summary>How many days are retained at all. Bounded so the file cannot grow forever.</summary>
    public const int RetainDays = 10;

    private readonly object _gate = new();
    private readonly string _path;

    public UserWorkDayLengthStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? BuildPath() : path;
    }

    /// <summary>
    /// Records (or overwrites) the observed span for one goal-day.
    ///
    /// Chat 2 (2026-08-24) — a day only counts once it has carried real work.
    ///
    /// Every day used to be recorded, including days that were barely worked at all. The average of
    /// the last five then drags the learned day length down, the predicted end moves earlier, and
    /// the wrap-up card creeps forward a little every day — a run of half days teaches Ari that the
    /// user's day is four hours long. Excluding them is not throwing data away: a day nobody worked
    /// is not evidence about how long this person works.
    ///
    /// The gate lives here rather than at the call site so the rule is in one place and testable,
    /// and because "what counts as a day" is the store's own question.
    /// </summary>
    public void Observe(string goalDayKey, TimeSpan span, TimeSpan activeWorkToday)
    {
        if (activeWorkToday < UserCheckInDayTiming.FirstMiddayActiveWorkFloor)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(goalDayKey) || span <= TimeSpan.Zero)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Dictionary<string, double> map = Load();
                map[goalDayKey] = Math.Round(span.TotalMinutes, 1);

                // Keep the newest RetainDays keys. Keys are yyyy-MM-dd goal-day strings, so ordinal
                // descending is chronological descending.
                if (map.Count > RetainDays)
                {
                    map = map.OrderByDescending(pair => pair.Key, StringComparer.Ordinal)
                        .Take(RetainDays)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                }

                Save(map);
            }
            catch (Exception ex)
            {
                // Not silent: a lost write here degrades the card schedule to the 8h default, which
                // is survivable but should be explainable if the cards ever land oddly.
                UserAgentFileLog.Info($"workday_length_write_failed err={ex.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// The mean of the most recent COMPLETED days, or null when there is no history yet. Today is
    /// excluded: a day that is three hours old would otherwise drag the estimate down and pull every
    /// remaining card of that same day earlier.
    /// </summary>
    public TimeSpan? AverageRecent(string excludeGoalDayKey)
    {
        lock (_gate)
        {
            try
            {
                List<double> recent = Load()
                    .Where(pair => !string.Equals(pair.Key, excludeGoalDayKey, StringComparison.Ordinal))
                    .OrderByDescending(pair => pair.Key, StringComparer.Ordinal)
                    .Take(RecentDays)
                    .Select(pair => pair.Value)
                    .Where(minutes => minutes > 0d)
                    .ToList();

                return recent.Count == 0 ? null : TimeSpan.FromMinutes(recent.Average());
            }
            catch
            {
                return null;
            }
        }
    }

    private Dictionary<string, double> Load()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        string json = File.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        return JsonSerializer.Deserialize<Dictionary<string, double>>(json)
            ?? new Dictionary<string, double>(StringComparer.Ordinal);
    }

    private void Save(Dictionary<string, double> map)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(map));
    }

    private static string BuildPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Porta",
        "workday-length.json");
}
