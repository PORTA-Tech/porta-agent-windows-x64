namespace Porta.Agent.Common;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// How the parsed time was expressed: a relative duration ("in 20m", "1h30"), an absolute
/// clock time ("at 16:30", "tomorrow 9am"), or not expressed at all. The compose UI uses
/// this to pre-select the matching picker state (duration → chips/stepper, clock → clock
/// entry) so what the user typed and what the picker shows never disagree.
/// </summary>
public enum ReminderTimeKind
{
    None,
    Duration,
    Clock
}

/// <summary>
/// Phase 2c — parses the reminder quick-add line ("standup in 20m", "ship build at 16:30",
/// "call kamal tomorrow 9am") into a title + due time. PURE and deliberately minimal: a
/// fixed grammar, nothing clever. Anything without a recognizable time yields
/// <see cref="ReminderParseResult.TimeRecognized"/> = false — such results are NEVER
/// schedulable; the widget requires an explicit picker selection before confirm. The
/// fallback <see cref="ReminderParseResult.DueAt"/> exists only so previews have a value
/// to render, never as something to schedule.
/// </summary>
public readonly record struct ReminderParseResult(
    string Title,
    DateTimeOffset DueAt,        // same offset/kind as the "now" passed in (local time expected)
    string PreviewLabel,         // "in 20m" | "in 1h30m" | "at 16:30" | "tomorrow 09:00"
    bool TimeRecognized,
    ReminderTimeKind Kind,
    int DurationMinutes);        // > 0 only when Kind == Duration

public static class ReminderTimeParser
{
    private const int DefaultMinutes = 20;

    // Combined hours+minutes must run before the bare-hours/bare-minutes forms, otherwise
    // "1h30m" would match "1h" alone and silently drop the 30. "in" is optional everywhere:
    // "in 1h30", "1h30m", "2 hours 15", "in 1 hour 30".
    private static readonly Regex HoursAndMinutes = new(
        @"\b(?:in\s+)?(\d{1,2})\s*h(?:rs?|ours?)?\s*(\d{1,2})(?:\s*m(?:in(?:ute)?s?)?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "20m", "in 20m", "90min", "in 45 minutes".
    private static readonly Regex Minutes = new(
        @"\b(?:in\s+)?(\d{1,3})\s*m(?:in(?:ute)?s?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "1h", "in 2h", "2 hours", "in 2 hours", "3hrs".
    private static readonly Regex Hours = new(
        @"\b(?:in\s+)?(\d{1,2})\s*h(?:rs?|ours?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "an hour", "one hour", "half an hour", "half hour" — with optional leading "in".
    private static readonly Regex WordHours = new(
        @"\b(?:in\s+)?(?:(?<one>an|one)\s+hour|(?<half>half)\s+(?:an\s+)?hour)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AtClock = new(
        @"\bat\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tomorrow = new(
        @"\btomorrow(?:\s+(\d{1,2})(?::(\d{2}))?\s*(am|pm)?)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses <paramref name="input"/> relative to <paramref name="now"/> (pass local time).</summary>
    public static ReminderParseResult Parse(string? input, DateTimeOffset now)
    {
        string text = (input ?? string.Empty).Trim();

        Match m = HoursAndMinutes.Match(text);
        if (m.Success
            && int.TryParse(m.Groups[1].Value, out int comboHours)
            && int.TryParse(m.Groups[2].Value, out int comboMinutes)
            && (comboHours > 0 || comboMinutes > 0)
            && comboMinutes < 60)
        {
            int total = (comboHours * 60) + comboMinutes;
            return BuildDuration(text, m, now, total);
        }

        m = Minutes.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int minutes) && minutes > 0)
        {
            return BuildDuration(text, m, now, minutes);
        }

        m = Hours.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out int hours) && hours > 0)
        {
            return BuildDuration(text, m, now, hours * 60);
        }

        m = WordHours.Match(text);
        if (m.Success)
        {
            return BuildDuration(text, m, now, m.Groups["half"].Success ? 30 : 60);
        }

        m = Tomorrow.Match(text);
        if (m.Success)
        {
            (int hour, int minute, bool valid) = ResolveClock(
                m.Groups[1], m.Groups[2], m.Groups[3], defaultHour: 9);
            if (valid)
            {
                DateTimeOffset due = new DateTimeOffset(
                    now.Year, now.Month, now.Day, hour, minute, 0, now.Offset).AddDays(1);
                return Build(text, m, due, $"tomorrow {hour:00}:{minute:00}", ReminderTimeKind.Clock);
            }
        }

        m = AtClock.Match(text);
        if (m.Success)
        {
            (int hour, int minute, bool valid) = ResolveClock(
                m.Groups[1], m.Groups[2], m.Groups[3], defaultHour: -1);
            // Bare "at 7" with no minutes and no am/pm is too ambiguous to accept silently —
            // require ":mm" or am/pm; otherwise fall through to the unrecognized result.
            bool precise = m.Groups[2].Success || m.Groups[3].Success;
            if (valid && precise)
            {
                DateTimeOffset due = new(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
                if (due <= now)
                {
                    due = due.AddDays(1); // that time already passed today → tomorrow
                }

                return Build(text, m, due, $"at {hour:00}:{minute:00}", ReminderTimeKind.Clock);
            }
        }

        // No recognizable time. The DueAt here is a PREVIEW placeholder only — callers must
        // never schedule a TimeRecognized=false result (the compose UI blocks confirm).
        return new ReminderParseResult(
            Title: NormalizeTitle(text),
            DueAt: now.AddMinutes(DefaultMinutes),
            PreviewLabel: $"in {DefaultMinutes}m",
            TimeRecognized: false,
            Kind: ReminderTimeKind.None,
            DurationMinutes: 0);
    }

    /// <summary>Formats a duration the same way parsed previews do: "20m", "1h", "1h30m".</summary>
    public static string FormatDuration(int totalMinutes)
    {
        int safe = Math.Max(0, totalMinutes);
        int h = safe / 60;
        int min = safe % 60;
        return h > 0
            ? (min > 0 ? $"{h}h{min}m" : $"{h}h")
            : $"{min}m";
    }

    private static ReminderParseResult BuildDuration(string text, Match match, DateTimeOffset now, int totalMinutes)
    {
        string title = NormalizeTitle(text.Remove(match.Index, match.Length));
        return new ReminderParseResult(
            Title: title,
            DueAt: now.AddMinutes(totalMinutes),
            PreviewLabel: $"in {FormatDuration(totalMinutes)}",
            TimeRecognized: true,
            Kind: ReminderTimeKind.Duration,
            DurationMinutes: totalMinutes);
    }

    private static (int Hour, int Minute, bool Valid) ResolveClock(
        Group hourGroup, Group minuteGroup, Group amPmGroup, int defaultHour)
    {
        if (!hourGroup.Success)
        {
            return defaultHour >= 0 ? (defaultHour, 0, true) : (0, 0, false);
        }

        if (!int.TryParse(hourGroup.Value, out int hour))
        {
            return (0, 0, false);
        }

        int minute = 0;
        if (minuteGroup.Success && (!int.TryParse(minuteGroup.Value, out minute) || minute > 59))
        {
            return (0, 0, false);
        }

        if (amPmGroup.Success)
        {
            if (hour is < 1 or > 12)
            {
                return (0, 0, false);
            }

            bool pm = amPmGroup.Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            hour = (hour % 12) + (pm ? 12 : 0);
        }
        else if (hour > 23)
        {
            return (0, 0, false);
        }

        return (hour, minute, true);
    }

    private static ReminderParseResult Build(
        string text, Match match, DateTimeOffset due, string previewLabel, ReminderTimeKind kind)
    {
        string title = NormalizeTitle(text.Remove(match.Index, match.Length));
        return new ReminderParseResult(title, due, previewLabel, TimeRecognized: true, Kind: kind, DurationMinutes: 0);
    }

    private static string NormalizeTitle(string raw)
    {
        string collapsed = Regex.Replace(raw, @"\s+", " ").Trim().Trim(',', '-', '·').Trim();
        if (collapsed.Length == 0)
        {
            return "Reminder";
        }

        // Sentence-case the first letter for a tidy pill; leave the rest as typed.
        return char.ToUpper(collapsed[0], CultureInfo.CurrentCulture) + collapsed[1..];
    }
}
